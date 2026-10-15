using AutoTestAi.Application.Maintenance;
using AutoTestAi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Maintenance;

/// <summary>EF Core maintenance scan schedule seam (Phase 4 Slice 10).</summary>
public sealed class EfMaintenanceScanScheduleStore : IMaintenanceScanScheduleStore
{
    private readonly Persistence.AutoTestAiDbContext _db;

    public EfMaintenanceScanScheduleStore(Persistence.AutoTestAiDbContext db) => _db = db;

    public Task<MaintenanceScanSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct)
        => _db.MaintenanceScanSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);

    public Task<MaintenanceScanSchedule?> GetByProjectIdAsync(Guid projectId, CancellationToken ct)
        => _db.MaintenanceScanSchedules.FirstOrDefaultAsync(s => s.ProjectId == projectId, ct);

    public Task AddAsync(MaintenanceScanSchedule schedule, CancellationToken ct)
        => _db.MaintenanceScanSchedules.AddAsync(schedule, ct).AsTask();

    public Task RemoveAsync(MaintenanceScanSchedule schedule, CancellationToken ct)
    {
        _db.MaintenanceScanSchedules.Remove(schedule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}
