using AutoTestAi.Application.Mobile;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Mobile;

/// <summary>EF Core mobile registry seam (Phase 3 Slice 3C-1/3C-2). No authorization here.</summary>
public sealed class EfMobileRegistryStore : IMobileRegistryStore
{
    private readonly AutoTestAiDbContext _db;

    public EfMobileRegistryStore(AutoTestAiDbContext db) => _db = db;

    public Task<MobileDevicePool?> GetPoolByIdAsync(Guid poolId, CancellationToken ct)
        => _db.MobileDevicePools.FirstOrDefaultAsync(p => p.Id == poolId, ct);

    public Task<MobileDevicePool?> FindPoolByNameAsync(Guid projectId, string name, CancellationToken ct)
        => _db.MobileDevicePools.FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Name == name, ct);

    public async Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid projectId, CancellationToken ct)
        => await _db.MobileDevicePools.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

    public async Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct)
        => await _db.MobileDevicePools.AddAsync(pool, ct);

    public Task<MobileDevice?> GetDeviceByIdAsync(Guid deviceId, CancellationToken ct)
        => _db.MobileDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);

    public Task<MobileDevice?> FindDeviceByUdidAsync(Guid projectId, string udid, CancellationToken ct)
        => _db.MobileDevices.FirstOrDefaultAsync(d => d.ProjectId == projectId && d.Udid == udid, ct);

    public async Task<IReadOnlyList<MobileDevice>> ListDevicesAsync(Guid projectId, Guid? poolId, CancellationToken ct)
        => await _db.MobileDevices.AsNoTracking()
            .Where(d => d.ProjectId == projectId && (poolId == null || d.PoolId == poolId))
            .OrderBy(d => d.Model).ThenBy(d => d.Id)
            .ToListAsync(ct);

    public Task<int> CountDevicesInPoolAsync(Guid poolId, CancellationToken ct)
        => _db.MobileDevices.CountAsync(d => d.PoolId == poolId, ct);

    public async Task AddDeviceAsync(MobileDevice device, CancellationToken ct)
        => await _db.MobileDevices.AddAsync(device, ct);

    public async Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsByDeviceAsync(Guid deviceId, CancellationToken ct)
        => await _db.MobileDeviceSlots.AsNoTracking()
            .Where(s => s.DeviceId == deviceId)
            .OrderBy(s => s.SlotNumber)
            .ToListAsync(ct);

    public async Task AddSlotAsync(MobileDeviceSlot slot, CancellationToken ct)
        => await _db.MobileDeviceSlots.AddAsync(slot, ct);

    public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid slotId, CancellationToken ct)
        => _db.MobileDeviceSlots.FirstOrDefaultAsync(s => s.Id == slotId, ct);

    public async Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsForClaimAsync(
        Guid projectId, Guid poolId, int take, CancellationToken ct)
        => await _db.MobileDeviceSlots.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.PoolId == poolId)
            .OrderBy(s => s.SlotNumber)
            .ThenBy(s => s.Id)
            .Take(take)
            .ToListAsync(ct);

    public Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid assignmentId, CancellationToken ct)
        => _db.MobileDeviceSlots.FirstOrDefaultAsync(s => s.AssignmentId == assignmentId, ct);

    public async Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct)
        => await _db.MobileDeviceSlots.AsNoTracking()
            .Where(s => s.Status != Domain.Enums.MobileSlotStatus.Free &&
                        s.Status != Domain.Enums.MobileSlotStatus.Released &&
                        s.ClaimExpiresAt != null && s.ClaimExpiresAt <= now)
            .OrderBy(s => s.ClaimExpiresAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct)
        => await _db.MobileDeviceSlots.AsNoTracking()
            .Where(s => s.Status == Domain.Enums.MobileSlotStatus.Released)
            .OrderBy(s => s.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<MobileApp?> GetAppByIdAsync(Guid appId, CancellationToken ct)
        => _db.MobileApps.FirstOrDefaultAsync(a => a.Id == appId, ct);

    public async Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid projectId, CancellationToken ct)
        => await _db.MobileApps.AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.Platform).ThenBy(a => a.Name)
            .ToListAsync(ct);

    public async Task AddAppAsync(MobileApp app, CancellationToken ct)
        => await _db.MobileApps.AddAsync(app, ct);

    public Task<MobileDeviceSession?> GetSessionByIdAsync(Guid sessionId, CancellationToken ct)
        => _db.MobileDeviceSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public Task<MobileDeviceSession?> FindSessionByAssignmentAsync(Guid assignmentId, CancellationToken ct)
        => _db.MobileDeviceSessions.FirstOrDefaultAsync(s => s.AssignmentId == assignmentId, ct);

    public async Task<IReadOnlyList<MobileDeviceSession>> ListStaleSessionsAsync(DateTimeOffset staleBefore, int take, CancellationToken ct)
        => await _db.MobileDeviceSessions.AsNoTracking()
            .Where(s => s.Status != Domain.Enums.MobileSessionStatus.Closed &&
                        s.Status != Domain.Enums.MobileSessionStatus.Orphaned &&
                        s.LastHeartbeatAt != null && s.LastHeartbeatAt <= staleBefore)
            .OrderBy(s => s.LastHeartbeatAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task AddSessionAsync(MobileDeviceSession session, CancellationToken ct)
        => await _db.MobileDeviceSessions.AddAsync(session, ct);

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}
