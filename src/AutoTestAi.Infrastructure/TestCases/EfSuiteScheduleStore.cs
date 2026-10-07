using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.TestCases;

/// <summary>EF Core implementation of the suite schedule store (Phase 4 Slice 9B).</summary>
public sealed class EfSuiteScheduleStore : ISuiteScheduleStore
{
    private readonly AutoTestAiDbContext _db;

    public EfSuiteScheduleStore(AutoTestAiDbContext db) => _db = db;

    public Task<TestSuiteSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct)
        => _db.TestSuiteSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);

    public async Task<IReadOnlyList<TestSuiteSchedule>> ListBySuiteAsync(Guid suiteId, bool includeArchived, CancellationToken ct)
    {
        var query = _db.TestSuiteSchedules.Where(s => s.SuiteId == suiteId);
        if (!includeArchived)
            query = query.Where(s => s.Status != Domain.Enums.ScheduleStatus.Archived);
        return await query
            .OrderBy(s => s.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Tracked: the suite-archive cascade mutates Status on the returned
    /// entities before SaveChanges.
    /// </summary>
    public async Task<IReadOnlyList<TestSuiteSchedule>> ListActiveBySuiteAsync(Guid suiteId, CancellationToken ct)
        => await _db.TestSuiteSchedules
            .Where(s => s.SuiteId == suiteId && s.Status == Domain.Enums.ScheduleStatus.Active)
            .ToListAsync(ct);

    public async Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeScheduleId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        var query = _db.TestSuiteSchedules
            .Where(s => s.ProjectId == projectId && s.Name.ToLower() == normalized);
        if (excludeScheduleId.HasValue)
            query = query.Where(s => s.Id != excludeScheduleId.Value);
        return await query.AnyAsync(ct);
    }

    public async Task AddAsync(TestSuiteSchedule schedule, CancellationToken ct)
        => await _db.TestSuiteSchedules.AddAsync(schedule, ct);

    public Task RemoveAsync(TestSuiteSchedule schedule, CancellationToken ct)
    {
        _db.TestSuiteSchedules.Remove(schedule);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct)
        => await _db.SaveChangesAsync(ct);
}
