using AutoTestAi.Application.Maintenance;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.Maintenance;

/// <summary>Fail-closed scan-schedule seam when no database is configured.</summary>
public sealed class UnavailableMaintenanceScanScheduleStore : IMaintenanceScanScheduleStore
{
    private static Exception Unavailable() => new InvalidOperationException("Maintenance scan schedule storage is not configured.");
    public Task<MaintenanceScanSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct) => throw Unavailable();
    public Task<MaintenanceScanSchedule?> GetByProjectIdAsync(Guid projectId, CancellationToken ct) => throw Unavailable();
    public Task AddAsync(MaintenanceScanSchedule schedule, CancellationToken ct) => throw Unavailable();
    public Task RemoveAsync(MaintenanceScanSchedule schedule, CancellationToken ct) => throw Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => throw Unavailable();
}
