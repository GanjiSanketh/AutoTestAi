using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Persistence seam for the test repository. Implemented in Infrastructure
/// with EF Core; throws when no database is configured (fail closed at the API).
/// Methods do NOT authorize — <see cref="ITestCaseService"/> enforces that.
/// </summary>
public interface ITestCaseStore
{
    Task<int> CountAsync(
        Guid projectId, string? search, TestCaseStatusFilter filter, CancellationToken ct);

    Task<IReadOnlyList<TestCase>> ListAsync(
        Guid projectId, string? search, TestCaseStatusFilter filter,
        int skip, int take, CancellationToken ct);

    /// <summary>Latest version per test case id (single round-trip, no N+1).</summary>
    Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(
        IReadOnlyList<Guid> testCaseIds, CancellationToken ct);

    Task<TestCase?> GetByIdAsync(Guid testCaseId, CancellationToken ct);
    Task<TestCase?> GetByKeyAsync(Guid projectId, string normalizedKey, CancellationToken ct);
    Task AddTestCaseAsync(TestCase testCase, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);

    Task<TestCaseVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken ct);
    Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid testCaseId, CancellationToken ct);

    /// <summary>
    /// Atomically allocates the next sequential version number and persists the
    /// version built by <paramref name="factory"/>. Retries on unique-violation
    /// races; throws <see cref="Common.ConflictException"/> if retries exhaust.
    /// </summary>
    Task<TestCaseVersion> AddNextVersionAsync(
        Guid testCaseId, Func<int, TestCaseVersion> factory, CancellationToken ct);

    Task AddVersionAsync(TestCaseVersion version, CancellationToken ct);
}

/// <summary>Validated list-filter values (null = no filter).</summary>
public sealed record TestCaseStatusFilter(
    string? Status,
    string? Priority,
    string? Framework,
    string? Platform,
    string? ReviewStatus);
