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

    Task<MobileApp?> GetAppByIdAsync(Guid appId, CancellationToken ct);
    Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid projectId, CancellationToken ct);
    Task AddAppAsync(MobileApp app, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
