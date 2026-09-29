using AutoTestAi.Application.Common;
using AutoTestAi.Application.Reports;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Reports;

/// <summary>
/// EF Core read-only aggregates for Slice 8. All grouping happens in the
/// database (translatable date-part grouping); no table is ever mutated here.
/// </summary>
public sealed class EfReportQueryStore : IReportQueryStore
{
    private static readonly IReadOnlySet<string> TerminalExecutionStatuses = new HashSet<string>(
        new[]
        {
            nameof(ExecutionStatus.Passed),
            nameof(ExecutionStatus.Failed),
            nameof(ExecutionStatus.Cancelled),
            nameof(ExecutionStatus.TimedOut),
            nameof(ExecutionStatus.Error),
        }, StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> TerminalTestStatuses = new HashSet<string>(
        new[]
        {
            nameof(ExecutionTestStatus.Passed),
            nameof(ExecutionTestStatus.Failed),
            nameof(ExecutionTestStatus.Error),
            nameof(ExecutionTestStatus.TimedOut),
            nameof(ExecutionTestStatus.Cancelled),
        }, StringComparer.Ordinal);

    /// <summary>Audit actions surfaced in the activity feed (safe, known writers only).</summary>
    private static readonly IReadOnlySet<string> ActivityActions = new HashSet<string>(
        new[]
        {
            "testcase.created",
            "test-generation.completed",
            "failure-analysis.completed",
            "defect.created",
            "defect.created_from_analysis",
            "defect.status_changed",
            "ticket.created",
            "ticket.creation_failed",
        }, StringComparer.Ordinal);

    private readonly AutoTestAiDbContext _db;

    public EfReportQueryStore(AutoTestAiDbContext db) => _db = db;

    public async Task<TestCaseKpis> GetTestCaseKpisAsync(Guid projectId, CancellationToken ct)
    {
        var total = await _db.TestCases
            .Where(c => c.ProjectId == projectId)
            .CountAsync(ct);
        // Approved = latest version per test case carries ReviewStatus Approved.
        var approved = await _db.TestCases
            .Where(c => c.ProjectId == projectId)
            .Where(c => _db.TestCaseVersions
                .Where(v => v.TestCaseId == c.Id)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => v.ReviewStatus)
                .FirstOrDefault() == ReviewStatus.Approved)
            .CountAsync(ct);
        return new TestCaseKpis(total, approved);
    }

