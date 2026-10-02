using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>
/// Mobile slot lease operations (Phase 3 Slice 3C-3). Owns validation and
/// staged mutation of slot lease state; the scheduler owns the
/// SaveChanges boundary for initial ownership, so a committed claim can
/// never exist without its assignment. Standalone operations
/// (activate/renew/release/reap) commit themselves. Never throws for
/// reaper races; throws ConflictException/NotFoundException/ValidationException
/// for caller errors. ClaimToken and AssignmentToken stay distinct and
/// control-plane-only.
/// </summary>
public sealed class MobileSlotLeaseService : IMobileSlotLeaseService
{
    private static readonly IReadOnlySet<GridAssignmentStatus> ActiveAssignment =
        new HashSet<GridAssignmentStatus>
        {
            GridAssignmentStatus.Claimed,
            GridAssignmentStatus.Running,
        };

    private static readonly IReadOnlySet<MobileSlotStatus> LeasedSlot =
        new HashSet<MobileSlotStatus>
        {
            MobileSlotStatus.Claimed,
            MobileSlotStatus.Active,
        };

    private readonly IMobileRegistryStore _slots;
    private readonly IGridAssignmentStore _assignments;
    private readonly IDateTimeProvider _clock;
    private readonly MobileOptions _options;
    private readonly IAuditService _audit;
    private readonly ILogger<MobileSlotLeaseService> _logger;

