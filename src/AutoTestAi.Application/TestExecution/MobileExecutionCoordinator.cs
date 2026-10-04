using System.Diagnostics;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.Executions;
using AutoTestAi.Domain.TestCases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Mobile dispatch boundary (Phase 3 Slice 3C-4B-2). Owns one mobile run:
/// queue-wait claim via <see cref="IGridScheduler.TryClaimMobileAsync"/>,
/// trusted assignment build (variables, device, app, server-side
/// capabilities), <see cref="MobileDeviceSession"/> create/activate/close,
/// <see cref="IMobileWorkerClient"/> dispatch + progress tracking, and
/// outcome mapping into the shared <see cref="WorkerExecutionOutcome"/>.
/// Persistence, fencing, retry budgets, and terminal finalization stay in
/// <see cref="ExecutionEngine"/>; this coordinator never writes execution
/// rows except attempt bookkeeping and the StartedAssignmentId capture.
/// </summary>
public interface IMobileExecutionCoordinator
{
    Task<WorkerExecutionOutcome> RunMobileAsync(Guid executionId, Func<Task>? heartbeatAsync, CancellationToken ct);
}

public sealed class MobileExecutionCoordinator : IMobileExecutionCoordinator
{
    private const int MaxErrorLength = 4000;
    private const int MaxAttempts = 2; // one controlled retry for infrastructure failures only

    private readonly IExecutionStore _store;
    private readonly ITestCaseStore _cases;
    private readonly IMobileRegistryStore _mobile;
    private readonly IGridScheduler _scheduler;
    private readonly IGridAssignmentStore _assignments;
    private readonly IMobileSessionService _sessions;
    private readonly IMobileWorkerClient _worker;
    private readonly IExecutionEventPublisher _events;
    private readonly IArtifactStorage _artifacts;
    private readonly IOptions<ExecutionOptions> _execution;
    private readonly IOptions<GridOptions> _grid;
    private readonly MobileCapabilityBuilder _capabilities;
    private readonly IDateTimeProvider _clock;
    private readonly IVariableResolutionService? _varResolver;
    private readonly IExecutionVariablesStore? _envelopes;
    private readonly SelfHealing.ISelfHealingPolicyStore? _healingPolicies;
    private readonly ILogger<MobileExecutionCoordinator> _logger;

    public MobileExecutionCoordinator(
        IExecutionStore store,
        ITestCaseStore cases,
        IMobileRegistryStore mobile,
        IGridScheduler scheduler,
        IGridAssignmentStore assignments,
        IMobileSessionService sessions,
        IMobileWorkerClient worker,
        IExecutionEventPublisher events,
        IArtifactStorage artifacts,
        IOptions<ExecutionOptions> execution,
        IOptions<GridOptions> grid,
        MobileCapabilityBuilder capabilities,
        IDateTimeProvider clock,
        ILogger<MobileExecutionCoordinator> logger,
        IVariableResolutionService? varResolver = null,
        IExecutionVariablesStore? envelopes = null,
        SelfHealing.ISelfHealingPolicyStore? healingPolicies = null)
    {
        _store = store;
        _cases = cases;
        _mobile = mobile;
        _scheduler = scheduler;
        _assignments = assignments;
        _sessions = sessions;
        _worker = worker;
        _events = events;
        _artifacts = artifacts;
        _execution = execution;
        _grid = grid;
        _capabilities = capabilities;
        _clock = clock;
        _varResolver = varResolver;
        _envelopes = envelopes;
        _healingPolicies = healingPolicies;
        _logger = logger;
    }

    public async Task<WorkerExecutionOutcome> RunMobileAsync(
        Guid executionId, Func<Task>? heartbeatAsync, CancellationToken ct)
    {
        var (execution, test) = await LoadAsync(executionId, ct);
        if (ExecutionTransitions.IsTestTerminal(test.Status))
            return OutcomeFromPersisted(test, test.Attempt);
        if (test.Status != ExecutionTestStatus.Running)
            throw new InvalidOperationException($"Execution test {test.Id} is {test.Status}, not Running.");
        if (!IsAppium(test))
            return AutomationError("Execution test framework is not mobile (appium).", test.Attempt, 0);

        WorkerInfrastructureException? lastInfraError = null;
        var total = Stopwatch.StartNew();
        var excluded = new HashSet<Guid>();
        for (var attempt = test.Attempt; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > test.Attempt)
            {
                await ClearAttemptRowsAsync(test.Id, ct);
                test.Attempt = attempt;
                test.UpdatedAt = _clock.UtcNow;
                await _store.SaveChangesAsync(ct);
                await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionTestStarted,
                    new { executionId = execution.Id, executionTestId = test.Id, attempt }, ct);
            }

