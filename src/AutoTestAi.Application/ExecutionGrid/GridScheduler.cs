using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Executions;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>
/// Deterministic grid scheduler (Phase 2 Slice 9). Claims are atomic:
/// the unique filtered lease index plus the worker concurrency token make
/// lost races surface as conflicts, which the scheduler resolves by trying
/// the next eligible worker. Least-loaded wins; WorkerKey breaks ties.
/// </summary>
public sealed class GridScheduler : IGridScheduler
{
    private const int MaxClaimAttempts = 8;
    private const int MaxReapBatch = 100;

    private static readonly IReadOnlySet<GridAssignmentStatus> ActiveLease =
        new HashSet<GridAssignmentStatus>
        {
            GridAssignmentStatus.Claimed,
            GridAssignmentStatus.Running,
        };

    private readonly IGridWorkerStore _workers;
    private readonly IGridAssignmentStore _assignments;
    private readonly IExecutionStore _executions;
    private readonly IDateTimeProvider _clock;
    private readonly IOptions<GridOptions> _options;
    private readonly ILogger<GridScheduler> _logger;

    public GridScheduler(
        IGridWorkerStore workers,
        IGridAssignmentStore assignments,
        IExecutionStore executions,
        IDateTimeProvider clock,
        IOptions<GridOptions> options,
        ILogger<GridScheduler> logger)
    {
        _workers = workers;
        _assignments = assignments;
        _executions = executions;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<GridClaim?> TryClaimAsync(
        Guid executionId, IReadOnlySet<Guid> excludeWorkerIds, CancellationToken ct)
    {
        var settings = _options.Value;
        var execution = await _executions.GetExecutionByIdAsync(executionId, ct)
            ?? throw new NotFoundException("Execution not found.");
        if (ExecutionTransitions.IsTerminal(execution.Status))
            throw new ConflictException($"Execution is already {execution.Status}.");
        var test = (await _executions.ListTestsByExecutionAsync(execution.Id, ct))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new NotFoundException("Execution test not found.");
        if (test.Status != ExecutionTestStatus.Queued)
            throw new ConflictException($"Execution test is {test.Status}, not Queued.");

        // Opportunistic bounded expiry before scheduling (no background loop).
        await ReapExpiredLeasesAsync(ct);

        if (await _assignments.CountActiveAsync(ct) >= Math.Max(1, settings.GlobalMaxActiveStreams))
        {
            _logger.LogInformation("Execution {ExecutionId} queued: global stream capacity reached.", executionId);
            return null;
        }
        if (await _assignments.CountActiveByProjectAsync(execution.ProjectId, ct)
            >= Math.Max(1, settings.ProjectMaxActiveStreams))
        {
            _logger.LogInformation("Execution {ExecutionId} queued: project stream capacity reached.", executionId);
            return null;
        }

        var now = _clock.UtcNow;
        var candidates = (await _workers.ListAsync(ct))
            .Where(w => IsSchedulable(w, now, settings))
            .Where(w => CapabilityMatches(w, test.Framework, test.Browser))
            .Where(w => w.ActiveAssignmentCount < Math.Max(1, w.Capacity))
            .Where(w => !excludeWorkerIds.Contains(w.Id))
            .OrderBy(w => w.ActiveAssignmentCount)
            .ThenBy(w => w.WorkerKey, StringComparer.Ordinal)
            .Take(MaxClaimAttempts)
            .ToList();
        if (candidates.Count == 0)
            return null;

        foreach (var worker in candidates)
        {
            var claim = await TryClaimOnWorkerAsync(execution, test, worker, settings, now, ct);
            if (claim is not null)
                return claim;
        }
        _logger.LogInformation("Execution {ExecutionId} queued: scheduling contention.", executionId);
        return null;
    }

    public async Task RenewLeaseAsync(Guid assignmentId, CancellationToken ct)
    {
        var settings = _options.Value;
        var assignment = await _assignments.GetByIdAsync(assignmentId, ct)
            ?? throw new NotFoundException("Assignment lease not found.");
        if (!ActiveLease.Contains(assignment.Status))
            throw new ConflictException($"Assignment is {assignment.Status} and cannot be renewed.");
        var now = _clock.UtcNow;
        if (assignment.ExpiresAt <= now)
            throw new ConflictException("Assignment lease has expired and cannot be renewed.");
        var worker = await _workers.GetByIdAsync(assignment.WorkerId, ct);
        if (worker is null || worker.Status == GridWorkerStatus.Disabled)
            throw new ConflictException("The owning worker can no longer hold this lease.");
        assignment.ExpiresAt = now + settings.LeaseDuration;
        assignment.LastRenewedAt = now;
        try
        {
            await _assignments.SaveChangesAsync(ct);
        }
        catch (ConflictException ex)
        {
            _logger.LogInformation(ex, "Lease {AssignmentId} changed concurrently; renewal was not applied.",
                assignment.Id);
            throw new ConflictException("Assignment lease changed concurrently; renewal was not applied.");
        }
    }

    public async Task ReleaseLeaseAsync(
        Guid executionTestId, string terminalStatus, CancellationToken ct)
    {
        if (!Enum.TryParse<GridAssignmentStatus>(terminalStatus, ignoreCase: true, out var terminal) ||
            ActiveLease.Contains(terminal) || terminal == GridAssignmentStatus.Pending)
            throw new ValidationException("Terminal lease status is invalid.",
                new[] { new FieldError("status", "Lease status must be Completed, Released, Expired or Cancelled.") });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var assignment = await _assignments.FindActiveByTestAsync(executionTestId, ct);
            if (assignment is null)
                return; // idempotent: nothing active to release
            assignment.Status = terminal;
            await DecrementAsync(assignment.WorkerId, ct);
            try
            {
                await _assignments.SaveChangesAsync(ct);
                _logger.LogInformation("Lease {AssignmentId} released as {Status}.", assignment.Id, terminal);
                return;
            }
            catch (ConflictException)
            {
                // Lost a counter race; reload and reapply once.
            }
        }
    }

    public async Task<int> ReapExpiredLeasesAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var expired = await _assignments.ListExpiredActiveAsync(now, MaxReapBatch, ct);
        if (expired.Count == 0)
            return 0;
        var workers = (await _workers.ListAsync(ct)).ToDictionary(w => w.Id);
        var reaped = 0;
        foreach (var assignment in expired)
        {
            // Sufficient evidence of worker loss only: stale beyond the offline
            // threshold, missing, or administratively disabled. A merely slow
            // worker keeps its lease until it is provably gone.
            var lost = !workers.TryGetValue(assignment.WorkerId, out var worker) ||
                worker.Status == GridWorkerStatus.Disabled ||
                worker.LastHeartbeatAt is null ||
                now - worker.LastHeartbeatAt.Value >= _options.Value.OfflineThreshold;
            if (!lost)
                continue;
            assignment.Status = GridAssignmentStatus.Expired;
            await DecrementAsync(assignment.WorkerId, ct);
            await RecoverExecutionAsync(assignment, ct);
            try
            {
                await _assignments.SaveChangesAsync(ct);
                reaped++;
                _logger.LogWarning("Lease {AssignmentId} expired after worker loss; execution {ExecutionId} requeued.",
                    assignment.Id, assignment.ExecutionId);
            }
            catch (ConflictException ex)
            {
                _logger.LogWarning(ex, "Lease {AssignmentId} changed concurrently during reap.", assignment.Id);
            }
        }
        return reaped;
    }

    public Task<int> CountActiveAssignmentsAsync(CancellationToken ct)
        => _assignments.CountActiveAsync(ct);

    // ---------- internals ----------

    private async Task<GridClaim?> TryClaimOnWorkerAsync(
        Execution execution, ExecutionTest test, GridWorker candidate,
        GridOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        // Reload tracked: the candidate list is untracked, and the counter
        // must be fresh for the optimistic-concurrency guard to mean anything.
        var worker = await _workers.GetByIdAsync(candidate.Id, ct);
        if (worker is null ||
            !IsSchedulable(worker, _clock.UtcNow, settings) ||
            worker.ActiveAssignmentCount >= Math.Max(1, worker.Capacity))
            return null;
        var assignment = new GridAssignment
        {
            ExecutionId = execution.Id,
            ExecutionTestId = test.Id,
            WorkerId = worker.Id,
            Status = GridAssignmentStatus.Claimed,
            Attempt = test.Attempt,
            AcquiredAt = now,
            ExpiresAt = now + settings.LeaseDuration,
            LastRenewedAt = now,
            WorkerAssignmentRef = test.Id.ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        worker.ActiveAssignmentCount++;
        worker.RowVersion++;
        worker.UpdatedAt = now;
        await _assignments.AddAsync(assignment, ct);
        try
        {
            await _assignments.SaveChangesAsync(ct);
            // Store the assignment token in the execution test for fencing
            test.AssignmentToken = assignment.AssignmentToken;
            test.AssignmentId = assignment.Id;
            await _executions.SaveChangesAsync(ct);
        }
        catch (ConflictException ex)
        {
            // Lost a race (duplicate active lease, capacity overclaim, or a
            // concurrent worker update). The next candidate is tried instead.
            _logger.LogInformation(ex,
                "Scheduling conflict claiming execution {ExecutionId} on worker {WorkerId}; trying next worker.",
                execution.Id, worker.Id);
            return null;
        }
        _logger.LogInformation("Execution {ExecutionId} claimed on worker {WorkerId} (lease {AssignmentId}).",
            execution.Id, worker.Id, assignment.Id);
        return new GridClaim(assignment, worker, assignment.AssignmentToken);
    }

    private async Task DecrementAsync(Guid workerId, CancellationToken ct)
    {
        var worker = await _workers.GetByIdAsync(workerId, ct);
        if (worker is not null)
        {
            worker.ActiveAssignmentCount = Math.Max(0, worker.ActiveAssignmentCount - 1);
            worker.RowVersion++;
            worker.UpdatedAt = _clock.UtcNow;
        }
    }

    /// <summary>
    /// Makes a worker-loss execution runnable again without consuming the
    /// engine's single infrastructure retry: the test returns to Queued and
    /// Temporal's activity retry policy bounds total re-execution.
    /// </summary>
    private async Task RecoverExecutionAsync(GridAssignment assignment, CancellationToken ct)
    {
        var execution = await _executions.GetExecutionByIdAsync(assignment.ExecutionId, ct);
        var test = await _executions.GetExecutionTestByIdAsync(assignment.ExecutionTestId, ct);
        if (execution is null || test is null)
            return;
        if (ExecutionTransitions.IsTestTerminal(test.Status))
            return; // engine already finalized concurrently; leave history alone
        var now = _clock.UtcNow;
        if (ExecutionTransitions.IsValidTestTransition(
                test.Status, ExecutionTestStatus.Queued))
        {
            test.Status = ExecutionTestStatus.Queued;
            test.UpdatedAt = now;
        }
        if (ExecutionTransitions.IsValidTransition(
                execution.Status, ExecutionStatus.Queued))
        {
            execution.Status = ExecutionStatus.Queued;
            execution.StartedAt = null;
            execution.UpdatedAt = now;
        }
    }

    internal static bool IsSchedulable(GridWorker worker, DateTimeOffset now, GridOptions settings)
        => worker.Status is GridWorkerStatus.Available
            or GridWorkerStatus.Registered
            or GridWorkerStatus.Busy
            && worker.LastHeartbeatAt is not null
            && now - worker.LastHeartbeatAt.Value < settings.HeartbeatTimeout;

    internal static bool CapabilityMatches(GridWorker worker, string? framework, string? browser)
    {
        if (!string.IsNullOrWhiteSpace(framework) &&
            !string.Equals(worker.Framework, framework.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(browser) &&
            !worker.Browsers.Contains(browser.Trim(), StringComparer.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
