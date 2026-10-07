using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.TestCases;

/// <summary>EF Core implementation of the suite store (Phase 4 Slice 9A).</summary>
public sealed class EfSuiteStore : ISuiteStore
{
    private readonly AutoTestAiDbContext _db;

    public EfSuiteStore(AutoTestAiDbContext db) => _db = db;

    public async Task<int> CountAsync(Guid projectId, string? search, string? status, CancellationToken ct)
    {
        var query = _db.TestSuites.Where(s => s.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(s => s.Name.ToLower().Contains(term) || (s.Description != null && s.Description.ToLower().Contains(term)));
        }

        if (TryParseStatus(status, out var statusEnum))
            query = query.Where(s => s.Status == statusEnum);

        return await query.CountAsync(ct);
    }

    public async Task<IReadOnlyList<SuiteListItemDto>> ListAsync(Guid projectId, string? search, string? status, int skip, int take, CancellationToken ct)
    {
        var query = _db.TestSuites
            .Where(s => s.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(s => s.Name.ToLower().Contains(term) || (s.Description != null && s.Description.ToLower().Contains(term)));
        }

        if (TryParseStatus(status, out var statusEnum))
            query = query.Where(s => s.Status == statusEnum);

        query = query.OrderByDescending(s => s.UpdatedAt);

        var page = await query
            .Skip(skip)
            .Take(take)
            .Select(s => new { s.Id, s.ProjectId, s.Name, s.Description, s.Status, s.UpdatedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        if (page.Count == 0)
            return Array.Empty<SuiteListItemDto>();

        // One batched count per suite — no per-row round-trips.
        var suiteIds = page.Select(s => s.Id).ToList();
        var counts = await _db.SuiteTestCases
            .Where(st => suiteIds.Contains(st.SuiteId))
            .GroupBy(st => st.SuiteId)
            .Select(g => new { SuiteId = g.Key, Count = g.Count() })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.SuiteId, x => x.Count, ct);

        return page.Select(s => new SuiteListItemDto(
                s.Id,
                s.ProjectId,
                s.Name,
                s.Description,
                s.Status.ToString(),
                counts.GetValueOrDefault(s.Id),
                s.UpdatedAt))
            .ToList();
    }

    public async Task<TestSuite?> GetByIdRawAsync(Guid suiteId, CancellationToken ct)
        => await _db.TestSuites.FirstOrDefaultAsync(s => s.Id == suiteId, ct);

    public async Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeSuiteId, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        var query = _db.TestSuites.Where(s => s.ProjectId == projectId && s.Name.ToLower() == normalized);
        if (excludeSuiteId.HasValue)
            query = query.Where(s => s.Id != excludeSuiteId.Value);
        return await query.AnyAsync(ct);
    }

    public async Task AddAsync(TestSuite suite, CancellationToken ct)
        => await _db.TestSuites.AddAsync(suite, ct);

    public async Task SaveChangesAsync(CancellationToken ct)
        => await _db.SaveChangesAsync(ct);

    public async Task<SuiteDetailDto?> GetByIdWithMembersAsync(Guid suiteId, CancellationToken ct)
    {
        var suite = await _db.TestSuites
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == suiteId, ct);

        if (suite is null)
            return null;

        var members = await LoadMembersAsync(suiteId, ct);