            try
            {
                var outcome = await ClaimDispatchTrackAsync(
                    execution, test, excluded, heartbeatAsync, ct);
                outcome = outcome with { Attempt = attempt, DurationMs = total.ElapsedMilliseconds };
                return SanitizeOutcome(outcome, await LoadSecretValuesForMaskingAsync(execution, ct));
            }
            catch (WorkerInfrastructureException ex) when (attempt < MaxAttempts && ex.IsRetryable)
            {
                lastInfraError = ex;
                _logger.LogWarning(ex, "Mobile worker infrastructure failure for execution {ExecutionId}; retrying once.",
                    executionId);
            }
            catch (WorkerInfrastructureException ex)
            {
                if (!ex.IsRetryable)
                    return AutomationError(RedactTruncate(ex.Message) ?? string.Empty, test.Attempt, total.ElapsedMilliseconds);
                lastInfraError = ex;
                break;
            }
            catch (ConflictException ex)
            {
                // Deterministic variable/contract failure from assignment
                // build (mirrors the engine): automation failure, no retry.
                // The lease was already released on the build path.
                return AutomationError(Truncate(ex.Message), test.Attempt, total.ElapsedMilliseconds);
            }
        }

        return new WorkerExecutionOutcome(
            ExecutionTestStatus.Error, FailureClassification.EnvironmentFailure,
            nameof(WorkerInfrastructureException),
            RedactTruncate(lastInfraError?.Message ?? "The Appium worker is unreachable."),
            total.ElapsedMilliseconds,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(), test.Attempt);
    }

    // ---------- claim / dispatch / track (one attempt) ----------

    private async Task<WorkerExecutionOutcome> ClaimDispatchTrackAsync(
        Execution execution, ExecutionTest test, HashSet<Guid> excluded,
        Func<Task>? heartbeatAsync, CancellationToken ct)
    {
        // Inner loop covers claim + dispatch only: a dispatch that never
        // reached the worker frees its lease and re-claims. Anything after
        // dispatch (track) propagates to the attempt loop, mirroring the
        // web engine: exactly one infrastructure retry, then terminal.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var claim = await ClaimAsync(execution.Id, excluded, ct);
            MobileDeviceSession? session = null;
            string? workerRef = null;
            IReadOnlyList<string> runSecrets = Array.Empty<string>();
            try
            {
                MobileAssignmentDto assignment;
                (assignment, runSecrets) = await BuildMobileAssignmentAsync(execution, test, claim, ct);
                session = await CreateSessionAsync(execution, test, claim, ct);
                workerRef = await _worker.StartAssignmentAsync(assignment, ct);
            }
            catch (WorkerInfrastructureException ex) when (ex.IsRetryable)
            {
                // Dispatch never reached the worker (or the lease died):
                // close the session row, free the lease so another worker can
                // take it, exclude this worker for this run, and re-claim.
                if (session is not null)
                    await CloseSessionBestEffortAsync(execution.ProjectId, session.Id, claim, ct);
                excluded.Add(claim.Worker.Id);
                await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (session is not null)
                    await CloseSessionBestEffortAsync(execution.ProjectId, session.Id, claim, ct);
                await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
                throw;
            }
            catch (WorkerInfrastructureException)
            {
                if (session is not null)
                    await CloseSessionBestEffortAsync(execution.ProjectId, session.Id, claim, ct);
                await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
                throw;
            }

            try
            {
                return await TrackAsync(execution, test, claim, session.Id, runSecrets, workerRef, heartbeatAsync, ct);
            }
            catch (OperationCanceledException)
            {
                try { await _worker.CancelAssignmentAsync(workerRef, CancellationToken.None); }
                catch (Exception abortEx)
                {
                    _logger.LogWarning(abortEx, "Appium worker abort failed for assignment {AssignmentId}.", workerRef);
                }
                await CloseSessionBestEffortAsync(execution.ProjectId, session.Id, claim, ct);
                throw;
            }
        }
    }

    private async Task<MobileGridClaim> ClaimAsync(
        Guid executionId, HashSet<Guid> excluded, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        var queuedEventSent = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            MobileGridClaim? claim = null;
            try
            {
                claim = await _scheduler.TryClaimMobileAsync(executionId, excluded, ct);
            }
            catch (NotFoundException ex)
            {
                throw new WorkerInfrastructureException(ex.Message, ex) { IsRetryable = false };
            }
            catch (ConflictException ex)
            {
                // Unclaimable (terminal/misconfigured): fail fast, never queue.
                throw new WorkerInfrastructureException(ex.Message, ex) { IsRetryable = false };
            }
            if (claim is not null)
                return claim;
            if (!queuedEventSent)
            {
                queuedEventSent = true;
                await PublishAsync(executionId, ExecutionEvents.ExecutionQueued,
                    new { executionId, reason = "waiting-for-mobile-capacity" }, ct);
                _logger.LogInformation("Mobile execution {ExecutionId} queued waiting for device capacity.", executionId);
            }
            if (waited.Elapsed >= _grid.Value.QueueWaitTimeout)
                throw new WorkerInfrastructureException(
                    "No mobile execution capacity became available before the queue timeout.");
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_execution.Value.WorkerPollIntervalSeconds, 1, 30)), ct);
        }
    }

    private async Task<MobileDeviceSession> CreateSessionAsync(
        Execution execution, ExecutionTest test, MobileGridClaim claim, CancellationToken ct)
    {
        MobileDeviceSession session;
        try
        {
            session = await _sessions.CreateAsync(
                execution.ProjectId, claim.Slot.DeviceId, claim.Slot.Id,
                execution.Id, claim.Assignment.Id, ct);
        }
        catch (ConflictException ex)
        {
            throw new WorkerInfrastructureException(ex.Message, ex);
        }
        // Fencing capture (mirrors PrepareAsync): the assignment that started
        // this run wins; stale completions are rejected at persist time.
        var (_, live) = await LoadAsync(execution.Id, ct);
        live.StartedAssignmentId = claim.Assignment.Id;
        live.UpdatedAt = _clock.UtcNow;
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (ConflictException ex)
        {
            throw new WorkerInfrastructureException(ex.Message, ex);
        }
        return session;
    }

    private async Task<WorkerExecutionOutcome> TrackAsync(
        Execution execution, ExecutionTest test, MobileGridClaim claim, Guid sessionId,
        IReadOnlyList<string> secretValues, string workerRef,
        Func<Task>? heartbeatAsync, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var seenSteps = new HashSet<int>();
        var seenLogs = new HashSet<long>();
        int? lastCurrentStep = null;
        var activated = false;

        while (true)
        {
            if (heartbeatAsync is not null)
                await heartbeatAsync();
            ct.ThrowIfCancellationRequested();

            var progress = await _worker.GetAssignmentAsync(workerRef, ct);
            if (!activated && !string.IsNullOrWhiteSpace(progress.AppiumSessionId))
            {
                try
                {
                    await _sessions.ActivateAsync(
                        execution.ProjectId, sessionId, claim.Assignment.Id,
                        claim.AssignmentToken, progress.AppiumSessionId!, ct);
                    activated = true;
                }
                catch (ConflictException ex)
                {
                    throw new WorkerInfrastructureException(ex.Message, ex);
                }
                catch (NotFoundException ex)
                {
                    throw new WorkerInfrastructureException(ex.Message, ex);
                }
            }
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
                await CloseSessionBestEffortAsync(execution.ProjectId, sessionId, claim, ct);
                return SanitizeOutcome(new WorkerExecutionOutcome(
                    MapWorkerStatus(result.Status), MapClassification(result.Classification),
                    result.ErrorType, Truncate(result.ErrorMessage),
                    started.ElapsedMilliseconds,
                    result.StepResults.Select(s => new WorkerStepResultDto(
                        s.Order, s.Action, s.Target, s.Status,
                        s.StartedAtUnixMs, s.CompletedAtUnixMs, s.DurationMs, s.ErrorMessage,
                        s.Healed, s.RecoveredTarget, s.HealingStrategy, s.AiAssisted)).ToList(),
                    result.Logs.Select(l => new WorkerLogDto(l.Seq, l.TimestampUnixMs, l.Level, l.Message)).ToList(),
                    result.Screenshots.Select(s => new WorkerScreenshotDto(s.StepOrder, s.FileName, s.ContentType, s.Base64Content)).ToList(),
                    test.Attempt,
                    result.HealingAttempts,
                    (result.PageSources ?? Array.Empty<MobilePageSourceDto>()).Select(s => new WorkerPageSourceDto(s.StepOrder, s.FileName, s.ContentType, s.XmlContent)).ToList(),
                    (result.ServerLogs ?? Array.Empty<MobileServerLogDto>()).Select(s => new WorkerServerLogDto(s.FileName, s.ContentType, s.TextContent)).ToList()), secretValues);
            }

            await Task.Delay(TimeSpan.FromSeconds(_execution.Value.WorkerPollIntervalSeconds), ct);
        }
    }

    private async Task CloseSessionBestEffortAsync(
        Guid projectId, Guid sessionId, MobileGridClaim claim, CancellationToken ct)
    {
        try
        {
            await _sessions.CloseAsync(projectId, sessionId, claim.Assignment.Id, claim.AssignmentToken, ct);
        }
        catch (ConflictException ex)
        {
            _logger.LogInformation(ex, "Mobile session {SessionId} close skipped (stale or already terminal).", sessionId);
        }
        catch (NotFoundException ex)
        {
            _logger.LogInformation(ex, "Mobile session {SessionId} close skipped (not found).", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mobile session {SessionId} close failed; recovery will converge.", sessionId);
        }
    }

    private async Task ReleaseAsync(Guid executionTestId, string terminalStatus, CancellationToken ct)
    {
        try
        {
            await _scheduler.ReleaseLeaseAsync(executionTestId, terminalStatus, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mobile lease release failed for test {TestId}; reaper will converge.", executionTestId);
        }
    }

    // ---------- trusted assignment build ----------

    private async Task<(MobileAssignmentDto Assignment, IReadOnlyList<string> SecretValues)> BuildMobileAssignmentAsync(
        Execution execution, ExecutionTest test, MobileGridClaim claim, CancellationToken ct)
    {
        var version = test.TestCaseVersionId is not null
            ? await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, ct)
            : null;
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        if (version is null || testCase is null)
            throw new WorkerInfrastructureException("The bound test case version is unavailable.");

        var (values, secretValues, secretKeys) = await ResolveVariablesForEngineAsync(execution, ct);
        var parsed = TestStep.Parse(version.StructuredSteps?.RootElement);
        var workerSteps = new List<MobileStepDto>();
        try
        {
            foreach (var s in parsed)
            {
                string? target, value;
                bool valueHadSecret;
                try
                {
                    (target, _) = VariableModel.SubstituteSecretAware(s.Target, values, secretKeys);
                    (value, valueHadSecret) = VariableModel.SubstituteSecretAware(s.Value, values, secretKeys);
                }
                catch (ConflictException ex)
                {
                    throw new ConflictException(
                        $"Step {s.Order} ({s.Action}) references an undefined variable: {ex.Message}");
                }
                if (!valueHadSecret)
                    value = RedactMobileValue(s.Action, target, value);
                workerSteps.Add(new MobileStepDto(s.Order, s.Action, target, value));
            }
        }
        catch (ConflictException)
        {
            await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
            throw;
        }

        if (execution.MobileDevicePoolId is null || execution.MobileAppId is null)
        {
            await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
            throw new ConflictException("Mobile execution requires a device pool and application.");
        }
        var device = await _mobile.GetDeviceByIdAsync(claim.Slot.DeviceId, ct)
            ?? throw new WorkerInfrastructureException("The claimed mobile device is no longer available.");
        var app = await _mobile.GetAppByIdAsync(execution.MobileAppId.Value, ct);
        if (app is null)
        {
            await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
            throw new ConflictException("Mobile app does not belong to this project.");
        }

        string? downloadUrl = null;
        if (app.InstallPolicy is MobileInstallPolicy.Install or MobileInstallPolicy.Reinstall)
        {
            if (string.IsNullOrWhiteSpace(app.StorageKey))
            {
                await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
                throw new ConflictException("Install/Reinstall policies require a storage-backed binary.");
            }
            if (!_artifacts.IsConfigured)
            {
                await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
                throw new ConflictException("Artifact storage is not configured for the application binary.");
            }
            try
            {
                downloadUrl = await _artifacts.GetPresignedDownloadUrlAsync(
                    app.StorageKey, _execution.Value.ArtifactDownloadExpirySeconds, ct);
            }
            catch (Exception ex)
            {
                await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
                throw new ConflictException($"Application binary could not be resolved: {ex.GetType().Name}");
            }
        }

        MobileCapabilitiesDto capabilities;
        try
        {
            capabilities = _capabilities.Build(device, app, downloadUrl);
        }
        catch (ValidationException ex)
        {
            await ReleaseAsync(test.Id, nameof(GridAssignmentStatus.Released), ct);
            throw new ConflictException(ex.Message);
        }

        var timeouts = new MobileTimeoutsDto(
            (int)_execution.Value.ExecutionTimeout.TotalMilliseconds,
            (int)_execution.Value.StepTimeout.TotalMilliseconds);
        var pool = await _mobile.GetPoolByIdAsync(execution.MobileDevicePoolId.Value, ct);
        var platform = pool is null || pool.Platform == MobilePlatform.Android ? "android" : "ios";
        return (new MobileAssignmentDto(
            test.Id.ToString("N"), execution.Id.ToString("N"), "appium", platform,
            new MobileDeviceTargetDto(device.Udid, device.Model, device.PlatformVersion),
            new MobileAppTargetDto(app.PackageId, app.BundleId, app.Version,
                app.InstallPolicy.ToString(), app.LaunchActivity, app.DeepLink, DownloadUrl: downloadUrl),
            capabilities, workerSteps, timeouts,
            ScreenshotOnFailure: true,
            claim.AssignmentToken,
            await ResolveHealingPolicyAsync(execution.ProjectId, ct)), secretValues);
    }

    /// <summary>
    /// Slice 3C-4C: the worker-facing healing policy, mirroring the engine's
    /// Slice 11 resolver. Missing store rows and any lookup failure mean
    /// disabled — normal steps never pay healing overhead and pre-healing
    /// behavior is preserved exactly.
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
                SelfHealing.SelfHealingService.ParseStrategies(row.AllowedStrategies));
        }
        catch (Exception ex)
        {
            // Policy lookup must never fail an execution: heal nothing.
            _logger.LogWarning(ex, "Self-healing policy lookup failed; healing disabled for this mobile execution.");
            return null;
        }
    }

    /// <summary>
    /// Mobile mirror of the web redaction rule: inputText into a
    /// password-like target never travels readable. The Appium worker applies
    /// the same rule as defense in depth.
    /// </summary>
    private static string? RedactMobileValue(string action, string? target, string? value)
    {
        if (value is null) return null;
        if (!ExecutionValueRedactor.IsSensitiveTarget(target)) return value;
        return action.Trim().ToLowerInvariant() switch
        {
            "inputtext" => ExecutionValueRedactor.Mask,
            _ => value,
        };
    }

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
        catch (ConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConflictException(
                $"Variables could not be resolved for this environment: {ex.GetType().Name}");
        }
    }

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

    // ---------- shared outcome helpers (mirror ExecutionEngine) ----------

    private static bool IsAppium(ExecutionTest test)
        => string.Equals(test.Framework, "appium", StringComparison.OrdinalIgnoreCase);

    private async Task<(Execution, ExecutionTest)> LoadAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await _store.GetExecutionByIdAsync(executionId, ct)
            ?? throw new InvalidOperationException($"Execution {executionId} not found.");
        var test = (await _store.ListTestsByExecutionAsync(execution.Id, ct))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new InvalidOperationException($"Execution {executionId} has no tests.");
        return (execution, test);
    }

    private async Task ClearAttemptRowsAsync(Guid executionTestId, CancellationToken ct)
    {
        await _store.DeleteStepResultsAsync(executionTestId, ct);
        await _store.DeleteLogsAsync(executionTestId, ct);
        await _store.DeleteArtifactsAsync(executionTestId, ct);
    }

    private async Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
    {
        try
        {
            await _events.PublishAsync(executionId, eventName, payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mobile event {Event} for execution {ExecutionId} was not published.",
                eventName, executionId);
        }
    }

    private static WorkerExecutionOutcome AutomationError(string message, int attempt, long durationMs)
        => new(ExecutionTestStatus.Error, FailureClassification.AutomationFailure,
            nameof(ConflictException), Truncate(message), durationMs,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(), attempt);

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
    /// Slice 3C-4B-3: defense-in-depth re-masking of worker evidence text
    /// (mirrors the engine). Bounds are re-enforced; entries are truncated,
    /// never persisted unbounded.
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

    private static string Truncate(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    private static string? RedactTruncate(string? value)
        => value is null ? null : Truncate(SensitiveDataRedactor.Redact(value));

    private static string? RedactTruncate(string? value, IReadOnlyList<string>? secretValues)
        => value is null ? null : Truncate(SensitiveDataRedactor.Redact(
            VariableModel.MaskSecrets(value, secretValues) ?? string.Empty));
}
