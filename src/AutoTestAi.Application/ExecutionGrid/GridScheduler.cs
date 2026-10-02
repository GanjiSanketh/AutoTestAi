using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
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

    private const string MobileWorkerType = "appium";
    private const string MobileFramework = "appium";

    private static readonly IReadOnlySet<GridAssignmentStatus> ActiveLease =
        new HashSet<GridAssignmentStatus>
        {
            GridAssignmentStatus.Claimed,
            GridAssignmentStatus.Running,
        };

    private readonly IGridWorkerStore _workers;
    private readonly IGridAssignmentStore _assignments;
    private readonly IExecutionStore _executions;
    private readonly IMobileSlotLeaseService _slotLeases;
    private readonly IMobileRegistryStore _mobile;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly IOptions<GridOptions> _options;
    private readonly IOptions<MobileOptions> _mobileOptions;
    private readonly ILogger<GridScheduler> _logger;

    public GridScheduler(
        IGridWorkerStore workers,
        IGridAssignmentStore assignments,
        IExecutionStore executions,
        IMobileSlotLeaseService slotLeases,
        IMobileRegistryStore mobile,
        IDateTimeProvider clock,
        IAuditService audit,
        IOptions<GridOptions> options,
        IOptions<MobileOptions> mobileOptions,
        ILogger<GridScheduler> logger)
    {
        _workers = workers;
        _assignments = assignments;
        _executions = executions;
        _slotLeases = slotLeases;
        _mobile = mobile;
        _clock = clock;
        _audit = audit;
        _options = options;
        _mobileOptions = mobileOptions;
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

    public async Task<MobileGridClaim?> TryClaimMobileAsync(
        Guid executionId, IReadOnlySet<Guid> excludeWorkerIds, CancellationToken ct)
    {
        var settings = _options.Value;
        var mobileSettings = _mobileOptions.Value;
        var execution = await _executions.GetExecutionByIdAsync(executionId, ct)
            ?? throw new NotFoundException("Execution not found.");
        if (ExecutionTransitions.IsTerminal(execution.Status))
            throw new ConflictException($"Execution is already {execution.Status}.");
        var test = (await _executions.ListTestsByExecutionAsync(execution.Id, ct))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new NotFoundException("Execution test not found.");
        if (test.Status != ExecutionTestStatus.Queued)
            throw new ConflictException($"Execution test is {test.Status}, not Queued.");
        if (!string.Equals(test.Framework, MobileFramework, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("Execution test framework is not mobile (appium).");

        // Mobile requirements ride on the persisted execution references so
        // the start command stays untouched in this slice.
        if (execution.MobileDevicePoolId is null)
            throw new ConflictException("Mobile execution requires a device pool.");
        var pool = await _mobile.GetPoolByIdAsync(execution.MobileDevicePoolId.Value, ct);
        if (pool is null || pool.ProjectId != execution.ProjectId)
            throw new ConflictException("Mobile device pool does not belong to this project.");
        if (pool.Status != MobilePoolStatus.Active)
            throw new ConflictException("Mobile device pool is disabled.");
        MobileApp? app = null;
        if (execution.MobileAppId is not null)
        {
            app = await _mobile.GetAppByIdAsync(execution.MobileAppId.Value, ct);
            if (app is null || app.ProjectId != execution.ProjectId)
                throw new ConflictException("Mobile app does not belong to this project.");
            if (app.Platform != pool.Platform)
                throw new ConflictException("Mobile app platform must match the device pool platform.");
        }

        if (await _assignments.CountActiveAsync(ct) >= Math.Max(1, settings.GlobalMaxActiveStreams))
        {
            _logger.LogInformation("Mobile execution {ExecutionId} queued: global stream capacity reached.", executionId);
            return null;
        }
        if (await _assignments.CountActiveByProjectAsync(execution.ProjectId, ct)
            >= Math.Max(1, settings.ProjectMaxActiveStreams))
        {
            _logger.LogInformation("Mobile execution {ExecutionId} queued: project stream capacity reached.", executionId);
            return null;
        }

        // Opportunistic bounded slot recovery before scheduling (mirrors the
        // assignment reaper call on the web path).
        await _slotLeases.ReapExpiredSlotsAsync(ct);

        var now = _clock.UtcNow;
        var workers = (await _workers.ListAsync(ct))
            .Where(w => IsSchedulable(w, now, settings))
            .Where(w => string.Equals(w.WorkerType, MobileWorkerType, StringComparison.OrdinalIgnoreCase))
            .Where(w => string.Equals(w.Framework, MobileFramework, StringComparison.OrdinalIgnoreCase))
            .Where(w => w.ActiveAssignmentCount < Math.Max(1, w.Capacity))
            .Where(w => !excludeWorkerIds.Contains(w.Id))
            .OrderBy(w => w.ActiveAssignmentCount)
            .ThenBy(w => w.WorkerKey, StringComparer.Ordinal)
            .Take(Math.Max(1, mobileSettings.MaxClaimAttempts))
            .ToList();
        if (workers.Count == 0)
            return null;

        var devices = (await _mobile.ListDevicesAsync(execution.ProjectId, pool.Id, ct))
            .Where(d => d.Status == MobileDeviceStatus.Available)
            .ToDictionary(d => d.Id);
        var slots = (await _mobile.ListSlotsForClaimAsync(
                execution.ProjectId, pool.Id, Math.Max(1, mobileSettings.MaxClaimAttempts), ct))
            .Where(s => devices.TryGetValue(s.DeviceId, out var device) &&
                        device.PoolId == pool.Id &&
                        device.Platform == pool.Platform)
            .ToList();
        if (slots.Count == 0)
            return null;

        var attempts = 0;
        foreach (var worker in workers)
        {
            foreach (var slot in slots)
            {
                if (++attempts > Math.Max(1, mobileSettings.MaxClaimAttempts))
                    return null;
                var claim = await TryClaimMobilePairAsync(
                    execution, test, pool, worker, slot, settings, mobileSettings, now, ct);
                if (claim is not null)
                    return claim;
            }
        }
        _logger.LogInformation("Mobile execution {ExecutionId} queued: scheduling contention.", executionId);
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
        // Mobile piggyback: extend the linked slot lease in the same
        // transaction when present. Never fails assignment renewal.
        await TryExtendLinkedSlotAsync(assignment, now, ct);
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
            // Mobile piggyback: release the linked slot in the same
            // transaction when present. Never fails assignment release.
            await TryReleaseLinkedSlotAsync(assignment, ct);
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
            // Mobile piggyback: expire the linked slot in the same
            // transaction. The slot reaper frees it once safe.
            await TryExpireLinkedSlotAsync(assignment, ct);
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

    /// <summary>
    /// Mobile slot piggybacks (Phase 3 Slice 3C-3). Each is a no-op when no
    /// slot is linked, and none ever throws: assignment lifecycle always
    /// wins and the slot reaper converges independently.
    /// </summary>
    private async Task TryExtendLinkedSlotAsync(
        GridAssignment assignment, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var slot = await _slotLeases.FindByAssignmentAsync(assignment.Id, ct);
            if (slot is not null)
                _slotLeases.TryStageRenewal(slot, assignment.Id, now + _mobileOptions.Value.LeaseDuration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Linked slot renewal staging failed for assignment {AssignmentId}.",
                assignment.Id);
        }
    }

    private async Task TryReleaseLinkedSlotAsync(GridAssignment assignment, CancellationToken ct)
    {
        try
        {
            var slot = await _slotLeases.FindByAssignmentAsync(assignment.Id, ct);
            if (slot is not null)
                _slotLeases.TryStageRelease(slot, assignment.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Linked slot release staging failed for assignment {AssignmentId}.",
                assignment.Id);
        }
    }

    private async Task TryExpireLinkedSlotAsync(GridAssignment assignment, CancellationToken ct)
    {
        try
        {
            var slot = await _slotLeases.FindByAssignmentAsync(assignment.Id, ct);
            if (slot is not null)
                _slotLeases.StageExpire(slot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Linked slot expiry staging failed for assignment {AssignmentId}.",
                assignment.Id);
        }
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
    /// Mobile ownership transaction (Phase 3 Slice 3C-3). Stages worker
    /// capacity, slot claim, assignment insert, slot binding, and test
    /// binding, then commits ONCE: the shared scoped DbContext persists all
    /// tracked changes atomically. Any conflict rolls everything back and
    /// the next pair is tried. The web path above is untouched.
    /// </summary>
    private async Task<MobileGridClaim?> TryClaimMobilePairAsync(
        Execution execution,
        ExecutionTest test,
        MobileDevicePool pool,
        GridWorker candidate,
        MobileDeviceSlot scanned,
        GridOptions settings,
        MobileOptions mobileSettings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Reload tracked: scans are untracked, and every guard below must be
        // re-verified inside the transaction window.
        var worker = await _workers.GetByIdAsync(candidate.Id, ct);
        if (worker is null ||
            !IsSchedulable(worker, _clock.UtcNow, settings) ||
            !string.Equals(worker.WorkerType, MobileWorkerType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(worker.Framework, MobileFramework, StringComparison.OrdinalIgnoreCase) ||
            worker.ActiveAssignmentCount >= Math.Max(1, worker.Capacity))
            return null;
        var device = await _mobile.GetDeviceByIdAsync(scanned.DeviceId, ct);
        var livePool = await _mobile.GetPoolByIdAsync(pool.Id, ct);
        if (device is null || livePool is null ||
            livePool.Status != MobilePoolStatus.Active ||
            livePool.ProjectId != execution.ProjectId ||
            device.ProjectId != execution.ProjectId ||
            device.PoolId != pool.Id ||
            device.Platform != pool.Platform ||
            device.Status != MobileDeviceStatus.Available)
            return null;

        MobileDeviceSlot slot;
        Guid claimToken;
        try
        {
            (slot, claimToken) = await _slotLeases.PrepareClaimAsync(
                execution.ProjectId, scanned.Id, worker.Id, ct);
        }
        catch (ConflictException ex)
        {
            _logger.LogInformation(ex,
                "Mobile slot {SlotId} lost to a concurrent claimant; trying next pair.",
                scanned.Id);
            return null;
        }
        catch (NotFoundException)
        {
            return null;
        }

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
        _slotLeases.BindAssignment(slot, assignment.Id);
        test.AssignmentToken = assignment.AssignmentToken;
        test.AssignmentId = assignment.Id;
        worker.ActiveAssignmentCount++;
        worker.RowVersion++;
        worker.UpdatedAt = now;
        await _assignments.AddAsync(assignment, ct);
        try
        {
            // Single persistence boundary: worker capacity + slot claim +
            // assignment + slot/ test binding commit atomically. Any failure
            // rolls back the entire ownership unit (see §CRITICAL invariant).
            await _assignments.SaveChangesAsync(ct);
        }
        catch (ConflictException ex)
        {
            _logger.LogInformation(ex,
                "Mobile ownership conflict for execution {ExecutionId} (worker {WorkerId}, slot {SlotId}); trying next pair.",
                execution.Id, worker.Id, slot.Id);
            await AuditSlotAsync("mobile.slot_claim_conflict", execution, slot, null, ct);
            MobileMetrics.SlotClaimConflict(pool.Platform.ToString().ToLowerInvariant());
            return null;
        }
        _logger.LogInformation(
            "Mobile execution {ExecutionId} claimed on worker {WorkerId} slot {SlotId} (lease {AssignmentId}).",
            execution.Id, worker.Id, slot.Id, assignment.Id);
        await AuditSlotAsync("mobile.slot_claimed", execution, slot, assignment.Id.ToString(), ct);
        MobileMetrics.SlotClaimed(pool.Platform.ToString().ToLowerInvariant());
        return new MobileGridClaim(
            assignment, worker, slot, assignment.AssignmentToken, claimToken);
    }

    private async Task AuditSlotAsync(
        string action, Execution execution, MobileDeviceSlot slot, string? assignmentId, CancellationToken ct)
    {
        await _audit.RecordAsync(action, "mobile_device_slot", slot.Id.ToString(), execution.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                slotId = slot.Id,
                poolId = slot.PoolId,
                deviceId = slot.DeviceId,
                workerId = slot.WorkerId,
                assignmentId,
                status = slot.Status.ToString(),
            })), ct);
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
