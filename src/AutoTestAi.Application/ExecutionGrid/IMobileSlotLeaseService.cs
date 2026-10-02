using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>
/// Mobile slot lease operations (Phase 3 Slice 3C-3). Validation and staged
/// mutation only for initial ownership: the scheduler owns the SaveChanges
/// boundary, so this interface deliberately exposes no independently
/// committed Claim operation. Standalone operations (activate/renew/release/
/// reap) commit themselves. ClaimToken and AssignmentToken stay distinct.
/// </summary>
public interface IMobileSlotLeaseService
{
    /// <summary>
    /// Validates and stages a slot claim (Free, or safely reclaimable
    /// expired) WITHOUT saving. The caller must commit via the scheduler
    /// ownership transaction. Returns the staged slot and its new token.
    /// </summary>
    Task<(MobileDeviceSlot Slot, Guid ClaimToken)> PrepareClaimAsync(
        Guid projectId, Guid slotId, Guid workerId, CancellationToken ct);

    /// <summary>Binds a staged claimed slot to its assignment. No save.</summary>
    void BindAssignment(MobileDeviceSlot slot, Guid assignmentId);

    Task ActivateAsync(Guid projectId, Guid slotId, Guid claimToken, CancellationToken ct);

    Task RenewAsync(Guid projectId, Guid slotId, Guid claimToken, CancellationToken ct);

    /// <summary>Validates and stages a renewal WITHOUT saving (scheduler piggyback).</summary>
    void StageRenewal(MobileDeviceSlot slot, Guid claimToken, DateTimeOffset newExpiry);

    Task ReleaseAsync(Guid projectId, Guid slotId, Guid claimToken, CancellationToken ct);

    /// <summary>Validates and stages a release WITHOUT saving (scheduler piggyback).</summary>
    void StageRelease(MobileDeviceSlot slot, Guid claimToken);

    /// <summary>Best-effort staging renewal; false when not applicable. Never throws.</summary>
    bool TryStageRenewal(MobileDeviceSlot slot, Guid assignmentId, DateTimeOffset newExpiry);

    /// <summary>Best-effort staging release; false when not applicable. Never throws.</summary>
    bool TryStageRelease(MobileDeviceSlot slot, Guid assignmentId);

    /// <summary>Marks a leased slot expired. No save.</summary>
    void StageExpire(MobileDeviceSlot slot);

    /// <summary>Reclaims safely-reclaimable expired/released slots. Never throws for races.</summary>
    Task<int> ReapExpiredSlotsAsync(CancellationToken ct);

    Task<MobileDeviceSlot?> FindByAssignmentAsync(Guid assignmentId, CancellationToken ct);
}