        return new SuiteDetailDto(
            suite.Id,
            suite.ProjectId,
            suite.Name,
            suite.Description,
            suite.Status.ToString(),
            suite.CreatedAt,
            suite.UpdatedAt,
            members);
    }

    public Task<IReadOnlyList<SuiteMemberDto>> GetMembersAsync(Guid suiteId, CancellationToken ct)
        => LoadMembersAsync(suiteId, ct);

    public async Task AddMemberAsync(Guid suiteId, Guid testCaseId, int executionOrder, CancellationToken ct)
    {
        await _db.SuiteTestCases.AddAsync(new SuiteTestCase
        {
            SuiteId = suiteId,
            TestCaseId = testCaseId,
            ExecutionOrder = executionOrder,
        }, ct);
    }

    public async Task<bool> RemoveMemberAsync(Guid suiteId, Guid testCaseId, CancellationToken ct)
    {
        var entity = await _db.SuiteTestCases
            .FirstOrDefaultAsync(st => st.SuiteId == suiteId && st.TestCaseId == testCaseId, ct);
        if (entity is null)
            return false;
        _db.SuiteTestCases.Remove(entity);
        return true;
    }

    public async Task UpdateMemberOrdersAsync(Guid suiteId, IReadOnlyDictionary<Guid, int> orders, CancellationToken ct)
    {
        var existing = await _db.SuiteTestCases
            .Where(st => st.SuiteId == suiteId)
            .ToListAsync(ct);

        foreach (var row in existing)
        {
            if (orders.TryGetValue(row.TestCaseId, out var order))
                row.ExecutionOrder = order;
        }
    }

    public async Task<int> GetExecutionCountAsync(Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, CancellationToken ct)
    {
        var query = _db.Executions.Where(e => e.SuiteId == suiteId);
        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);
        if (triggerType.HasValue)
            query = query.Where(e => e.TriggerType == triggerType.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IReadOnlyList<SuiteExecutionSummaryDto>> GetExecutionHistoryAsync(
        Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, int skip, int take, CancellationToken ct)
    {
        var query = _db.Executions.Where(e => e.SuiteId == suiteId);
        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);
        if (triggerType.HasValue)
            query = query.Where(e => e.TriggerType == triggerType.Value);

        var page = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(e => new { e.Id, e.Status, e.TriggerType, e.CreatedAt, e.StartedAt, e.CompletedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        if (page.Count == 0)
            return Array.Empty<SuiteExecutionSummaryDto>();

        // One batched aggregate per execution — no per-row round-trips.
        var executionIds = page.Select(e => e.Id).ToList();
        var aggregates = await _db.ExecutionTests
            .Where(t => executionIds.Contains(t.ExecutionId))
            .GroupBy(t => t.ExecutionId)
            .Select(g => new
            {
                ExecutionId = g.Key,
                Total = g.Count(),
                Passed = g.Count(t => t.Status == ExecutionTestStatus.Passed),
                Failed = g.Count(t => t.Status == ExecutionTestStatus.Failed),
            })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ExecutionId, ct);

        return page.Select(e =>
        {
            aggregates.TryGetValue(e.Id, out var agg);
            return new SuiteExecutionSummaryDto(
                e.Id,
                e.Status.ToString(),
                e.TriggerType.ToString(),
                e.CreatedAt,
                e.StartedAt,
                e.CompletedAt,
                agg?.Total ?? 0,
                agg?.Passed ?? 0,
                agg?.Failed ?? 0);
        }).ToList();
    }

    public async Task<SuiteReportDto?> GetReportDataAsync(Guid suiteId, DateTimeOffset? from, DateTimeOffset? to, TriggerType? trigger, CancellationToken ct)
    {
        var suiteName = await _db.TestSuites
            .Where(s => s.Id == suiteId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);
        if (suiteName is null)
            return null;

        var query = _db.Executions
            .Where(e => e.SuiteId == suiteId)
            .Where(e => e.Status != ExecutionStatus.Queued && e.Status != ExecutionStatus.Running);
        if (trigger.HasValue)
            query = query.Where(e => e.TriggerType == trigger.Value);
        if (from.HasValue)
            query = query.Where(e => e.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.CreatedAt <= to.Value);

        var executionIds = await query
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (executionIds.Count == 0)
            return null;

        // One batched aggregate over the report window.
        var stats = await _db.ExecutionTests
            .Where(t => executionIds.Contains(t.ExecutionId))
            .GroupBy(t => 1)
            .Select(g => new
            {
                Passed = g.Count(t => t.Status == ExecutionTestStatus.Passed),
                Failed = g.Count(t => t.Status == ExecutionTestStatus.Failed),
                Cancelled = g.Count(t => t.Status == ExecutionTestStatus.Cancelled),
                TimedOut = g.Count(t => t.Status == ExecutionTestStatus.TimedOut),
                Error = g.Count(t => t.Status == ExecutionTestStatus.Error),
                TotalDurationMs = g.Where(t => t.DurationMs.HasValue).Sum(t => (long)t.DurationMs!.Value),
                AverageDurationMs = g.Where(t => t.DurationMs.HasValue).Average(t => (double?)(long)t.DurationMs!.Value),
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        var latestExecutionAt = await _db.Executions
            .Where(e => e.SuiteId == suiteId)
            .MaxAsync(e => (DateTimeOffset?)e.CreatedAt, ct);

        var passed = stats?.Passed ?? 0;
        var failed = stats?.Failed ?? 0;
        var cancelled = stats?.Cancelled ?? 0;
        var timedOut = stats?.TimedOut ?? 0;
        var error = stats?.Error ?? 0;
        var totalTests = passed + failed + cancelled + timedOut + error;

        return new SuiteReportDto(
            SuiteId: suiteId,
            SuiteName: suiteName,
            TotalExecutions: executionIds.Count,
            PassedCount: passed,
            FailedCount: failed,
            CancelledCount: cancelled,
            TimedOutCount: timedOut,
            ErrorCount: error,
            PassRate: totalTests > 0 ? (double)passed / totalTests * 100 : null,
            TotalDurationMs: stats?.TotalDurationMs ?? 0,
            AverageDurationMs: stats?.AverageDurationMs is null ? 0 : (long)stats.AverageDurationMs.Value,
            LatestExecutionAt: latestExecutionAt,
            TriggerBreakdown: Array.Empty<TriggerBreakdownItem>(),
            Trend: Array.Empty<SuiteReportTrendPoint>());
    }

    public async Task<IReadOnlyList<TriggerBreakdownItem>> GetTriggerBreakdownAsync(Guid suiteId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        // One server-side grouped aggregate over the window.
        var query = _db.Executions
            .Where(e => e.SuiteId == suiteId)
            .Where(e => e.Status != ExecutionStatus.Queued && e.Status != ExecutionStatus.Running);
        if (from.HasValue)
            query = query.Where(e => e.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.CreatedAt <= to.Value);

        var perTrigger = await query
            .GroupBy(e => e.TriggerType)
            .Select(g => new
            {
                Trigger = g.Key,
                ExecutionIds = g.Select(e => e.Id).ToList(),
            })
            .AsNoTracking()
            .ToListAsync(ct);

        if (perTrigger.Count == 0)
            return Array.Empty<TriggerBreakdownItem>();

        var allIds = perTrigger.SelectMany(g => g.ExecutionIds).ToList();
        var counts = await _db.ExecutionTests
            .Where(t => allIds.Contains(t.ExecutionId))
            .GroupBy(t => t.ExecutionId)
            .Select(g => new
            {
                ExecutionId = g.Key,
                Passed = g.Count(t => t.Status == ExecutionTestStatus.Passed),
                Failed = g.Count(t => t.Status == ExecutionTestStatus.Failed),
            })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ExecutionId, ct);

        return perTrigger
            .OrderBy(g => g.Trigger.ToString())
            .Select(g =>
            {
                var passed = 0;
                var failed = 0;
                foreach (var id in g.ExecutionIds)
                {
                    if (counts.TryGetValue(id, out var c))
                    {
                        passed += c.Passed;
                        failed += c.Failed;
                    }
                }
                var total = passed + failed;
                return new TriggerBreakdownItem(
                    g.Trigger.ToString(),
                    g.ExecutionIds.Count,
                    passed,
                    failed,
                    total > 0 ? (double)passed / total * 100 : null);
            })
            .ToList();
    }

    public async Task<SuiteTrendData> GetTrendDataAsync(Guid suiteId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Two bounded server queries; day bucketing happens client-side so no
        // provider-specific date SQL and no per-day round-trips.
        var executions = await _db.Executions
            .Where(e => e.SuiteId == suiteId)
            .Where(e => e.Status != ExecutionStatus.Queued && e.Status != ExecutionStatus.Running)
            .Where(e => e.CreatedAt >= from && e.CreatedAt <= to)
            .Select(e => new SuiteTrendExecution(e.Id, e.TriggerType.ToString(), e.CreatedAt))
            .AsNoTracking()
            .ToListAsync(ct);

        if (executions.Count == 0)
            return new SuiteTrendData(Array.Empty<SuiteTrendExecution>(), new Dictionary<Guid, SuiteTrendTests>());

        var ids = executions.Select(e => e.ExecutionId).ToList();
        var tests = await _db.ExecutionTests
            .Where(t => ids.Contains(t.ExecutionId))
            .GroupBy(t => t.ExecutionId)
            .Select(g => new
            {
                ExecutionId = g.Key,
                Passed = g.Count(t => t.Status == ExecutionTestStatus.Passed),
                Failed = g.Count(t => t.Status == ExecutionTestStatus.Failed),
                Cancelled = g.Count(t => t.Status == ExecutionTestStatus.Cancelled),
                TimedOut = g.Count(t => t.Status == ExecutionTestStatus.TimedOut),
                Error = g.Count(t => t.Status == ExecutionTestStatus.Error),
                TotalDurationMs = g.Where(t => t.DurationMs.HasValue).Sum(t => (long)t.DurationMs!.Value),
            })
            .AsNoTracking()
            .ToDictionaryAsync(
                x => x.ExecutionId,
                x => new SuiteTrendTests(x.Passed, x.Failed, x.Cancelled, x.TimedOut, x.Error, x.TotalDurationMs),
                ct);

        return new SuiteTrendData(executions, tests);
    }

    public async Task<Execution?> GetExecutionByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return null;

        return await _db.Executions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.IdempotencyKey == idempotencyKey, ct);
    }

    // ---------- helpers ----------

    private static bool TryParseStatus(string? status, out ProjectStatus parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(status) &&
               Enum.TryParse<ProjectStatus>(status.Trim(), true, out parsed);
    }

    /// <summary>
    /// Loads suite members in deterministic order (ExecutionOrder, then
    /// TestCaseId) with three batched queries: membership rows, test cases,
    /// then latest versions for Jira provenance. No per-member round-trips.
    /// </summary>
    private async Task<IReadOnlyList<SuiteMemberDto>> LoadMembersAsync(Guid suiteId, CancellationToken ct)
    {
        var links = await _db.SuiteTestCases
            .Where(st => st.SuiteId == suiteId)
            .OrderBy(st => st.ExecutionOrder)
            .ThenBy(st => st.TestCaseId)
            .Select(st => new { st.TestCaseId, st.ExecutionOrder })
            .AsNoTracking()
            .ToListAsync(ct);

        if (links.Count == 0)
            return Array.Empty<SuiteMemberDto>();

        var testCaseIds = links.Select(l => l.TestCaseId).ToList();

        var cases = await _db.TestCases
            .Where(t => testCaseIds.Contains(t.Id))
            .Select(t => new { t.Id, t.TestKey, t.Title })
            .AsNoTracking()
            .ToDictionaryAsync(t => t.Id, ct);

        var versions = await _db.TestCaseVersions
            .Where(v => testCaseIds.Contains(v.TestCaseId))
            .Select(v => new { v.TestCaseId, v.VersionNumber, v.GenerationRequest })
            .AsNoTracking()
            .ToListAsync(ct);

        var jiraKeys = versions
            .GroupBy(v => v.TestCaseId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.MaxBy(v => v.VersionNumber);
                    return latest is null
                        ? null
                        : JiraProvenanceReader.TryRead(latest.GenerationRequest)?.JiraIssueKey;
                });

        var members = new List<SuiteMemberDto>(links.Count);
        foreach (var link in links)
        {
            // A membership row whose test case was deleted renders with its
            // key missing rather than dropping the row (execution rejects it
            // deterministically instead of silently skipping it).
            cases.TryGetValue(link.TestCaseId, out var testCase);
            jiraKeys.TryGetValue(link.TestCaseId, out var jiraKey);
            members.Add(new SuiteMemberDto(
                suiteId,
                link.TestCaseId,
                testCase?.TestKey ?? string.Empty,
                testCase?.Title ?? string.Empty,
                link.ExecutionOrder,
                jiraKey,
                null));
        }

        return members;
    }
}
