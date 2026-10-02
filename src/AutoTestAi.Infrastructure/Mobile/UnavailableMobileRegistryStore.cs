using AutoTestAi.Application.Mobile;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.Mobile;

/// <summary>Fail-closed mobile registry seam when no database is configured.</summary>
public sealed class UnavailableMobileRegistryStore : IMobileRegistryStore
{
    private static Exception Unavailable() => new InvalidOperationException("Mobile registry storage is not configured.");
    public Task<MobileDevicePool?> GetPoolByIdAsync(Guid poolId, CancellationToken ct) => throw Unavailable();
    public Task<MobileDevicePool?> FindPoolByNameAsync(Guid projectId, string name, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid projectId, CancellationToken ct) => throw Unavailable();
    public Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct) => throw Unavailable();
    public Task<MobileDevice?> GetDeviceByIdAsync(Guid deviceId, CancellationToken ct) => throw Unavailable();
    public Task<MobileDevice?> FindDeviceByUdidAsync(Guid projectId, string udid, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MobileDevice>> ListDevicesAsync(Guid projectId, Guid? poolId, CancellationToken ct) => throw Unavailable();
    public Task<int> CountDevicesInPoolAsync(Guid poolId, CancellationToken ct) => throw Unavailable();
    public Task AddDeviceAsync(MobileDevice device, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsByDeviceAsync(Guid deviceId, CancellationToken ct) => throw Unavailable();
    public Task AddSlotAsync(MobileDeviceSlot slot, CancellationToken ct) => throw Unavailable();
    public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid slotId, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsForClaimAsync(Guid projectId, Guid poolId, int take, CancellationToken ct) => throw Unavailable();
    public Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid assignmentId, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct) => throw Unavailable();
    public Task<MobileApp?> GetAppByIdAsync(Guid appId, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid projectId, CancellationToken ct) => throw Unavailable();
    public Task AddAppAsync(MobileApp app, CancellationToken ct) => throw Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => throw Unavailable();
}