    public async Task<ExecutionKpis> GetExecutionKpisAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var groups = await _db.Executions
            .Where(e => e.ProjectId == projectId && e.CreatedAt >= range.From && e.CreatedAt <= range.To)
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .ToListAsync(ct);
        var byStatus = groups.ToDictionary(g => g.Status, g => g.Count, StringComparer.Ordinal);
        static int Get(Dictionary<string, int> d, string key) => d.TryGetValue(key, out var v) ? v : 0;
        var passed = Get(byStatus, nameof(ExecutionStatus.Passed));
        var failed = Get(byStatus, nameof(ExecutionStatus.Failed));
        var cancelled = Get(byStatus, nameof(ExecutionStatus.Cancelled));
        var timedOut = Get(byStatus, nameof(ExecutionStatus.TimedOut));
        var error = Get(byStatus, nameof(ExecutionStatus.Error));
        var queuedOrRunning = Get(byStatus, nameof(ExecutionStatus.Queued)) + Get(byStatus, nameof(ExecutionStatus.Running));
        var terminal = passed + failed + cancelled + timedOut + error;
        double? passRate = terminal == 0 ? null : (double)passed / terminal;
        return new ExecutionKpis(
            passed + failed + cancelled + timedOut + error + queuedOrRunning,
            passed, failed, cancelled, timedOut, error, queuedOrRunning, passRate);
    }

    public async Task<DefectKpis> GetDefectKpisAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var groups = await _db.Defects
            .Where(d => d.ProjectId == projectId && d.CreatedAt >= range.From && d.CreatedAt <= range.To)
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .ToListAsync(ct);
        var byStatus = groups.ToDictionary(g => g.Status, g => g.Count, StringComparer.Ordinal);
        static int Get(Dictionary<string, int> d, string key) => d.TryGetValue(key, out var v) ? v : 0;
        var high = await _db.Defects
            .Where(d => d.ProjectId == projectId && d.CreatedAt >= range.From && d.CreatedAt <= range.To)
            .Where(d => d.Severity == Severity.Critical || d.Severity == Severity.High)
            .CountAsync(ct);
        var open = Get(byStatus, nameof(DefectStatus.Open));
        var inProgress = Get(byStatus, nameof(DefectStatus.InProgress));
        var resolved = Get(byStatus, nameof(DefectStatus.Resolved));
        var closed = Get(byStatus, nameof(DefectStatus.Closed));
        var rejected = Get(byStatus, nameof(DefectStatus.Rejected));
        return new DefectKpis(open + inProgress + resolved + closed + rejected, open, inProgress, resolved, closed, rejected, high);
    }

    public async Task<TicketKpis> GetTicketKpisAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var groups = await _db.Tickets
            .Where(t => t.ProjectId == projectId && t.CreatedAt >= range.From && t.CreatedAt <= range.To)
            .GroupBy(t => t.SyncStatus)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .ToListAsync(ct);
        var byStatus = groups.ToDictionary(g => g.Status, g => g.Count, StringComparer.Ordinal);
        static int Get(Dictionary<string, int> d, string key) => d.TryGetValue(key, out var v) ? v : 0;
        var synced = Get(byStatus, nameof(TicketSyncStatus.Synced));
        var failed = Get(byStatus, nameof(TicketSyncStatus.Failed));
        var pending = Get(byStatus, nameof(TicketSyncStatus.Pending));
        return new TicketKpis(synced + failed + pending, synced, failed, pending);
    }

    public async Task<IReadOnlyList<StatusDayCount>> GetExecutionStatusByDayAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
        => await _db.Executions
            .Where(e => e.ProjectId == projectId && e.CreatedAt >= range.From && e.CreatedAt <= range.To)
            .GroupBy(e => new
            {
                e.CreatedAt.Year,
                e.CreatedAt.Month,
                e.CreatedAt.Day,
                Status = e.Status.ToString(),
            })
            .Select(g => new StatusDayCount(g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Status, g.Count()))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<NamedCount>> GetFailureClassificationCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        // Authoritative deterministic classification from execution_tests
        // (Slice 5); AI advisory output is never consulted here.
        var inRange = _db.Executions
            .Where(e => e.ProjectId == projectId && e.CreatedAt >= range.From && e.CreatedAt <= range.To)
            .Select(e => e.Id);
        var terminal = TerminalTestStatuses.Select(s => s).ToList();
        // Ordering applied in memory: some providers cannot order over a
        // grouping projection server-side.
        var rows = await _db.ExecutionTests
            .Where(t => inRange.Contains(t.ExecutionId) && terminal.Contains(t.Status.ToString()))
            .GroupBy(t => t.FailureClassification.ToString())
            .Select(g => new NamedCount(g.Key, g.Count()))
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.Count).ToList();
    }

    public async Task<IReadOnlyList<NamedCount>> GetDefectSeverityCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var rows = await _db.Defects
            .Where(d => d.ProjectId == projectId && d.CreatedAt >= range.From && d.CreatedAt <= range.To)
            .GroupBy(d => d.Severity.ToString())
            .Select(g => new NamedCount(g.Key, g.Count()))
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.Count).ToList();
    }

    public async Task<IReadOnlyList<NamedCount>> GetDefectClassificationCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var rows = await _db.Defects
            .Where(d => d.ProjectId == projectId && d.CreatedAt >= range.From && d.CreatedAt <= range.To)
            .GroupBy(d => d.RootCauseType == null ? "Unknown" : d.RootCauseType.ToString())
            .Select(g => new NamedCount(g.Key, g.Count()))
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.Count).ToList();
    }

    public async Task<IReadOnlyList<NamedCount>> GetTicketProviderCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var rows = await _db.Tickets
            .Where(t => t.ProjectId == projectId && t.CreatedAt >= range.From && t.CreatedAt <= range.To)
            .GroupBy(t => t.Provider)
            .Select(g => new NamedCount(g.Key, g.Count()))
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.Count).ToList();
    }

    public async Task<IReadOnlyList<RecentExecutionItem>> GetRecentExecutionsAsync(
        Guid projectId, int take, CancellationToken ct)
    {
        var rows = await _db.Executions
            .Where(e => e.ProjectId == projectId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(take)
            .Select(e => new
            {
                Execution = e,
                Test = _db.ExecutionTests
                    .Where(t => t.ExecutionId == e.Id)
                    .OrderBy(t => t.CreatedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var items = new List<RecentExecutionItem>(rows.Count);
        foreach (var row in rows)
        {
            string? key = null, title = null;
            int? version = null;
            if (row.Test is not null)
            {
                var tc = await _db.TestCases
                    .Where(c => c.Id == row.Test.TestCaseId)
                    .Select(c => new { c.TestKey, c.Title })
                    .FirstOrDefaultAsync(ct);
                key = tc?.TestKey;
                title = tc?.Title;
                if (row.Test.TestCaseVersionId is not null)
                    version = await _db.TestCaseVersions
                        .Where(v => v.Id == row.Test.TestCaseVersionId.Value)
                        .Select(v => (int?)v.VersionNumber)
                        .FirstOrDefaultAsync(ct);
            }
            items.Add(new RecentExecutionItem(
                row.Execution.Id,
                row.Execution.Status.ToString(),
                key, title, version,
                row.Test?.FailureClassification.ToString(),
                row.Test?.DurationMs,
                row.Execution.StartedAt,
                row.Execution.CompletedAt,
                row.Execution.CreatedAt));
        }
        return items;
    }

    public async Task<IReadOnlyList<RecentDefectItem>> GetRecentDefectsAsync(
        Guid projectId, int take, CancellationToken ct)
    {
        var rows = await _db.Defects
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(take)
            .Select(d => new
            {
                Defect = d,
                JiraKey = _db.Tickets
                    .Where(t => t.DefectId == d.Id && t.SyncStatus == TicketSyncStatus.Synced)
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => t.ExternalKey)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new RecentDefectItem(
            r.Defect.Id, r.Defect.Title,
            r.Defect.Severity.ToString(), r.Defect.Status.ToString(),
            r.Defect.RootCauseType == null ? null : r.Defect.RootCauseType.ToString(),
            r.JiraKey, r.Defect.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<RecentTicketItem>> GetRecentTicketsAsync(
        Guid projectId, int take, CancellationToken ct)
        => await _db.Tickets
            .Where(t => t.ProjectId == projectId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .Select(t => new RecentTicketItem(
                t.Id, t.Provider, t.ExternalKey, t.SyncStatus.ToString(), t.DefectId, t.CreatedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ActivityItem>> GetRecentActivityAsync(
        Guid projectId, ReportDateRange range, int take, CancellationToken ct)
        => await _db.AuditEvents
            .Where(a => a.ProjectId == projectId
                && a.CreatedAt >= range.From && a.CreatedAt <= range.To
                && ActivityActions.Contains(a.Action))
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(a => new ActivityItem(a.Action, a.EntityType, a.EntityId, a.CreatedAt))
            .ToListAsync(ct);

    public async Task<PagedResult<ExecutionReportItem>> QueryExecutionsAsync(
        Guid projectId, ReportDateRange range, ExecutionReportFilters filters,
        int skip, int take, CancellationToken ct)
    {
        var query = _db.Executions
            .Where(e => e.ProjectId == projectId && e.CreatedAt >= range.From && e.CreatedAt <= range.To);
        if (!string.IsNullOrWhiteSpace(filters.Status))
            query = query.Where(e => e.Status.ToString() == filters.Status);
        if (filters.TestCaseId is not null)
        {
            var testCaseId = filters.TestCaseId.Value;
            query = query.Where(e => _db.ExecutionTests
                .Any(t => t.ExecutionId == e.Id && t.TestCaseId == testCaseId));
        }
        if (!string.IsNullOrWhiteSpace(filters.Classification))
            query = query.Where(e => _db.ExecutionTests
                .Any(t => t.ExecutionId == e.Id && t.FailureClassification.ToString() == filters.Classification));

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip).Take(take)
            .Select(e => new
            {
                Execution = e,
                Test = _db.ExecutionTests
                    .Where(t => t.ExecutionId == e.Id)
                    .OrderBy(t => t.CreatedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var items = new List<ExecutionReportItem>(page.Count);
        foreach (var row in page)
        {
            string? key = null, title = null;
            Guid? testCaseId = null;
            int? version = null;
            if (row.Test is not null)
            {
                testCaseId = row.Test.TestCaseId;
                var tc = await _db.TestCases
                    .Where(c => c.Id == row.Test.TestCaseId)
                    .Select(c => new { c.TestKey, c.Title })
                    .FirstOrDefaultAsync(ct);
                key = tc?.TestKey;
                title = tc?.Title;
                if (row.Test.TestCaseVersionId is not null)
                    version = await _db.TestCaseVersions
                        .Where(v => v.Id == row.Test.TestCaseVersionId.Value)
                        .Select(v => (int?)v.VersionNumber)
                        .FirstOrDefaultAsync(ct);
            }
            items.Add(new ExecutionReportItem(
                row.Execution.Id, row.Execution.Status.ToString(),
                testCaseId, key, title, version,
                row.Test?.FailureClassification.ToString(),
                row.Test?.DurationMs,
                row.Execution.StartedAt, row.Execution.CompletedAt,
                row.Execution.CreatedAt));
        }
        return new PagedResult<ExecutionReportItem>(items, total, 0, 0);
    }

    public async Task<PagedResult<DefectReportItem>> QueryDefectsAsync(
        Guid projectId, ReportDateRange range, DefectReportFilters filters,
        int skip, int take, CancellationToken ct)
    {
        var query = _db.Defects
            .Where(d => d.ProjectId == projectId && d.CreatedAt >= range.From && d.CreatedAt <= range.To);
        if (!string.IsNullOrWhiteSpace(filters.Status))
            query = query.Where(d => d.Status.ToString() == filters.Status);
        if (!string.IsNullOrWhiteSpace(filters.Severity))
            query = query.Where(d => d.Severity.ToString() == filters.Severity);
        if (!string.IsNullOrWhiteSpace(filters.Classification))
            query = filters.Classification == "Unknown"
                ? query.Where(d => d.RootCauseType == null)
                : query.Where(d => d.RootCauseType != null && d.RootCauseType.ToString() == filters.Classification);
        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var term = filters.Search.Trim().ToLower();
            query = query.Where(d =>
                d.Title.ToLower().Contains(term) ||
                (d.Description != null && d.Description.ToLower().Contains(term)));
        }
        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(skip).Take(take)
            .Select(d => new
            {
                Defect = d,
                JiraKey = _db.Tickets
                    .Where(t => t.DefectId == d.Id && t.SyncStatus == TicketSyncStatus.Synced)
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => t.ExternalKey)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var items = page.Select(r => new DefectReportItem(
            r.Defect.Id, r.Defect.Title,
            r.Defect.Severity.ToString(), r.Defect.Status.ToString(),
            r.Defect.RootCauseType == null ? null : r.Defect.RootCauseType.ToString(),
            r.JiraKey, r.Defect.CreatedAt)).ToList();
        return new PagedResult<DefectReportItem>(items, total, 0, 0);
    }

    public async Task<PagedResult<TicketReportItem>> QueryTicketsAsync(
        Guid projectId, ReportDateRange range, TicketReportFilters filters,
        int skip, int take, CancellationToken ct)
    {
        var query = _db.Tickets
            .Where(t => t.ProjectId == projectId && t.CreatedAt >= range.From && t.CreatedAt <= range.To);
        if (!string.IsNullOrWhiteSpace(filters.Provider))
            query = query.Where(t => t.Provider == filters.Provider);
        if (!string.IsNullOrWhiteSpace(filters.SyncStatus))
            query = query.Where(t => t.SyncStatus.ToString() == filters.SyncStatus);
        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip(skip).Take(take)
            .Select(t => new
            {
                Ticket = t,
                DefectTitle = _db.Defects
                    .Where(d => d.Id == t.DefectId)
                    .Select(d => d.Title)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var items = page.Select(r => new TicketReportItem(
            r.Ticket.Id, r.Ticket.Provider, r.Ticket.ExternalKey, r.Ticket.ExternalUrl,
            r.Ticket.SyncStatus.ToString(), r.Ticket.DefectId, r.DefectTitle,
            r.Ticket.CreatedAt)).ToList();
        return new PagedResult<TicketReportItem>(items, total, 0, 0);
    }
}

/// <summary>Fail-closed store used when no database is configured.</summary>
public sealed class UnavailableReportQueryStore : IReportQueryStore
{
    private static Task<T> Fail<T>() => throw new InvalidOperationException("The database is not configured.");

    public Task<TestCaseKpis> GetTestCaseKpisAsync(Guid p, CancellationToken ct) => Fail<TestCaseKpis>();
    public Task<ExecutionKpis> GetExecutionKpisAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<ExecutionKpis>();
    public Task<DefectKpis> GetDefectKpisAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<DefectKpis>();
    public Task<TicketKpis> GetTicketKpisAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<TicketKpis>();
    public Task<IReadOnlyList<StatusDayCount>> GetExecutionStatusByDayAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<StatusDayCount>>();
    public Task<IReadOnlyList<NamedCount>> GetFailureClassificationCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<NamedCount>>();
    public Task<IReadOnlyList<NamedCount>> GetDefectSeverityCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<NamedCount>>();
    public Task<IReadOnlyList<NamedCount>> GetDefectClassificationCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<NamedCount>>();
    public Task<IReadOnlyList<NamedCount>> GetTicketProviderCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<NamedCount>>();
    public Task<IReadOnlyList<RecentExecutionItem>> GetRecentExecutionsAsync(Guid p, int t, CancellationToken ct) => Fail<IReadOnlyList<RecentExecutionItem>>();
    public Task<IReadOnlyList<RecentDefectItem>> GetRecentDefectsAsync(Guid p, int t, CancellationToken ct) => Fail<IReadOnlyList<RecentDefectItem>>();
    public Task<IReadOnlyList<RecentTicketItem>> GetRecentTicketsAsync(Guid p, int t, CancellationToken ct) => Fail<IReadOnlyList<RecentTicketItem>>();
    public Task<IReadOnlyList<ActivityItem>> GetRecentActivityAsync(Guid p, ReportDateRange r, int t, CancellationToken ct) => Fail<IReadOnlyList<ActivityItem>>();
    public Task<PagedResult<ExecutionReportItem>> QueryExecutionsAsync(Guid p, ReportDateRange r, ExecutionReportFilters f, int s, int t, CancellationToken ct) => Fail<PagedResult<ExecutionReportItem>>();
    public Task<PagedResult<DefectReportItem>> QueryDefectsAsync(Guid p, ReportDateRange r, DefectReportFilters f, int s, int t, CancellationToken ct) => Fail<PagedResult<DefectReportItem>>();
    public Task<PagedResult<TicketReportItem>> QueryTicketsAsync(Guid p, ReportDateRange r, TicketReportFilters f, int s, int t, CancellationToken ct) => Fail<PagedResult<TicketReportItem>>();
}
