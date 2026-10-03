using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Persistence seam for the mobile registry (Phase 3 Slice 3C-1/3C-2).
/// Implemented in Infrastructure; no authorization here. Slot lease fields
/// are structural only — no claiming service exists in this checkpoint.
/// </summary>
public interface IMobileRegistryStore
{
    Task<MobileDevicePool?> GetPoolByIdAsync(Guid poolId, CancellationToken ct);
    Task<MobileDevicePool?> FindPoolByNameAsync(Guid projectId, string name, CancellationToken ct);
    Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid projectId, CancellationToken ct);
    Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct);

    Task<MobileDevice?> GetDeviceByIdAsync(Guid deviceId, CancellationToken ct);
    Task<MobileDevice?> FindDeviceByUdidAsync(Guid projectId, string udid, CancellationToken ct);
    Task<IReadOnlyList<MobileDevice>> ListDevicesAsync(Guid projectId, Guid? poolId, CancellationToken ct);
    Task<int> CountDevicesInPoolAsync(Guid poolId, CancellationToken ct);
    Task AddDeviceAsync(MobileDevice device, CancellationToken ct);

    Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsByDeviceAsync(Guid deviceId, CancellationToken ct);
    Task AddSlotAsync(MobileDeviceSlot slot, CancellationToken ct);

    /// <summary>Tracked slot lookup for claim/renew/release/reap writes.</summary>
    Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid slotId, CancellationToken ct);

    /// <summary>
    /// Claim candidates in deterministic (SlotNumber, SlotId) order.
    /// Returns slots regardless of lease state; eligibility is decided by
    /// the lease service so races converge instead of being skipped blindly.
    /// </summary>
    Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsForClaimAsync(
        Guid projectId, Guid poolId, int take, CancellationToken ct);

    /// <summary>Reverse linkage: slot currently bound to an assignment.</summary>
    Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid assignmentId, CancellationToken ct);

    /// <summary>Reaper scan: non-free slots whose claim has expired, oldest first.</summary>
    Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct);

    /// <summary>Reaper scan: released slots awaiting recycling verification.</summary>
    Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct);

    /// <summary>Tracked session lookup for lifecycle writes.</summary>
    Task<MobileDeviceSession?> GetSessionByIdAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Reverse linkage: runtime session currently bound to an assignment.</summary>
    Task<MobileDeviceSession?> FindSessionByAssignmentAsync(Guid assignmentId, CancellationToken ct);

    /// <summary>
    /// Orphan sweep: non-closed sessions whose heartbeat went stale, oldest first.
    /// Bounded; the caller decides terminal transitions.
    /// </summary>
    Task<IReadOnlyList<MobileDeviceSession>> ListStaleSessionsAsync(DateTimeOffset staleBefore, int take, CancellationToken ct);

    Task AddSessionAsync(MobileDeviceSession session, CancellationToken ct);

    Task<MobileApp?> GetAppByIdAsync(Guid appId, CancellationToken ct);
    Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid projectId, CancellationToken ct);
    Task AddAppAsync(MobileApp app, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
