using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.SelfHealing;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.Variables;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.Executions;
using AutoTestAi.Domain.TestCases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.TestExecution;

/// <summary>Prepare outcome: runnable with a worker assignment, or a skip reason
/// when the execution was already reconciled (cancelled/terminal/missing data).</summary>
public sealed record PreparedExecution(
    Guid ExecutionId,
    Guid ExecutionTestId,
    bool CanRun,
    string? SkipReason,
    WorkerAssignmentDto? Assignment,
    Guid? AssignmentToken);

/// <summary>Normalized worker outcome (functional results are returned, never thrown).</summary>
public sealed record WorkerExecutionOutcome(
    ExecutionTestStatus Status,
    FailureClassification Classification,
    string? ErrorType,
    string? ErrorMessage,
    long DurationMs,
    IReadOnlyList<WorkerStepResultDto> Steps,
    IReadOnlyList<WorkerLogDto> Logs,
    IReadOnlyList<WorkerScreenshotDto> Screenshots,
    int Attempt,
    IReadOnlyList<WorkerHealingAttemptDto>? HealingAttempts = null,
    IReadOnlyList<WorkerPageSourceDto>? PageSources = null,
    IReadOnlyList<WorkerServerLogDto>? ServerLogs = null);

/// <summary>
/// Workflow-facing execution engine (Slice 5 §10-11). Temporal activities are
/// thin wrappers over this Temporal-free orchestrator, so every path is
/// unit-testable with fakes. Idempotent terminal finalization: exactly one
/// terminal state wins (§60).
/// </summary>
public interface IExecutionEngine
{
    Task<PreparedExecution> PrepareAsync(Guid executionId, CancellationToken ct);
    Task<WorkerExecutionOutcome> RunWorkerAsync(Guid executionId, Func<Task>? heartbeatAsync, CancellationToken ct);
    Task PersistResultAsync(Guid executionId, WorkerExecutionOutcome outcome, CancellationToken ct);
    Task FinalizeCancelledAsync(Guid executionId, string reason, CancellationToken ct);
    Task FinalizeTimedOutAsync(Guid executionId, string reason, CancellationToken ct);
    Task FinalizeErrorAsync(Guid executionId, string reason, CancellationToken ct);
}

public sealed class ExecutionEngine : IExecutionEngine
{
    private const int MaxErrorLength = 4000;
    private const int MaxAttempts = 2; // one controlled retry for infrastructure failures only

    private readonly IExecutionStore _store;
    private readonly ITestCaseStore _cases;
    private readonly IPlaywrightWorkerClient _worker;
    private readonly IExecutionEventPublisher _events;
    private readonly IArtifactStorage _artifacts;
    private readonly IOptions<ExecutionOptions> _options;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly IGridLeaseManager? _leases;
    private readonly IGridAssignmentStore? _assignments;
    private readonly ISelfHealingPolicyStore? _healingPolicies;
    private readonly ISelfHealingService? _healing;
    private readonly IVariableResolutionService? _varResolver;
    private readonly IExecutionVariablesStore? _envelopes;
    private readonly IMobileExecutionCoordinator? _mobile;
    private readonly Mobile.IMobileSessionService? _mobileSessions;
    private readonly Visual.IVisualComparisonService? _visual;
    private readonly ILogger<ExecutionEngine> _logger;

    public ExecutionEngine(
        IExecutionStore store,
        ITestCaseStore cases,
        IPlaywrightWorkerClient worker,
        IExecutionEventPublisher events,
        IArtifactStorage artifacts,
        IOptions<ExecutionOptions> options,
        IDateTimeProvider clock,
        IAuditService audit,
        ILogger<ExecutionEngine> logger,
        IGridLeaseManager? leases = null,
        IGridAssignmentStore? assignments = null,
        ISelfHealingPolicyStore? healingPolicies = null,
        ISelfHealingService? healing = null,
        IVariableResolutionService? varResolver = null,
        IExecutionVariablesStore? envelopes = null,
        IMobileExecutionCoordinator? mobileCoordinator = null,
        Mobile.IMobileSessionService? mobileSessions = null,
        Visual.IVisualComparisonService? visualComparison = null)
    {
        _store = store;
        _cases = cases;
        _worker = worker;
        _events = events;
        _artifacts = artifacts;
        _options = options;
        _clock = clock;
        _audit = audit;
        _leases = leases;
        _assignments = assignments;
        _healingPolicies = healingPolicies;
        _healing = healing;
        _varResolver = varResolver;
        _envelopes = envelopes;
        _mobile = mobileCoordinator;
        _mobileSessions = mobileSessions;
        _visual = visualComparison;
        _logger = logger;
    }

    // ---------- prepare ----------

    public async Task<PreparedExecution> PrepareAsync(Guid executionId, CancellationToken ct)
    {
        var (execution, test) = await LoadAsync(executionId, ct);
        if (ExecutionTransitions.IsTestTerminal(test.Status))
            return new PreparedExecution(executionId, test.Id, false, $"already {test.Status}", null, null);
        if (test.Status != ExecutionTestStatus.Queued)
            return new PreparedExecution(executionId, test.Id, false, $"unexpected status {test.Status}", null, null);

        // Re-validate the exact bound version (fail-safe if it vanished or left Approved).
        var version = test.TestCaseVersionId is not null
            ? await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, ct)
            : null;
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        if (version is null || testCase is null || testCase.ProjectId != execution.ProjectId)
        {
            await FailSafeAsync(execution, test,
                "The bound test case version is no longer available.", ct);
            return new PreparedExecution(executionId, test.Id, false, "version unavailable", null, null);
        }
        if (version.ReviewStatus != ReviewStatus.Approved)
        {
            await FailSafeAsync(execution, test,
                $"Version {version.VersionNumber} is no longer Approved (now {version.ReviewStatus}).", ct);
            return new PreparedExecution(executionId, test.Id, false, "version not approved", null, null);
        }

        var now = _clock.UtcNow;
        test.Status = ExecutionTestStatus.Running;
        test.UpdatedAt = now;
        execution.Status = ExecutionStatus.Running;
        execution.StartedAt = now;
        execution.UpdatedAt = now;
        // Capture the assignment ID that started this execution for fencing
        if (test.AssignmentId != test.StartedAssignmentId)
        {
            test.StartedAssignmentId = test.AssignmentId;
        }
        await _store.SaveChangesAsync(ct);

