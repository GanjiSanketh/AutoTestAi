using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.TestCases;

/// <summary>
/// Fail-closed store used when no database is configured: every operation
/// surfaces as a dependency failure (503) rather than inventing data.
/// </summary>
public sealed class UnavailableTestCaseStore : ITestCaseStore
{
    private static Task<T> Unavailable<T>() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    private static Task Unavailable() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    public Task<int> CountAsync(Guid projectId, string? search, TestCaseStatusFilter filter, CancellationToken ct)
        => Unavailable<int>();
    public Task<IReadOnlyList<TestCase>> ListAsync(
        Guid projectId, string? search, TestCaseStatusFilter filter, int skip, int take, CancellationToken ct)
        => Unavailable<IReadOnlyList<TestCase>>();
    public Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(
        IReadOnlyList<Guid> testCaseIds, CancellationToken ct)
        => Unavailable<IReadOnlyDictionary<Guid, TestCaseVersion>>();
    public Task<TestCase?> GetByIdAsync(Guid testCaseId, CancellationToken ct) => Unavailable<TestCase?>();
    public Task<TestCase?> GetByKeyAsync(Guid projectId, string normalizedKey, CancellationToken ct)
        => Unavailable<TestCase?>();
    public Task AddTestCaseAsync(TestCase testCase, CancellationToken ct) => Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => Unavailable();
    public Task<TestCaseVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken ct)
        => Unavailable<TestCaseVersion?>();
    public Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid testCaseId, CancellationToken ct)
        => Unavailable<IReadOnlyList<TestCaseVersion>>();
    public Task<TestCaseVersion> AddNextVersionAsync(
        Guid testCaseId, Func<int, TestCaseVersion> factory, CancellationToken ct)
        => Unavailable<TestCaseVersion>();
    public Task AddVersionAsync(TestCaseVersion version, CancellationToken ct) => Unavailable();
}
