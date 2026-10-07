using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.TestCases;

/// <summary>
/// Fail-closed store used when no database is configured: every operation
/// surfaces as a dependency failure (503) rather than inventing data.
/// </summary>
public sealed class UnavailableSuiteScheduleStore : ISuiteScheduleStore
{
    private static Task<T> Unavailable<T>() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    private static Task Unavailable() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    public Task<TestSuiteSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct)
        => Unavailable<TestSuiteSchedule?>();
    public Task<IReadOnlyList<TestSuiteSchedule>> ListBySuiteAsync(Guid suiteId, bool includeArchived, CancellationToken ct)
        => Unavailable<IReadOnlyList<TestSuiteSchedule>>();
    public Task<IReadOnlyList<TestSuiteSchedule>> ListActiveBySuiteAsync(Guid suiteId, CancellationToken ct)
        => Unavailable<IReadOnlyList<TestSuiteSchedule>>();
    public Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeScheduleId, CancellationToken ct)
        => Unavailable<bool>();
    public Task AddAsync(TestSuiteSchedule schedule, CancellationToken ct) => Unavailable();
    public Task RemoveAsync(TestSuiteSchedule schedule, CancellationToken ct) => Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => Unavailable();
}
