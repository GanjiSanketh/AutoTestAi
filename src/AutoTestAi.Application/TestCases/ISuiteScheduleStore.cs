using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Schedule store abstraction (Phase 4 Slice 9B).
/// Methods do NOT authorize — <see cref="ISuiteScheduleService"/> enforces that.
/// </summary>
public interface ISuiteScheduleStore
{
    Task<TestSuiteSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct);

    Task<IReadOnlyList<TestSuiteSchedule>> ListBySuiteAsync(Guid suiteId, bool includeArchived, CancellationToken ct);

    Task<IReadOnlyList<TestSuiteSchedule>> ListActiveBySuiteAsync(Guid suiteId, CancellationToken ct);

    /// <summary>Exact (case-insensitive) name match within the project, optionally excluding one schedule.</summary>
    Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeScheduleId, CancellationToken ct);

    Task AddAsync(TestSuiteSchedule schedule, CancellationToken ct);

    Task RemoveAsync(TestSuiteSchedule schedule, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