        await _audit.RecordAsync("execution.started", "execution",
            execution.Id.ToString(), execution.ProjectId,
            SafeMeta(execution, "started"), ct);
        await PublishStatusAsync(execution, ct);
        await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStarted,
            new { executionId = execution.Id, status = execution.Status.ToString() }, ct);
        await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionTestStarted,
            new { executionId = execution.Id, executionTestId = test.Id, attempt = test.Attempt }, ct);

        // Prepare returns an assignment for diagnostics only — the workflow never
        // forwards it to the worker (RunWorker rebuilds inside its own activity).
        // Secret-derived values are masked here so Temporal history never sees
        // plaintext even though this DTO is persisted as the activity result.
        return new PreparedExecution(executionId, test.Id, true, null,
            await BuildAssignmentAsync(execution, test, testCase, version, ct, redactSecrets: true), null);
    }

    // ---------- run ----------

    public async Task<WorkerExecutionOutcome> RunWorkerAsync(
        Guid executionId, Func<Task>? heartbeatAsync, CancellationToken ct)
    {
        var (execution, test) = await LoadAsync(executionId, ct);
        if (ExecutionTransitions.IsTestTerminal(test.Status))
            return OutcomeFromPersisted(test, test.Attempt);
        if (test.Status != ExecutionTestStatus.Running)
            throw new InvalidOperationException($"Execution test {test.Id} is {test.Status}, not Running.");

        // Slice 3C-4B-2: explicit framework branch. Playwright flows below
        // are byte-identical; appium executions run the mobile coordinator.
        if (string.Equals(test.Framework, "appium", StringComparison.OrdinalIgnoreCase))
        {
            if (_mobile is null)
                return new WorkerExecutionOutcome(
                    ExecutionTestStatus.Error, FailureClassification.AutomationFailure,
                    nameof(WorkerInfrastructureException), "Mobile execution is not configured.",
                    0,
                    Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
                    Array.Empty<WorkerScreenshotDto>(), test.Attempt);
            return await _mobile.RunMobileAsync(executionId, heartbeatAsync, ct);
        }

        WorkerInfrastructureException? lastInfraError = null;
        var total = Stopwatch.StartNew();
        for (var attempt = test.Attempt; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > test.Attempt)
            {
                // Controlled infra retry: fresh attempt rows, attempt counter, live event.
                await ClearAttemptRowsAsync(test.Id, ct);
                test.Attempt = attempt;
                test.UpdatedAt = _clock.UtcNow;
                await _store.SaveChangesAsync(ct);
                await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionTestStarted,
                    new { executionId = execution.Id, executionTestId = test.Id, attempt }, ct);
            }

            WorkerAssignmentDto assignment;
            IReadOnlyList<string> runSecrets = Array.Empty<string>();
            try
            {
                (assignment, runSecrets) = await BuildTrustedAssignmentAsync(execution, test, ct);
            }
            catch (ConflictException ex)
            {
                // Deterministic variable/secret failure: missing variable or
                // unavailable secret. Never leaks values (message carries only names).
                return new WorkerExecutionOutcome(
                    ExecutionTestStatus.Error, FailureClassification.AutomationFailure,
                    nameof(Common.ConflictException), Truncate(ex.Message),
                    total.ElapsedMilliseconds,
                    Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
                    Array.Empty<WorkerScreenshotDto>(), test.Attempt);
            }
            try
            {
                var outcome = await DispatchAndTrackAsync(
                    execution, test, assignment, runSecrets, heartbeatAsync, ct);
                outcome = outcome with { Attempt = attempt, DurationMs = total.ElapsedMilliseconds };
                return SanitizeOutcome(outcome, runSecrets);
            }
            catch (WorkerInfrastructureException ex) when (attempt < MaxAttempts && ex.IsRetryable)
            {
                lastInfraError = ex;
                _logger.LogWarning(ex, "Worker infrastructure failure for execution {ExecutionId}; retrying once.",
                    executionId);
            }
            catch (WorkerInfrastructureException ex)
            {
                if (!ex.IsRetryable)
                {
                    // Deterministic contract rejection: automation failure, no retry.
                    return new WorkerExecutionOutcome(
                        ExecutionTestStatus.Error, FailureClassification.AutomationFailure,
                        nameof(WorkerInfrastructureException), RedactTruncate(ex.Message),
                        total.ElapsedMilliseconds,
                        Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
                        Array.Empty<WorkerScreenshotDto>(), test.Attempt);
                }
                // Retries exhausted: fall through to the environment-failure outcome.
                lastInfraError = ex;
                break;
            }
        }

        // Infrastructure stayed down after the single retry: terminal AutomationFailure.
        return new WorkerExecutionOutcome(
            ExecutionTestStatus.Error, FailureClassification.EnvironmentFailure,
            nameof(WorkerInfrastructureException),
            RedactTruncate(lastInfraError?.Message ?? "The Playwright worker is unreachable."),
            total.ElapsedMilliseconds,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(), test.Attempt);
    }

    private async Task<WorkerExecutionOutcome> DispatchAndTrackAsync(
        Execution execution, ExecutionTest test, WorkerAssignmentDto assignment,
        IReadOnlyList<string> secretValues,
        Func<Task>? heartbeatAsync, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var assignmentId = await _worker.StartAssignmentAsync(assignment, ct);
        var seenSteps = new HashSet<int>();
        var seenLogs = new HashSet<long>();
        int? lastCurrentStep = null;

        try
        {
            while (true)
            {
                if (heartbeatAsync is not null)
                    await heartbeatAsync();
                ct.ThrowIfCancellationRequested();

                var progress = await _worker.GetAssignmentAsync(assignmentId, ct);
            if (progress.CurrentStepOrder != lastCurrentStep && progress.CurrentStepOrder is not null)
            {
                lastCurrentStep = progress.CurrentStepOrder;
                await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStepStarted,
                    new { executionId = execution.Id, executionTestId = test.Id, order = lastCurrentStep }, ct);
            }
            foreach (var step in progress.StepResults)
            {
                if (seenSteps.Add(step.Order))
                    await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStepCompleted,
                        new
                        {
                            executionId = execution.Id,
                            executionTestId = test.Id,
                            step = new
                            {
                                order = step.Order,
                                action = step.Action,
                                target = step.Target,
                                status = step.Status,
                                durationMs = step.DurationMs,
                                errorMessage = RedactTruncate(step.ErrorMessage, secretValues),
                                healed = step.Healed,
                                recoveredTarget = step.RecoveredTarget,
                                healingStrategy = step.HealingStrategy,
                                aiAssisted = step.AiAssisted,
                            },
                        }, ct);
            }
            var newLogs = progress.Logs.Where(l => seenLogs.Add(l.Seq)).ToList();
            if (newLogs.Count > 0)
                // Slice 3A: the live SignalR path applies the same secret-aware
                // redaction as persistence (previously raw). Secret values never
                // stream to subscribers.
                await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionLogReceived,
                    new
                    {
                        executionId = execution.Id,
                        executionTestId = test.Id,
                        logs = newLogs.Select(l => new { timestamp = l.TimestampUnixMs, level = l.Level, message = RedactTruncate(l.Message, secretValues) }),
                    }, ct);

            if (progress.Result is not null)
            {
                var result = progress.Result;
                // Sanitize worker-echoed text before it enters the outcome (which
                // flows into Temporal history via the activity return).
                return SanitizeOutcome(new WorkerExecutionOutcome(
                    MapWorkerStatus(result.Status), MapClassification(result.Classification),
                    result.ErrorType, Truncate(result.ErrorMessage),
                    started.ElapsedMilliseconds,
                    result.StepResults, result.Logs, result.Screenshots, test.Attempt,
                    result.HealingAttempts ?? Array.Empty<WorkerHealingAttemptDto>()), secretValues);
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.Value.WorkerPollIntervalSeconds), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Best-effort worker abort, then surface cancellation to Temporal.
            try { await _worker.CancelAssignmentAsync(assignmentId, CancellationToken.None); }
            catch (Exception abortEx)
            {
                _logger.LogWarning(abortEx, "Worker abort failed for assignment {AssignmentId}.", assignmentId);
            }
            throw;
        }
    }

    // ---------- persist ----------

    public async Task PersistResultAsync(
        Guid executionId, WorkerExecutionOutcome outcome, CancellationToken ct)
    {
        var (execution, test) = await LoadAsync(executionId, ct);
        if (ExecutionTransitions.IsTestTerminal(test.Status))
            return; // idempotent: exactly one terminal state wins

        // Fencing: verify the active assignment ID matches the test's started assignment ID
        // to prevent stale workers from persisting results after their lease expired.
        if (_assignments is not null)
        {
            var activeAssignment = await _assignments.FindActiveByTestAsync(test.Id, ct);
            if (activeAssignment is not null && test.StartedAssignmentId != activeAssignment.Id)
            {
                _logger.LogWarning("Stale completion rejected for execution test {TestId}: assignment ID mismatch (expected {Expected}, got {Actual}).",
                    test.Id, test.StartedAssignmentId, activeAssignment.Id);
                throw new ConflictException($"Stale completion rejected for execution test {test.Id}: assignment ID mismatch.");
            }
        }

        // Slice 3C-4B-2: best-effort mobile session close while the lease is
        // still active (fenced; stale closes are swallowed). Web executions
        // and already-closed sessions are no-ops.
        await CloseMobileSessionAsync(execution, test, ct);

        var now = _clock.UtcNow;
        // Slice 3A: re-resolve secret values in this activity scope for
        // exact-match masking of worker-echoed text. Best-effort: persist must
        // never fail because the secret backend is down (heuristic redaction
        // still applies). Values stay in memory and are never stored.
        var persistSecrets = await LoadSecretValuesForMaskingAsync(execution, ct);
        await _store.AddStepResultsAsync(outcome.Steps.Select(s => new ExecutionStepResult
        {
            ExecutionTestId = test.Id,
            StepOrder = s.Order,
            Action = s.Action,
            Target = s.Target,
            Status = MapWorkerStatus(s.Status),
            StartedAt = FromUnixMs(s.StartedAtUnixMs),
            CompletedAt = FromUnixMs(s.CompletedAtUnixMs),
            DurationMs = s.DurationMs,
            ErrorMessage = RedactTruncate(s.ErrorMessage, persistSecrets),
        }), ct);

        var logRows = new List<ExecutionLog>();
        foreach (var log in outcome.Logs)
            logRows.Add(new ExecutionLog
            {
                ExecutionTestId = test.Id,
                Timestamp = FromUnixMs(log.TimestampUnixMs),
                Level = log.Level,
                Message = RedactTruncate(log.Message, persistSecrets) ?? string.Empty,
                Metadata = null,
            });
        await _store.AppendLogsAsync(logRows, ct);

        await PersistScreenshotsAsync(execution, test, outcome, ct);

        // Slice 3C-4B-3: mobile failure evidence through the same fenced,
        // best-effort artifact path. No new tables, endpoints, or storage.
        await PersistMobileEvidenceAsync(execution, test, outcome, ct);

        // Slice 3C-4D-2: deterministic visual comparison. Only an
        // otherwise-Passed outcome with verifyScreenshot evidence is
        // evaluated; unrelated failures are never rewritten. A mismatch
        // overrides the outcome below through the normal terminal path.
        if (outcome.Status == ExecutionTestStatus.Passed)
            outcome = await ApplyVisualComparisonAsync(execution, test, outcome, ct);

        // Slice 11: persist worker-reported healing outcomes (fenced). Healing
        // never mutates the bound test version; history keeps original targets.
        await RecordHealingAttemptsAsync(execution, test, outcome, ct);

        test.Status = outcome.Status;
        test.FailureClassification = outcome.Classification;
        test.DurationMs = outcome.DurationMs;
        test.ErrorType = outcome.ErrorType;
        test.ErrorMessage = RedactTruncate(outcome.ErrorMessage, persistSecrets);
        test.UpdatedAt = now;
        execution.Status = MapExecutionStatus(outcome.Status);
        execution.CompletedAt = now;
        execution.UpdatedAt = now;
        await _store.SaveChangesAsync(ct);
        if (_leases is not null)
        {
            var activeAssignment = await _assignments.FindActiveByTestAsync(test.Id, ct);
            if (activeAssignment is not null)
                await _leases.ReleaseAssignmentAsync(test.Id, activeAssignment.Id, activeAssignment.AssignmentToken, test.Status.ToString(), ct);
        }

        await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionTestCompleted,
            new
            {
                executionId = execution.Id,
                executionTestId = test.Id,
                status = test.Status.ToString(),
                classification = test.FailureClassification.ToString(),
            }, ct);
        await PublishStatusAsync(execution, ct);
        if (execution.Status == ExecutionStatus.Passed)
        {
            await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionCompleted,
                new { executionId = execution.Id, status = execution.Status.ToString() }, ct);
            await _audit.RecordAsync("execution.completed", "execution",
                execution.Id.ToString(), execution.ProjectId, SafeMeta(execution, "completed"), ct);
        }
        else
        {
            await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionFailed,
                new
                {
                    executionId = execution.Id,
                    status = execution.Status.ToString(),
                    classification = test.FailureClassification.ToString(),
                }, ct);
            var action = execution.Status switch
            {
                ExecutionStatus.Cancelled => "execution.cancelled",
                ExecutionStatus.TimedOut => "execution.timed_out",
                _ => "execution.failed",
            };
            await _audit.RecordAsync(action, "execution",
                execution.Id.ToString(), execution.ProjectId, SafeMeta(execution, "terminal"), ct);
        }
    }

    // ---------- terminal finalizers (idempotent) ----------

    public async Task FinalizeCancelledAsync(Guid executionId, string reason, CancellationToken ct)
        => await FinalizeTerminalAsync(executionId,
            ExecutionStatus.Cancelled, ExecutionTestStatus.Cancelled,
            FailureClassification.Unknown, "execution.cancelled", reason, ct);

    public async Task FinalizeTimedOutAsync(Guid executionId, string reason, CancellationToken ct)
        => await FinalizeTerminalAsync(executionId,
            ExecutionStatus.TimedOut, ExecutionTestStatus.TimedOut,
            FailureClassification.EnvironmentFailure, "execution.timed_out", reason, ct);

    public async Task FinalizeErrorAsync(Guid executionId, string reason, CancellationToken ct)
        => await FinalizeTerminalAsync(executionId,
            ExecutionStatus.Error, ExecutionTestStatus.Error,
            FailureClassification.AutomationFailure, "execution.failed", reason, ct);

    private async Task FinalizeTerminalAsync(
        Guid executionId, ExecutionStatus executionStatus, ExecutionTestStatus testStatus,
        FailureClassification classification, string auditAction, string reason, CancellationToken ct)
    {
        var (execution, test) = await LoadAsync(executionId, ct);
        if (ExecutionTransitions.IsTestTerminal(test.Status))
            return;

        // Fencing: verify the active assignment ID matches the test's started assignment ID
        // to prevent stale workers from finalizing executions after their lease expired.
        if (_assignments is not null)
        {
            var activeAssignment = await _assignments.FindActiveByTestAsync(test.Id, ct);
            if (activeAssignment is not null && test.StartedAssignmentId != activeAssignment.Id)
            {
                _logger.LogWarning("Stale finalization rejected for execution test {TestId}: assignment ID mismatch (expected {Expected}, got {Actual}).",
                    test.Id, test.StartedAssignmentId, activeAssignment.Id);
                throw new ConflictException($"Stale finalization rejected for execution test {test.Id}: assignment ID mismatch.");
            }
        }

        // Slice 3C-4B-2: best-effort mobile session close on terminal paths
        // (timeout/cancel/error where the coordinator never ran or was
        // interrupted). Fenced and swallowed; never fails finalization.
        await CloseMobileSessionAsync(execution, test, ct);

        var now = _clock.UtcNow;
        if (ExecutionTransitions.IsValidTestTransition(test.Status, testStatus))
        {
            test.Status = testStatus;
            test.FailureClassification = classification;
            test.ErrorMessage = Truncate(reason);
            test.UpdatedAt = now;
        }
        if (ExecutionTransitions.IsValidTransition(execution.Status, executionStatus))
        {
            execution.Status = executionStatus;
            execution.CompletedAt = now;
            execution.UpdatedAt = now;
        }
        await _store.AppendLogsAsync(new[]
        {
            new ExecutionLog
            {
                ExecutionTestId = test.Id,
                Timestamp = now,
                Level = "Warning",
                Message = SensitiveDataRedactor.Redact(reason),
            },
        }, ct);
        await _store.SaveChangesAsync(ct);
        if (_leases is not null)
        {
            var activeAssignment = await _assignments.FindActiveByTestAsync(test.Id, ct);
            if (activeAssignment is not null)
                await _leases.ReleaseAssignmentAsync(test.Id, activeAssignment.Id, activeAssignment.AssignmentToken, test.Status.ToString(), ct);
        }

        await PublishStatusAsync(execution, ct);
        await _events.PublishAsync(execution.Id,
            executionStatus == ExecutionStatus.Passed
                ? ExecutionEvents.ExecutionCompleted
                : ExecutionEvents.ExecutionFailed,
            new { executionId = execution.Id, status = execution.Status.ToString() }, ct);
        await _audit.RecordAsync(auditAction, "execution",
            execution.Id.ToString(), execution.ProjectId, SafeMeta(execution, "finalized"), ct);
    }

    // ---------- internals ----------

    private async Task<(Execution Execution, ExecutionTest Test)> LoadAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await _store.GetExecutionByIdAsync(executionId, ct)
            ?? throw new Common.NotFoundException("Execution not found.");
        var test = (await _store.ListTestsByExecutionAsync(execution.Id, ct))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new Common.NotFoundException("Execution test not found.");
        return (execution, test);
    }

    private async Task FailSafeAsync(
        Execution execution, ExecutionTest test, string reason, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        test.Status = ExecutionTestStatus.Failed;
        test.FailureClassification = FailureClassification.AutomationFailure;
        test.ErrorType = nameof(FailureClassification.AutomationFailure);
        test.ErrorMessage = Truncate(reason);
        test.UpdatedAt = now;
        execution.Status = ExecutionStatus.Failed;
        execution.CompletedAt = now;
        execution.UpdatedAt = now;
        await _store.SaveChangesAsync(ct);
        if (_leases is not null)
        {
            var activeAssignment = await _assignments.FindActiveByTestAsync(test.Id, ct);
            if (activeAssignment is not null)
                await _leases.ReleaseAssignmentAsync(test.Id, activeAssignment.Id, activeAssignment.AssignmentToken, test.Status.ToString(), ct);
        }
        await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionFailed,
            new { executionId = execution.Id, status = execution.Status.ToString(), reason }, ct);
        await _audit.RecordAsync("execution.failed", "execution",
            execution.Id.ToString(), execution.ProjectId, SafeMeta(execution, "prepare-failed"), ct);
    }

    private async Task ClearAttemptRowsAsync(Guid testId, CancellationToken ct)
    {
        await _store.DeleteStepResultsAsync(testId, ct);
        await _store.DeleteLogsAsync(testId, ct);
        await _store.DeleteArtifactsAsync(testId, ct);
        await _store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Slice 3C-4B-2: best-effort close of the runtime session bound to the
    /// test's current assignment. No-op for web executions, unconfigured
    /// sessions, or missing bindings. Fenced inside the session service;
    /// every failure is swallowed so terminal persistence always wins and a
    /// stale path can never disturb a newer session (the reaper converges).
    /// </summary>
    private async Task CloseMobileSessionAsync(
        Execution execution, ExecutionTest test, CancellationToken ct)
    {
        if (_mobileSessions is null)
            return;
        if (!string.Equals(test.Framework, "appium", StringComparison.OrdinalIgnoreCase))
            return;
        if (test.AssignmentId is null || test.AssignmentToken is null)
            return;
        try
        {
            await _mobileSessions.CloseForAssignmentAsync(
                execution.ProjectId, test.AssignmentId.Value, test.AssignmentToken.Value, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Mobile session close skipped for execution {ExecutionId}.",
                execution.Id);
        }
    }

    private async Task<WorkerAssignmentDto> BuildAssignmentAsync(
        Execution execution, ExecutionTest test, CancellationToken ct)
    {
        var version = test.TestCaseVersionId is not null
            ? await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, ct)
            : null;
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        if (version is null || testCase is null)
            throw new WorkerInfrastructureException("The bound test case version is unavailable.");
        // Retry path rebuilds inside the trusted activity: full values.
        var (assignment, _) = await BuildTrustedAssignmentAsync(execution, test, testCase, version, redactSecrets: false, ct);
        return assignment;
    }

    private async Task<(WorkerAssignmentDto Assignment, IReadOnlyList<string> SecretValues)> BuildTrustedAssignmentAsync(
        Execution execution, ExecutionTest test, CancellationToken ct)
    {
        var version = test.TestCaseVersionId is not null
            ? await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, ct)
            : null;
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        if (version is null || testCase is null)
            throw new WorkerInfrastructureException("The bound test case version is unavailable.");
        return await BuildTrustedAssignmentAsync(execution, test, testCase, version, redactSecrets: false, ct);
    }

    private async Task<WorkerAssignmentDto> BuildAssignmentAsync(
        Execution execution, ExecutionTest test, TestCase testCase, TestCaseVersion version,
        CancellationToken ct, bool redactSecrets = false)
        => (await BuildTrustedAssignmentAsync(execution, test, testCase, version, redactSecrets, ct)).Assignment;

    /// <summary>
    /// Slice 3A: substitutes ${{ KEY }} placeholders with resolved variables
    /// (single-pass). Secret resolution happens here, inside the trusted
    /// activity scope. When <paramref name="redactSecrets"/> is set (Prepare
    /// path, whose DTO enters Temporal history), secret-derived values are
    /// masked before return. The worker-bound path keeps plaintext only in
    /// memory for the direct HTTPS dispatch.
    /// </summary>
    private async Task<(WorkerAssignmentDto Assignment, IReadOnlyList<string> SecretValues)> BuildTrustedAssignmentAsync(
        Execution execution, ExecutionTest test, TestCase testCase, TestCaseVersion version,
        bool redactSecrets, CancellationToken ct)
    {
        var (values, secretValues, secretKeys) = await ResolveVariablesForEngineAsync(execution, ct);
        var steps = TestStep.Parse(version.StructuredSteps?.RootElement);
        var workerSteps = steps.Select(s =>
        {
            string? target, value;
            bool valueHadSecret;
            try
            {
                (target, _) = VariableModel.SubstituteSecretAware(s.Target, values, secretKeys);
                (value, valueHadSecret) = VariableModel.SubstituteSecretAware(s.Value, values, secretKeys);
            }
            catch (Common.ConflictException ex)
            {
                throw new Common.ConflictException(
                    $"Step {s.Order} ({s.Action}) references an undefined variable: {ex.Message}");
            }
            if (redactSecrets)
            {
                // Mask secret-derived content so the Prepare DTO (persisted in
                // Temporal history) carries no plaintext. Heuristic redaction
                // still applies downstream as defense in depth.
                target = VariableModel.MaskSecrets(target, secretValues);
                value = ExecutionValueRedactor.RedactStepValue(s.Action, s.Target, VariableModel.MaskSecrets(value, secretValues));
            }
            else if (valueHadSecret)
            {
                // Secret-derived values travel plaintext over the trusted,
                // authenticated worker transport only. Exact-match masking at
                // every observability boundary keeps them out of logs,
                // SignalR, Temporal history, and persistence.
            }
            else
            {
                value = ExecutionValueRedactor.RedactStepValue(s.Action, target, value);
            }
            return new WorkerStepDto(s.Order, s.Action, target, value);
        }).ToList();
        var targetUrl = steps.Select(s => new
            {
                s.Action,
                Target = VariableModel.Substitute(s.Target, values),
            })
            .FirstOrDefault(s =>
                string.Equals(s.Action, "navigate", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(s.Target))?.Target;
        if (redactSecrets)
            targetUrl = VariableModel.MaskSecrets(targetUrl, secretValues);
        var timeouts = new WorkerTimeoutsDto(
            (int)_options.Value.ExecutionTimeout.TotalMilliseconds,
            (int)_options.Value.StepTimeout.TotalMilliseconds);
        return (new WorkerAssignmentDto(
            test.Id.ToString("N"), execution.Id.ToString("N"),
            string.IsNullOrWhiteSpace(test.Framework) ? "playwright" : test.Framework!,
            string.IsNullOrWhiteSpace(test.Browser) ? "chromium" : test.Browser!,
            targetUrl, workerSteps, timeouts,
            ScreenshotOnFailure: true, ScreenshotOnFinish: false,
            test.AssignmentToken ?? Guid.Empty,
            await ResolveHealingPolicyAsync(execution.ProjectId, ct)), secretValues);
    }

    /// <summary>
    /// Resolves variables for an execution. Legacy environment-less executions
    /// (null EnvironmentId, no envelope) resolve to empty — steps run literally
    /// as in Phase 2. Returns (values, secretValues-for-masking).
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> SecretValues, IReadOnlySet<string> SecretKeys)> ResolveVariablesForEngineAsync(
        Execution execution, CancellationToken ct)
    {
        if (_varResolver is null || _envelopes is null)
            return (new Dictionary<string, string>(StringComparer.Ordinal), Array.Empty<string>(),
                new HashSet<string>(StringComparer.Ordinal));
        var envelope = await _envelopes.GetByExecutionAsync(execution.Id, ct);
        var environmentId = envelope?.EnvironmentId ?? execution.EnvironmentId;
        if (!environmentId.HasValue)
            return (new Dictionary<string, string>(StringComparer.Ordinal), Array.Empty<string>(),
                new HashSet<string>(StringComparer.Ordinal));
        try
        {
            var resolved = await _varResolver.ResolveForExecutionAsync(
                execution.ProjectId, environmentId.Value, envelope?.SuiteId, execution.Id, ct);
            return (resolved.Values, resolved.SecretValues, resolved.SecretKeys);
        }
        catch (Common.ConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Secret/variable backend down: deterministic automation failure,
            // never plaintext, never silent substitution.
            throw new Common.ConflictException(
                $"Variables could not be resolved for this environment: {ex.GetType().Name}");
        }
    }

    /// <summary>Best-effort secret reload for masking persisted/worker-echoed text.</summary>
    private async Task<IReadOnlyList<string>> LoadSecretValuesForMaskingAsync(
        Execution execution, CancellationToken ct)
    {
        try
        {
            return (await ResolveVariablesForEngineAsync(execution, ct)).SecretValues;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Secret-aware outcome sanitizer: exact-match masks secret-derived values
    /// in every worker-echoed string, then applies heuristic redaction.
    /// The sanitized outcome is what enters Temporal history and persistence.
    /// </summary>
    private static WorkerExecutionOutcome SanitizeOutcome(
        WorkerExecutionOutcome outcome, IReadOnlyList<string> secretValues)
    {
        if (secretValues is null || secretValues.Count == 0)
            return new WorkerExecutionOutcome(
                outcome.Status, outcome.Classification, outcome.ErrorType,
                RedactTruncate(outcome.ErrorMessage),
                outcome.DurationMs,
                outcome.Steps.Select(s => s with { ErrorMessage = RedactTruncate(s.ErrorMessage) }).ToList(),
                outcome.Logs.Select(l => l with { Message = RedactTruncate(l.Message) ?? string.Empty }).ToList(),
                outcome.Screenshots, outcome.Attempt, outcome.HealingAttempts,
                SanitizeEvidence(outcome, null), SanitizeServerLogs(outcome, null));
        return new WorkerExecutionOutcome(
            outcome.Status, outcome.Classification, outcome.ErrorType,
            RedactTruncate(outcome.ErrorMessage, secretValues),
            outcome.DurationMs,
            outcome.Steps.Select(s => s with { ErrorMessage = RedactTruncate(s.ErrorMessage, secretValues) }).ToList(),
            outcome.Logs.Select(l => l with { Message = RedactTruncate(l.Message, secretValues) ?? string.Empty }).ToList(),
            outcome.Screenshots, outcome.Attempt, outcome.HealingAttempts,
            SanitizeEvidence(outcome, secretValues), SanitizeServerLogs(outcome, secretValues));
    }

    /// <summary>
    /// Slice 3C-4B-3: defense-in-depth re-masking of worker evidence text.
    /// The worker already redacts and bounds page sources; the coordinator
    /// re-applies exact secret masking here so a worker/control-plane skew
    /// can never leak a known secret into Temporal history or persistence.
    /// Bounds are re-enforced; oversized entries are truncated, never dropped
    /// silently into an unbounded store.
    /// </summary>
    private static IReadOnlyList<WorkerPageSourceDto> SanitizeEvidence(
        WorkerExecutionOutcome outcome, IReadOnlyList<string>? secretValues)
        => (outcome.PageSources ?? Array.Empty<WorkerPageSourceDto>()).Select(s => s with
        {
            XmlContent = BoundEvidence(RedactTruncate(s.XmlContent, secretValues) ?? string.Empty, MobileEvidenceBounds.MaxPageSourceChars),
        }).ToList();

    private static IReadOnlyList<WorkerServerLogDto> SanitizeServerLogs(
        WorkerExecutionOutcome outcome, IReadOnlyList<string>? secretValues)
        => (outcome.ServerLogs ?? Array.Empty<WorkerServerLogDto>()).Select(s => s with
        {
            TextContent = BoundEvidence(RedactTruncate(s.TextContent, secretValues) ?? string.Empty, MobileEvidenceBounds.MaxServerLogChars),
        }).ToList();

    private static string BoundEvidence(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars];

    /// <summary>
    /// Slice 11: the worker-facing healing policy. Missing store rows and any
    /// lookup failure mean disabled — normal steps never pay healing overhead
    /// and pre-Slice-11 behavior is preserved exactly.
    /// </summary>
    private async Task<WorkerHealingPolicyDto?> ResolveHealingPolicyAsync(
        Guid projectId, CancellationToken ct)
    {
        if (_healingPolicies is null)
            return null;
        try
        {
            var row = await _healingPolicies.GetByProjectAsync(projectId, ct);
            if (row is null || !row.Enabled)
                return null;
            return new WorkerHealingPolicyDto(
                true, row.AiFallbackEnabled, 1,
                row.MinDeterministicScore, row.MinAiConfidence,
                SelfHealingService.ParseStrategies(row.AllowedStrategies));
        }
        catch (Exception ex)
        {
            // Policy lookup must never fail an execution: heal nothing.
            _logger.LogWarning(ex, "Self-healing policy lookup failed; healing disabled for this execution.");
            return null;
        }
    }

    private async Task PersistScreenshotsAsync(
        Execution execution, ExecutionTest test, WorkerExecutionOutcome outcome, CancellationToken ct)
    {
        if (outcome.Screenshots.Count == 0) return;
        if (!_artifacts.IsConfigured)
        {
            _logger.LogWarning("Artifact storage is not configured; {Count} screenshot(s) for execution {ExecutionId} were not stored.",
                outcome.Screenshots.Count, execution.Id);
            return;
        }
        foreach (var shot in outcome.Screenshots)
        {
            try
            {
                var bytes = Convert.FromBase64String(shot.Base64Content);
                var slug = Slug(shot.FileName);
                var key = $"projects/{execution.ProjectId}/executions/{execution.Id}/tests/{test.Id}/step-{(shot.StepOrder ?? 0):000}-{slug}";
                using var stream = new MemoryStream(bytes, writable: false);
                await _artifacts.UploadAsync(key, stream,
                    string.IsNullOrWhiteSpace(shot.ContentType) ? "image/png" : shot.ContentType, ct);
                await _store.AddArtifactAsync(new ExecutionArtifact
                {
                    ExecutionTestId = test.Id,
                    ArtifactType = "screenshot",
                    StorageKey = key,
                    FileName = shot.FileName,
                    StepOrder = shot.StepOrder,
                    ContentType = string.IsNullOrWhiteSpace(shot.ContentType) ? "image/png" : shot.ContentType,
                    SizeBytes = bytes.Length,
                    CreatedAt = _clock.UtcNow,
                }, ct);
            }
            catch (Exception ex)
            {
                // Artifact upload must never fail the execution result itself (§60.7).
                _logger.LogWarning(ex, "Screenshot upload failed for execution {ExecutionId}.", execution.Id);
            }
        }
    }

    /// <summary>
    /// Slice 3C-4B-3: persists bounded mobile failure evidence (page-source
    /// snapshots, worker log tails) through the existing artifact
    /// infrastructure. Runs inside the same fenced <c>PersistResultAsync</c>
    /// path as screenshots, so StartedAssignmentId fencing and idempotency
    /// apply unchanged. Content arrives already redacted and bounded from
    /// the worker/coordinator; bounds are re-checked here and oversized
    /// entries are skipped with a warning. Upload failures never corrupt
    /// the execution result.
    /// </summary>
    private async Task PersistMobileEvidenceAsync(
        Execution execution, ExecutionTest test, WorkerExecutionOutcome outcome, CancellationToken ct)
    {
        var sources = outcome.PageSources ?? Array.Empty<WorkerPageSourceDto>();
        var logs = outcome.ServerLogs ?? Array.Empty<WorkerServerLogDto>();
        if (sources.Count == 0 && logs.Count == 0) return;
        if (!_artifacts.IsConfigured)
        {
            _logger.LogWarning("Artifact storage is not configured; {Count} mobile evidence artifact(s) for execution {ExecutionId} were not stored.",
                sources.Count + logs.Count, execution.Id);
            return;
        }
        foreach (var source in sources)
        {
            var xml = source.XmlContent ?? string.Empty;
            if (xml.Length > MobileEvidenceBounds.MaxPageSourceChars)
            {
                _logger.LogWarning("Oversized page-source evidence for execution {ExecutionId} was not stored.", execution.Id);
                continue;
            }
            await PersistTextEvidenceAsync(execution, test,
                "page-source", "text/xml", source.StepOrder, source.FileName, xml, ct);
        }
        foreach (var log in logs)
        {
            var text = log.TextContent ?? string.Empty;
            if (text.Length > MobileEvidenceBounds.MaxServerLogChars)
            {
                _logger.LogWarning("Oversized server-log evidence for execution {ExecutionId} was not stored.", execution.Id);
                continue;
            }
            await PersistTextEvidenceAsync(execution, test,
                "appium-log", "text/plain", null, log.FileName, text, ct);
        }
    }

    private async Task PersistTextEvidenceAsync(
        Execution execution, ExecutionTest test,
        string artifactType, string contentType,
        int? stepOrder, string? fileName, string text, CancellationToken ct)
    {
        try
        {
            var slug = Slug(fileName);
            var order = stepOrder is null ? "finish" : $"step-{stepOrder.Value:000}";
            var key = $"projects/{execution.ProjectId}/executions/{execution.Id}/tests/{test.Id}/{order}-{slug}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            using var stream = new MemoryStream(bytes, writable: false);
            await _artifacts.UploadAsync(key, stream, contentType, ct);
            await _store.AddArtifactAsync(new ExecutionArtifact
            {
                ExecutionTestId = test.Id,
                ArtifactType = artifactType,
                StorageKey = key,
                FileName = fileName,
                StepOrder = stepOrder,
                ContentType = contentType,
                SizeBytes = bytes.Length,
                CreatedAt = _clock.UtcNow,
            }, ct);
        }
        catch (Exception ex)
        {
            // Artifact upload must never fail the execution result itself (§60.7).
            _logger.LogWarning(ex, "Mobile evidence upload failed for execution {ExecutionId}.", execution.Id);
        }
    }

    /// <summary>
    /// Slice 3C-4D-2: deterministic visual comparison for verifyScreenshot
    /// checkpoints. Runs inside the fenced idempotent persist path, after
    /// evidence is stored and before terminal finalization. Only
    /// otherwise-Passed outcomes with verifyScreenshot step evidence are
    /// evaluated; every skip/error path leaves the outcome untouched, and a
    /// mismatch overrides it to Failed/TestFailure through the normal
    /// terminal flow below (defects, tickets, events, lease release).
    /// </summary>
    private async Task<WorkerExecutionOutcome> ApplyVisualComparisonAsync(
        Execution execution, ExecutionTest test, WorkerExecutionOutcome outcome, CancellationToken ct)
    {
        if (_visual is null)
            return outcome;
        var checkpoints = outcome.Steps
            .Where(s => string.Equals(s.Action, "verifyScreenshot", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Order)
            .Distinct()
            .ToList();
        if (checkpoints.Count == 0 || test.TestCaseVersionId is null)
            return outcome; // comparison service not consulted without checkpoints

        string? firstMismatch = null;
        foreach (var order in checkpoints)
        {
            ct.ThrowIfCancellationRequested();
            var shot = outcome.Screenshots.FirstOrDefault(s => s.StepOrder == order);
            if (shot is null)
            {
                _logger.LogWarning("Visual comparison skipped for execution {ExecutionId} step {StepOrder}: no checkpoint screenshot.",
                    execution.Id, order);
                continue;
            }
            byte[] actual;
            try
            {
                actual = Convert.FromBase64String(shot.Base64Content);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Visual comparison skipped for execution {ExecutionId} step {StepOrder}: checkpoint image undecodable.",
                    execution.Id, order);
                continue;
            }
            var compared = await _visual.CompareCheckpointAsync(
                execution.ProjectId, test.TestCaseVersionId.Value, order, actual, ct);
            if (compared is null)
                continue; // skipped/error: verdict unchanged, warning already logged
            await _audit.RecordAsync("visual.compared", "execution",
                execution.Id.ToString(), execution.ProjectId, SafeVisualMeta(execution, test, compared, order), ct);
            if (!compared.IsMismatch)
                continue;
            await PersistVisualDiffAsync(execution, test, order, compared, ct);
            firstMismatch ??= compared.DimensionMismatch
                ? $"Visual mismatch at step {order}: dimensions {compared.ActualWidth}x{compared.ActualHeight} vs baseline {compared.BaselineWidth}x{compared.BaselineHeight} (baseline {ShortSha(compared.BaselineSha256)})."
                : $"Visual mismatch at step {order}: {compared.MismatchRateBps} bps exceeds {compared.ThresholdBps} bps threshold (baseline {ShortSha(compared.BaselineSha256)}).";
        }
        if (firstMismatch is null)
            return outcome;
        return outcome with
        {
            Status = ExecutionTestStatus.Failed,
            Classification = FailureClassification.TestFailure,
            ErrorType = "VisualMismatch",
            ErrorMessage = Truncate(firstMismatch),
        };
    }

    private async Task PersistVisualDiffAsync(
        Execution execution, ExecutionTest test, int stepOrder,
        Visual.VisualComparisonOutcome compared, CancellationToken ct)
    {
        if (compared.DiffPng is null || compared.DiffPng.Length == 0)
        {
            _logger.LogWarning("Visual diff omitted for execution {ExecutionId} step {StepOrder}: no diff bytes.",
                execution.Id, stepOrder);
            return;
        }
        try
        {
            // The diff is evidence, not the verdict: upload failures preserve
            // the authoritative mismatch and never fail persistence (§60.7).
            var slug = Slug($"step-{stepOrder}-visual-diff.png");
            var key = $"projects/{execution.ProjectId}/executions/{execution.Id}/tests/{test.Id}/step-{stepOrder:000}-{slug}";
            using var stream = new MemoryStream(compared.DiffPng, writable: false);
            await _artifacts.UploadAsync(key, stream, "image/png", ct);
            await _store.AddArtifactAsync(new ExecutionArtifact
            {
                ExecutionTestId = test.Id,
                ArtifactType = "visual-diff",
                StorageKey = key,
                FileName = $"step-{stepOrder}-visual-diff.png",
                StepOrder = stepOrder,
                ContentType = "image/png",
                SizeBytes = compared.DiffPng.Length,
                CreatedAt = _clock.UtcNow,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Visual diff upload failed for execution {ExecutionId}.", execution.Id);
        }
    }

    private static string ShortSha(string sha256)
        => string.IsNullOrEmpty(sha256) ? "unknown"
            : sha256.Length <= 8 ? sha256 : sha256[..8];

    private static string SafeVisualMeta(
        Execution execution, ExecutionTest test, Visual.VisualComparisonOutcome compared, int stepOrder)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            executionId = execution.Id,
            testId = test.Id,
            testVersionId = test.TestCaseVersionId,
            stepOrder,
            baselineId = compared.BaselineId,
            baselineSha256 = ShortSha(compared.BaselineSha256),
            mismatchRateBps = compared.MismatchRateBps,
            thresholdBps = compared.ThresholdBps,
            baselineWidth = compared.BaselineWidth,
            baselineHeight = compared.BaselineHeight,
            actualWidth = compared.ActualWidth,
            actualHeight = compared.ActualHeight,
            dimensionMismatch = compared.DimensionMismatch,
            verdict = compared.IsMismatch ? "mismatch" : "match",
            algorithmVersion = compared.AlgorithmVersion,
            durationMs = compared.DurationMs,
        }));

    private async Task PublishStatusAsync(Execution execution, CancellationToken ct)
        => await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStatusChanged,
            new { executionId = execution.Id, status = execution.Status.ToString() }, ct);
    /// <summary>
    /// Slice 11: records healing outcomes under the same fencing as the result
    /// itself. A stale worker's report is rejected (logged, never fatal to the
    /// already-persisted execution result); failed healing flows into the
    /// normal failure classification/defect pipeline untouched.
    /// </summary>
    private async Task RecordHealingAttemptsAsync(
        Execution execution, ExecutionTest test, WorkerExecutionOutcome outcome,
        CancellationToken ct)
    {
        if (_healing is null || outcome.HealingAttempts is null || outcome.HealingAttempts.Count == 0)
            return;
        Guid? assignmentId = null;
        if (_assignments is not null)
        {
            try
            {
                assignmentId = (await _assignments.FindActiveByTestAsync(test.Id, ct))?.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Healing assignment lookup failed for execution {ExecutionId}.",
                    execution.Id);
                return;
            }
        }
        try
        {
            await _healing.RecordAttemptsAsync(execution.Id, assignmentId, outcome.HealingAttempts, ct);
        }
        catch (ConflictException ex)
        {
            // Stale report: the execution result already stands; healing state
            // must not overwrite it.
            _logger.LogWarning(ex, "Stale healing report ignored for execution {ExecutionId}.",
                execution.Id);
        }
        catch (Exception ex)
        {
            // Healing persistence must never fail the execution result itself.
            _logger.LogWarning(ex, "Healing persistence failed for execution {ExecutionId}.",
                execution.Id);
        }
    }

    private static WorkerExecutionOutcome OutcomeFromPersisted(ExecutionTest test, int attempt)
        => new(test.Status, test.FailureClassification, test.ErrorType, test.ErrorMessage,
            test.DurationMs ?? 0,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(), attempt);

    private static ExecutionTestStatus MapWorkerStatus(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "passed" => ExecutionTestStatus.Passed,
            "failed" => ExecutionTestStatus.Failed,
            "timedout" or "timed_out" or "timeout" => ExecutionTestStatus.TimedOut,
            "cancelled" or "canceled" => ExecutionTestStatus.Cancelled,
            "skipped" => ExecutionTestStatus.Skipped,
            _ => ExecutionTestStatus.Error,
        };

    private static FailureClassification MapClassification(string? classification)
        => (classification ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "test" or "testfailure" => FailureClassification.TestFailure,
            "application" or "applicationdefect" => FailureClassification.ApplicationDefect,
            "environment" or "environmentfailure" => FailureClassification.EnvironmentFailure,
            "automation" or "automationfailure" => FailureClassification.AutomationFailure,
            _ => FailureClassification.Unknown,
        };

    private static ExecutionStatus MapExecutionStatus(ExecutionTestStatus status)
        => status switch
        {
            ExecutionTestStatus.Passed => ExecutionStatus.Passed,
            ExecutionTestStatus.Failed => ExecutionStatus.Failed,
            ExecutionTestStatus.Cancelled => ExecutionStatus.Cancelled,
            ExecutionTestStatus.TimedOut => ExecutionStatus.TimedOut,
            _ => ExecutionStatus.Error,
        };

    private static DateTimeOffset FromUnixMs(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, unixMs));

    private static string Truncate(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    /// <summary>Defense in depth: worker-echoed text is re-redacted before persistence.</summary>
    private static string? RedactTruncate(string? value)
        => value is null ? null : Truncate(SensitiveDataRedactor.Redact(value));

    /// <summary>
    /// Slice 3A secret-aware variant: exact secret-derived values are masked
    /// first (covers all actions, not just password-like targets), then the
    /// heuristic redactor runs as defense in depth.
    /// </summary>
    private static string? RedactTruncate(string? value, IReadOnlyList<string>? secretValues)
        => value is null ? null : Truncate(SensitiveDataRedactor.Redact(
            VariableModel.MaskSecrets(value, secretValues) ?? string.Empty));

    private static string Slug(string? fileName)
    {
        var lower = (fileName ?? "screenshot.png").ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        foreach (var c in lower)
            builder.Append(char.IsLetterOrDigit(c) ? c : c == '.' ? '.' : '-');
        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0) slug = "screenshot.png";
        return slug.Length > 60 ? slug[^60..].TrimStart('-', '.') : slug;
    }

    private string SafeMeta(Execution execution, string outcome)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            executionId = execution.Id,
            status = execution.Status.ToString(),
            workflowId = execution.WorkflowId,
            outcome,
        }));
}
