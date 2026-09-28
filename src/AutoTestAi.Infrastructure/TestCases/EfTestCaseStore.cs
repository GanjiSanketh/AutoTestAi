using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoTestAi.Infrastructure.TestCases;

/// <summary>EF Core implementation of the test repository seam. No authorization here.</summary>
public sealed class EfTestCaseStore : ITestCaseStore
{
    private readonly AutoTestAiDbContext _db;

    public EfTestCaseStore(AutoTestAiDbContext db) => _db = db;

    public Task<int> CountAsync(
        Guid projectId, string? search, TestCaseStatusFilter filter, CancellationToken ct)
        => ApplyFilters(projectId, search, filter).CountAsync(ct);

    public async Task<IReadOnlyList<TestCase>> ListAsync(
        Guid projectId, string? search, TestCaseStatusFilter filter,
        int skip, int take, CancellationToken ct)
        => await ApplyFilters(projectId, search, filter)
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(
        IReadOnlyList<Guid> testCaseIds, CancellationToken ct)
    {
        if (testCaseIds.Count == 0) return new Dictionary<Guid, TestCaseVersion>();
        var versions = await _db.TestCaseVersions
            .Where(v => testCaseIds.Contains(v.TestCaseId))
            .OrderByDescending(v => v.VersionNumber)
            .AsNoTracking()
            .ToListAsync(ct);
        return versions
            .GroupBy(v => v.TestCaseId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public Task<TestCase?> GetByIdAsync(Guid testCaseId, CancellationToken ct)
        => _db.TestCases.FirstOrDefaultAsync(t => t.Id == testCaseId, ct);

    public Task<TestCase?> GetByKeyAsync(Guid projectId, string normalizedKey, CancellationToken ct)
        => _db.TestCases.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.TestKey == normalizedKey, ct);

    public Task AddTestCaseAsync(TestCase testCase, CancellationToken ct)
        => _db.TestCases.AddAsync(testCase, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    public Task<TestCaseVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken ct)
        // Tracked: ReviewAsync mutates ReviewStatus on the returned entity.
        => _db.TestCaseVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);

    public async Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid testCaseId, CancellationToken ct)
        => await _db.TestCaseVersions
            .Where(v => v.TestCaseId == testCaseId)
            .OrderByDescending(v => v.VersionNumber)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<TestCaseVersion> AddNextVersionAsync(
        Guid testCaseId, Func<int, TestCaseVersion> factory, CancellationToken ct)
    {
        // Unique index (test_case_id, version_number) is the backstop; retry the
        // read-modify-write on violation so concurrent edits cannot duplicate numbers.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var max = await _db.TestCaseVersions
                .Where(v => v.TestCaseId == testCaseId)
                .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;
            var version = factory(max + 1);
            _db.TestCaseVersions.Add(version);
            try
            {
                await _db.SaveChangesAsync(ct);
                return version;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 2)
            {
                _db.Entry(version).State = EntityState.Detached;
            }
        }
        throw new ConflictException("Version conflict, please retry the update.");
    }

    public Task AddVersionAsync(TestCaseVersion version, CancellationToken ct)
        => _db.TestCaseVersions.AddAsync(version, ct).AsTask();

    private IQueryable<TestCase> ApplyFilters(
        Guid projectId, string? search, TestCaseStatusFilter filter)
    {
        IQueryable<TestCase> query = _db.TestCases.Where(t => t.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Lower() comparisons: case-insensitive on PostgreSQL and InMemory alike.
            var term = search.Trim().ToLower();
            query = query.Where(t =>
                t.TestKey.ToLower().Contains(term) || t.Title.ToLower().Contains(term) ||
                (t.Module != null && t.Module.ToLower().Contains(term)));
        }
        if (filter.Status is not null)
            query = query.Where(t => t.Status.ToString() == filter.Status);
        if (filter.Priority is not null)
            query = query.Where(t => t.Priority.ToString() == filter.Priority);
        if (filter.Framework is not null)
        {
            var framework = filter.Framework.ToLower();
            query = query.Where(t => t.Framework != null && t.Framework.ToLower().Contains(framework));
        }
        if (filter.Platform is not null)
        {
            var platform = filter.Platform.ToLower();
            query = query.Where(t => t.Platform != null && t.Platform.ToLower().Contains(platform));
        }
        if (filter.ReviewStatus is not null)
        {
            var review = filter.ReviewStatus;
            query = query.Where(t => _db.TestCaseVersions
                .Where(v => v.TestCaseId == t.Id)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => v.ReviewStatus.ToString())
                .FirstOrDefault() == review);
        }
        return query;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException pg && pg.SqlState == "23505";
}
