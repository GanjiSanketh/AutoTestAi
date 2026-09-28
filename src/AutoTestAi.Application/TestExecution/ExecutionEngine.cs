using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
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
    WorkerAssignmentDto? Assignment);

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
    int Attempt);

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
        ILogger<ExecutionEngine> logger)
    {
        _store = store;
        _cases = cases;
        _worker = worker;
        _events = events;
        _artifacts = artifacts;
        _options = options;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    // ---------- prepare ----------

    public async Task<PreparedExecution> PrepareAsync(Guid executionId, CancellationToken ct)
    {
        var (execution, test) = await LoadAsync(executionId, ct);
        if (ExecutionTransitions.IsTestTerminal(test.Status))
            return new PreparedExecution(executionId, test.Id, false, $"already {test.Status}", null);
        if (test.Status != ExecutionTestStatus.Queued)
            return new PreparedExecution(executionId, test.Id, false, $"unexpected status {test.Status}", null);

        // Re-validate the exact bound version (fail-safe if it vanished or left Approved).
        var version = test.TestCaseVersionId is not null
            ? await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, ct)
            : null;
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        if (version is null || testCase is null || testCase.ProjectId != execution.ProjectId)
        {
            await FailSafeAsync(execution, test,
                "The bound test case version is no longer available.", ct);
            return new PreparedExecution(executionId, test.Id, false, "version unavailable", null);
        }
        if (version.ReviewStatus != ReviewStatus.Approved)
        {
            await FailSafeAsync(execution, test,
                $"Version {version.VersionNumber} is no longer Approved (now {version.ReviewStatus}).", ct);
            return new PreparedExecution(executionId, test.Id, false, "version not approved", null);
        }

        var now = _clock.UtcNow;
        test.Status = ExecutionTestStatus.Running;
        test.UpdatedAt = now;
        execution.Status = ExecutionStatus.Running;
        execution.StartedAt = now;
        execution.UpdatedAt = now;
        await _store.SaveChangesAsync(ct);

        await _audit.RecordAsync("execution.started", "execution",
            execution.Id.ToString(), execution.ProjectId,
            SafeMeta(execution, "started"), ct);
        await PublishStatusAsync(execution, ct);
        await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStarted,
            new { executionId = execution.Id, status = execution.Status.ToString() }, ct);
        await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionTestStarted,
            new { executionId = execution.Id, executionTestId = test.Id, attempt = test.Attempt }, ct);

        return new PreparedExecution(executionId, test.Id, true, null,
            BuildAssignment(execution, test, testCase, version));
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

            WorkerAssignmentDto assignment = await BuildAssignmentAsync(execution, test, ct);
            try
            {
                var outcome = await DispatchAndTrackAsync(
                    execution, test, assignment, heartbeatAsync, ct);
                outcome = outcome with { Attempt = attempt, DurationMs = total.ElapsedMilliseconds };
                return outcome;
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
                        nameof(WorkerInfrastructureException), Truncate(ex.Message),
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
            Truncate(lastInfraError?.Message ?? "The Playwright worker is unreachable."),
            total.ElapsedMilliseconds,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(), test.Attempt);
    }

    private async Task<WorkerExecutionOutcome> DispatchAndTrackAsync(
        Execution execution, ExecutionTest test, WorkerAssignmentDto assignment,
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
                                errorMessage = RedactTruncate(step.ErrorMessage),
                            },
                        }, ct);
            }
            var newLogs = progress.Logs.Where(l => seenLogs.Add(l.Seq)).ToList();
            if (newLogs.Count > 0)
                await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionLogReceived,
                    new
                    {
                        executionId = execution.Id,
                        executionTestId = test.Id,
                        logs = newLogs.Select(l => new { timestamp = l.TimestampUnixMs, level = l.Level, message = l.Message }),
                    }, ct);

            if (progress.Result is not null)
            {
                var result = progress.Result;
                return new WorkerExecutionOutcome(
                    MapWorkerStatus(result.Status), MapClassification(result.Classification),
                    result.ErrorType, Truncate(result.ErrorMessage),
                    started.ElapsedMilliseconds,
                    result.StepResults, result.Logs, result.Screenshots, test.Attempt);
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

        var now = _clock.UtcNow;
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
            ErrorMessage = RedactTruncate(s.ErrorMessage),
        }), ct);

        var logRows = new List<ExecutionLog>();
        foreach (var log in outcome.Logs)
            logRows.Add(new ExecutionLog
            {
                ExecutionTestId = test.Id,
                Timestamp = FromUnixMs(log.TimestampUnixMs),
                Level = log.Level,
                Message = SensitiveDataRedactor.Redact(log.Message),
                Metadata = null,
            });
        await _store.AppendLogsAsync(logRows, ct);

        await PersistScreenshotsAsync(execution, test, outcome, ct);

        test.Status = outcome.Status;
        test.FailureClassification = outcome.Classification;
        test.DurationMs = outcome.DurationMs;
        test.ErrorType = outcome.ErrorType;
        test.ErrorMessage = RedactTruncate(outcome.ErrorMessage);
        test.UpdatedAt = now;
        execution.Status = MapExecutionStatus(outcome.Status);
        execution.CompletedAt = now;
        execution.UpdatedAt = now;
        await _store.SaveChangesAsync(ct);

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
        await PublishStatusAsync(execution, ct);
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

    private async Task<WorkerAssignmentDto> BuildAssignmentAsync(
        Execution execution, ExecutionTest test, CancellationToken ct)
    {
        var version = test.TestCaseVersionId is not null
            ? await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, ct)
            : null;
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        if (version is null || testCase is null)
            throw new WorkerInfrastructureException("The bound test case version is unavailable.");
        return BuildAssignment(execution, test, testCase, version);
    }

    private WorkerAssignmentDto BuildAssignment(
        Execution execution, ExecutionTest test, TestCase testCase, TestCaseVersion version)
    {
        var steps = TestStep.Parse(version.StructuredSteps?.RootElement);
        var workerSteps = steps.Select(s => new WorkerStepDto(
            s.Order, s.Action, s.Target,
            ExecutionValueRedactor.RedactStepValue(s.Action, s.Target, s.Value))).ToList();
        var targetUrl = steps.FirstOrDefault(s =>
                string.Equals(s.Action, "navigate", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(s.Target))?.Target;
        var timeouts = new WorkerTimeoutsDto(
            (int)_options.Value.ExecutionTimeout.TotalMilliseconds,
            (int)_options.Value.StepTimeout.TotalMilliseconds);
        return new WorkerAssignmentDto(
            test.Id.ToString("N"), execution.Id.ToString("N"),
            string.IsNullOrWhiteSpace(test.Framework) ? "playwright" : test.Framework!,
            string.IsNullOrWhiteSpace(test.Browser) ? "chromium" : test.Browser!,
            targetUrl, workerSteps, timeouts,
            ScreenshotOnFailure: true, ScreenshotOnFinish: false);
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

    private async Task PublishStatusAsync(Execution execution, CancellationToken ct)
        => await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStatusChanged,
            new { executionId = execution.Id, status = execution.Status.ToString() }, ct);

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