    public MobileSlotLeaseService(
        IMobileRegistryStore slots,
        IGridAssignmentStore assignments,
        IDateTimeProvider clock,
        IOptions<MobileOptions> options,
        IAuditService audit,
        ILogger<MobileSlotLeaseService> logger)
    {
        _slots = slots;
        _assignments = assignments;
        _clock = clock;
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    public async Task<(MobileDeviceSlot Slot, Guid ClaimToken)> PrepareClaimAsync(
        Guid projectId, Guid slotId, Guid workerId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var slot = await _slots.GetSlotByIdAsync(slotId, ct);
        if (slot is null || slot.ProjectId != projectId)
            throw new NotFoundException("Device slot not found.");
        if (slot.Status == MobileSlotStatus.Free)
        {
            return ClaimFresh(slot, workerId, now);
        }
        if (slot.Status == MobileSlotStatus.Claimed || slot.Status == MobileSlotStatus.Active)
        {
            if (slot.ClaimExpiresAt is null || slot.ClaimExpiresAt > now)
                throw new ConflictException("Device slot is already claimed.");
            return await ReclaimExpiredAsync(slot, workerId, now, ct);
        }
        throw new ConflictException($"Device slot is {slot.Status} and cannot be claimed.");
    }

    public void BindAssignment(MobileDeviceSlot slot, Guid assignmentId)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Status != MobileSlotStatus.Claimed)
            throw new ConflictException("Only a claimed slot can be bound to an assignment.");
        slot.AssignmentId = assignmentId;
        slot.UpdatedAt = _clock.UtcNow;
    }

    public async Task ActivateAsync(Guid projectId, Guid slotId, Guid claimToken, CancellationToken ct)
    {
        var slot = await RequireOwnedAsync(projectId, slotId, claimToken, ct);
        if (slot.Status != MobileSlotStatus.Claimed)
            throw new ConflictException($"Only a claimed slot can be activated (slot is {slot.Status}).");
        slot.Status = MobileSlotStatus.Active;
        slot.UpdatedAt = _clock.UtcNow;
        await SaveAsync(ct);
        await AuditAsync("mobile.slot_activated", slot, null, ct);
    }

    public async Task RenewAsync(Guid projectId, Guid slotId, Guid claimToken, CancellationToken ct)
    {
        var slot = await RequireOwnedAsync(projectId, slotId, claimToken, ct);
        StageRenewal(slot, claimToken, _clock.UtcNow + _options.LeaseDuration);
        await SaveAsync(ct);
        await AuditAsync("mobile.slot_renewed", slot, null, ct);
        MobileMetrics.SlotRenewed(await PlatformOfAsync(slot, ct));
    }

    public void StageRenewal(MobileDeviceSlot slot, Guid claimToken, DateTimeOffset newExpiry)
    {
        ArgumentNullException.ThrowIfNull(slot);
        RequireToken(slot, claimToken);
        if (!LeasedSlot.Contains(slot.Status))
            throw new ConflictException($"Only a claimed or active slot can be renewed (slot is {slot.Status}).");
        if (slot.ClaimExpiresAt is not null && slot.ClaimExpiresAt <= _clock.UtcNow)
            throw new ConflictException("Slot lease has expired and cannot be renewed.");
        slot.ClaimExpiresAt = newExpiry;
        slot.UpdatedAt = _clock.UtcNow;
    }

    public async Task ReleaseAsync(Guid projectId, Guid slotId, Guid claimToken, CancellationToken ct)
    {
        var slot = await RequireOwnedAsync(projectId, slotId, claimToken, ct);
        StageRelease(slot, claimToken);
        await SaveAsync(ct);
        await AuditAsync("mobile.slot_released", slot, null, ct);
        MobileMetrics.SlotReleased(await PlatformOfAsync(slot, ct));
    }

    public void StageRelease(MobileDeviceSlot slot, Guid claimToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        RequireToken(slot, claimToken);
        if (!LeasedSlot.Contains(slot.Status))
            return; // idempotent: already terminal for leasing purposes
        slot.Status = MobileSlotStatus.Released;
        slot.ClaimToken = null;
        slot.ClaimExpiresAt = null;
        slot.WorkerId = null;
        slot.UpdatedAt = _clock.UtcNow;
    }

    public bool TryStageRenewal(MobileDeviceSlot slot, Guid assignmentId, DateTimeOffset newExpiry)
    {
        if (slot is null || slot.AssignmentId != assignmentId || !LeasedSlot.Contains(slot.Status))
            return false;
        if (slot.ClaimToken is null)
            return false;
        try
        {
            StageRenewal(slot, slot.ClaimToken.Value, newExpiry);
            return true;
        }
        catch (ConflictException)
        {
            return false;
        }
    }

    public bool TryStageRelease(MobileDeviceSlot slot, Guid assignmentId)
    {
        if (slot is null || slot.AssignmentId != assignmentId || !LeasedSlot.Contains(slot.Status))
            return false;
        if (slot.ClaimToken is null)
            return false;
        try
        {
            StageRelease(slot, slot.ClaimToken.Value);
            return true;
        }
        catch (ConflictException)
        {
            return false;
        }
    }

    public void StageExpire(MobileDeviceSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!LeasedSlot.Contains(slot.Status))
            return;
        slot.Status = MobileSlotStatus.Expired;
        slot.UpdatedAt = _clock.UtcNow;
    }

    public async Task<int> ReapExpiredSlotsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var reaped = 0;
        reaped += await ReapExpiredClaimsAsync(now, ct);
        reaped += await RecycleReleasedAsync(ct);
        return reaped;
    }

    public Task<MobileDeviceSlot?> FindByAssignmentAsync(Guid assignmentId, CancellationToken ct)
        => _slots.FindSlotByAssignmentAsync(assignmentId, ct);

    private (MobileDeviceSlot Slot, Guid ClaimToken) ClaimFresh(
        MobileDeviceSlot slot, Guid workerId, DateTimeOffset now)
    {
        var token = Guid.NewGuid();
        slot.Status = MobileSlotStatus.Claimed;
        slot.ClaimToken = token;
        slot.ClaimExpiresAt = now + _options.LeaseDuration;
        slot.WorkerId = workerId;
        slot.AssignmentId = null;
        slot.UpdatedAt = now;
        return (slot, token);
    }

    private async Task<(MobileDeviceSlot Slot, Guid ClaimToken)> ReclaimExpiredAsync(
        MobileDeviceSlot slot, Guid workerId, DateTimeOffset now, CancellationToken ct)
    {
        // Expired with no assignment linkage: only recoverable past the grace
        // period, since the atomic scheduler never commits this shape and its
        // presence indicates a legacy/bugged writer, never steady state.
        if (slot.AssignmentId is null)
        {
            if (now - slot.UpdatedAt < _options.ClaimGrace)
                throw new ConflictException("Device slot claim is stale but inside its recovery grace period.");
            var (fresh, token) = ClaimFresh(slot, workerId, now);
            await AuditAsync("mobile.slot_recovered", fresh, "unlinked-expired-claim", ct);
            MobileMetrics.SlotRecovered(await PlatformOfAsync(fresh, ct));
            return (fresh, token);
        }
        var assignment = await _assignments.GetByIdAsync(slot.AssignmentId.Value, ct);
        if (assignment is not null &&
            ActiveAssignment.Contains(assignment.Status) &&
            assignment.ExpiresAt > now)
            throw new ConflictException("Device slot is owned by a live assignment.");
        var (reclaimed, reclaimedToken) = ClaimFresh(slot, workerId, now);
        await AuditAsync("mobile.slot_recovered", reclaimed, "expired-claim", ct);
        MobileMetrics.SlotRecovered(await PlatformOfAsync(reclaimed, ct));
        return (reclaimed, reclaimedToken);
    }

    private async Task<int> ReapExpiredClaimsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await _slots.ListExpiredSlotsAsync(now, Math.Max(1, _options.ReapBatchSize), ct);
        var reaped = 0;
        foreach (var scanned in expired)
        {
            var slot = await _slots.GetSlotByIdAsync(scanned.Id, ct);
            if (slot is null || !LeasedSlot.Contains(slot.Status) ||
                slot.ClaimExpiresAt is null || slot.ClaimExpiresAt > _clock.UtcNow)
                continue;
            var platform = await PlatformOfAsync(slot, ct);
            if (slot.AssignmentId is null)
            {
                // Committed Claimed+NULL past grace only (see ReclaimExpiredAsync).
                if (_clock.UtcNow - slot.UpdatedAt < _options.ClaimGrace)
                    continue;
                FreeSlot(slot);
                if (await TrySaveAsync(ct))
                {
                    reaped++;
                    await AuditAsync("mobile.slot_recovered", slot, "unlinked-expired-claim", ct);
                    MobileMetrics.SlotRecovered(platform);
                }
                continue;
            }
            var assignment = await _assignments.GetByIdAsync(slot.AssignmentId.Value, ct);
            if (assignment is not null &&
                ActiveAssignment.Contains(assignment.Status) &&
                assignment.ExpiresAt > _clock.UtcNow)
            {
                // Linked assignment still live: never free on slot expiry
                // alone. The assignment lifecycle resolves it; audit loudly.
                _logger.LogWarning(
                    "Mobile slot {SlotId} expired while assignment {AssignmentId} is still active; keeping slot.",
                    slot.Id, assignment.Id);
                continue;
            }
            FreeSlot(slot);
            if (await TrySaveAsync(ct))
            {
                reaped++;
                await AuditAsync("mobile.slot_expired", slot, "expired-claim", ct);
                MobileMetrics.SlotExpired(platform, "expired-claim");
            }
        }
        return reaped;
    }

    private async Task<int> RecycleReleasedAsync(CancellationToken ct)
    {
        var released = await _slots.ListReleasedSlotsAsync(Math.Max(1, _options.ReapBatchSize), ct);
        var recycled = 0;
        foreach (var scanned in released)
        {
            var slot = await _slots.GetSlotByIdAsync(scanned.Id, ct);
            if (slot is null || slot.Status != MobileSlotStatus.Released)
                continue;
            if (slot.AssignmentId is not null)
            {
                var assignment = await _assignments.GetByIdAsync(slot.AssignmentId.Value, ct);
                if (assignment is not null &&
                    ActiveAssignment.Contains(assignment.Status) &&
                    assignment.ExpiresAt > _clock.UtcNow)
                    continue;
            }
            var platform = await PlatformOfAsync(slot, ct);
            FreeSlot(slot);
            if (await TrySaveAsync(ct))
            {
                recycled++;
                await AuditAsync("mobile.slot_recovered", slot, "released-recycled", ct);
                MobileMetrics.SlotRecovered(platform);
            }
        }
        return recycled;
    }

    private static void FreeSlot(MobileDeviceSlot slot)
    {
        slot.Status = MobileSlotStatus.Free;
        slot.ClaimToken = null;
        slot.ClaimExpiresAt = null;
        slot.WorkerId = null;
        slot.AssignmentId = null;
        slot.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<MobileDeviceSlot> RequireOwnedAsync(
        Guid projectId, Guid slotId, Guid claimToken, CancellationToken ct)
    {
        var slot = await _slots.GetSlotByIdAsync(slotId, ct);
        if (slot is null || slot.ProjectId != projectId)
            throw new NotFoundException("Device slot not found.");
        RequireToken(slot, claimToken);
        return slot;
    }

    private static void RequireToken(MobileDeviceSlot slot, Guid claimToken)
    {
        if (slot.ClaimToken is null || slot.ClaimToken.Value != claimToken)
            throw new ConflictException("Stale slot ownership token; the slot has been reclaimed.");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await _slots.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("Device slot changed concurrently; retry the operation.");
        }
    }

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await _slots.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex) || IsUniqueViolation(ex))
        {
            _logger.LogWarning(ex, "Mobile slot reaper lost a concurrent update; skipping.");
            return false;
        }
    }

    private async Task<string> PlatformOfAsync(MobileDeviceSlot slot, CancellationToken ct)
    {
        var pool = await _slots.GetPoolByIdAsync(slot.PoolId, ct);
        return pool?.Platform.ToString().ToLowerInvariant() ?? "unknown";
    }

    private Task AuditAsync(string action, MobileDeviceSlot slot, string? reason, CancellationToken ct)
        => _audit.RecordAsync(action, "mobile_device_slot", slot.Id.ToString(), slot.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                slotId = slot.Id,
                poolId = slot.PoolId,
                deviceId = slot.DeviceId,
                assignmentId = slot.AssignmentId,
                status = slot.Status.ToString(),
                reason,
            })), ct);

    private static bool IsConcurrencyConflict(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateConcurrencyException", StringComparison.Ordinal))
            return true;
        return ex.InnerException is not null && IsConcurrencyConflict(ex.InnerException);
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateException", StringComparison.Ordinal))
            return true;
        if (ex.InnerException is not null && IsUniqueViolation(ex.InnerException))
            return true;
        return ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}
