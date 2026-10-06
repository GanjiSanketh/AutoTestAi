using AutoTestAi.Application.Common;
using AutoTestAi.Application.Reports;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
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

    /// <summary>Terminal execution outcomes (Slice 12 verdict analysis).</summary>
    private static readonly IReadOnlyList<string> TerminalVerdictStatuses = new[]
    {
        nameof(ExecutionStatus.Passed),
        nameof(ExecutionStatus.Failed),
        nameof(ExecutionStatus.Cancelled),
        nameof(ExecutionStatus.TimedOut),
        nameof(ExecutionStatus.Error),
    };

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

    /// <summary>
    /// Audit Explorer (Phase 4 Slice 2). Project predicate is always applied
    /// server-side. Only safe columns are projected — MetadataJson,
    /// IpAddress, and UserAgent never leave the database.
    /// </summary>
    public async Task<PagedResult<AuditEventItem>> QueryAuditEventsAsync(
        Guid projectId, ReportDateRange range, AuditEventFilters filters,
        int skip, int take, CancellationToken ct)
    {
        var query = _db.AuditEvents
            .Where(a => a.ProjectId == projectId && a.CreatedAt >= range.From && a.CreatedAt <= range.To);
        if (!string.IsNullOrWhiteSpace(filters.Action))
            query = query.Where(a => a.Action == filters.Action);
        if (filters.ActorUserId is not null)
        {
            var actor = filters.ActorUserId.Value;
            query = query.Where(a => a.ActorUserId == actor);
        }
        if (!string.IsNullOrWhiteSpace(filters.EntityType))
            query = query.Where(a => a.EntityType == filters.EntityType);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip(skip).Take(take)
            .Select(a => new AuditEventItem(
                a.Id, a.CreatedAt, a.Action, a.EntityType, a.EntityId, a.ActorUserId))
            .ToListAsync(ct);
        return new PagedResult<AuditEventItem>(items, total, 0, 0);
    }

    // ---------- executive analytics primitives (Slice 12) ----------

    /// <summary>
    /// One logical execution = one Execution row (the engine's single infra
    /// retry reuses the same execution, so retries never double-count). All
    /// verdict queries scope project + CreatedAt range + terminal status.
    /// </summary>
    private IQueryable<Execution> WindowExecutions(Guid projectId, ReportDateRange range)
        => _db.Executions.Where(e =>
            e.ProjectId == projectId && e.CreatedAt >= range.From && e.CreatedAt <= range.To);

    public async Task<IReadOnlyList<TestOutcomeRow>> GetTestOutcomeRowsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var terminal = TerminalVerdictStatuses;
        var groups = await WindowExecutions(projectId, range)
            .Where(e => terminal.Contains(e.Status.ToString()))
            .Join(_db.ExecutionTests,
                e => e.Id, t => t.ExecutionId,
                (e, t) => new { t.TestCaseId, Status = e.Status.ToString(), e.CreatedAt })
            .GroupBy(x => new { x.TestCaseId, x.Status })
            .Select(g => new
            {
                g.Key.TestCaseId,
                g.Key.Status,
                Count = g.Count(),
                LastRunAt = g.Max(x => x.CreatedAt),
            })
            .ToListAsync(ct);
        return groups
            .GroupBy(g => g.TestCaseId)
            .Select(g => new TestOutcomeRow(
                g.Key,
                g.Where(x => x.Status == nameof(ExecutionStatus.Passed)).Sum(x => x.Count),
                g.Where(x => x.Status == nameof(ExecutionStatus.Failed)).Sum(x => x.Count),
                g.Where(x => x.Status != nameof(ExecutionStatus.Passed)
                    && x.Status != nameof(ExecutionStatus.Failed)).Sum(x => x.Count),
                g.Max(x => x.LastRunAt)))
            .ToList();
    }

    public async Task<IReadOnlyList<TestDayOutcomeRow>> GetTestDayOutcomeRowsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var terminal = TerminalVerdictStatuses;
        return await WindowExecutions(projectId, range)
            .Where(e => terminal.Contains(e.Status.ToString()))
            .Join(_db.ExecutionTests,
                e => e.Id, t => t.ExecutionId,
                (e, t) => new { t.TestCaseId, e.CreatedAt, Status = e.Status.ToString() })
            .GroupBy(x => new
            {
                x.TestCaseId,
                x.CreatedAt.Year,
                x.CreatedAt.Month,
                x.CreatedAt.Day,
                x.Status,
            })
            .Select(g => new TestDayOutcomeRow(
                g.Key.TestCaseId, g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Status, g.Count()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TestLastRunRow>> GetTestLastRunsAsync(
        Guid projectId, ReportDateRange range, IReadOnlyList<Guid> testCaseIds,
        CancellationToken ct)
    {
        // Latest execution per test for the DISPLAY PAGE ONLY (page-bounded
        // follow-ups, the Slice 8 precedent): grouped max dates come from
        // GetTestOutcomeRowsAsync-adjacent shaping; statuses resolve here.
        // Same-millisecond ties break by execution id for determinism.
        var terminal = TerminalVerdictStatuses;
        // Bounded by the caller (report page ≤100, CSV export ≤5000).
        var ids = testCaseIds.Distinct().Take(5000).ToList();
        var items = new List<TestLastRunRow>(ids.Count);
        foreach (var id in ids)
        {
            var latest = await WindowExecutions(projectId, range)
                .Where(e => terminal.Contains(e.Status.ToString())
                    && _db.ExecutionTests.Any(t =>
                        t.ExecutionId == e.Id && t.TestCaseId == id))
                .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
                .Select(e => new { e.Id, Status = e.Status.ToString(), e.CreatedAt })
                .FirstOrDefaultAsync(ct);
            if (latest is not null)
                items.Add(new TestLastRunRow(id, latest.Id, latest.Status, latest.CreatedAt));
        }
        return items;
    }

    public async Task<IReadOnlyList<TestVerdictRow>> GetTestRecentVerdictsAsync(
        Guid projectId, ReportDateRange range, IReadOnlyList<Guid> testCaseIds,
        int perTestTake, CancellationToken ct)
    {
        // Single newest-first pass over the window (Slice 12 verdict
        // analysis): pass/fail execution statuses only, matching the
        // flakiness aggregates. Bounded server-side; the caller groups per
        // test and applies perTestTake. Same-millisecond ties break by
        // execution id for determinism.
        var take = Math.Clamp(perTestTake, 1, 100);
        var ids = testCaseIds.Distinct().Take(5000).ToList();
        var rows = await WindowExecutions(projectId, range)
            .Where(e => (e.Status == ExecutionStatus.Passed || e.Status == ExecutionStatus.Failed)
                && _db.ExecutionTests.Any(t =>
                    t.ExecutionId == e.Id && ids.Contains(t.TestCaseId)))
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
            .Join(_db.ExecutionTests,
                e => e.Id, t => t.ExecutionId,
                (e, t) => new { t.TestCaseId, Passed = e.Status == ExecutionStatus.Passed, e.CreatedAt })
            .Where(x => ids.Contains(x.TestCaseId))
            .Take(20000)
            .ToListAsync(ct);
        return rows
            .GroupBy(x => x.TestCaseId)
            .SelectMany(g => g.Take(take).Select(x =>
                new TestVerdictRow(g.Key, x.Passed, x.CreatedAt)))
            .ToList();
    }

    public async Task<IReadOnlyList<TestHealingRow>> GetTestHealingRowsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var groups = await _db.SelfHealingAttempts
            .Where(a => a.ProjectId == projectId
                && a.CreatedAt >= range.From && a.CreatedAt <= range.To)
            .GroupBy(a => a.TestCaseId)
            .Select(g => new
            {
                g.Key,
                Attempts = g.Count(),
                Applied = g.Sum(a => a.WasApplied ? 1 : 0),
            })
            .ToListAsync(ct);
        return groups.Select(g => new TestHealingRow(g.Key, g.Attempts, g.Applied)).ToList();
    }

    public async Task<CoverageCounts> GetCoverageCountsAsync(Guid projectId, CancellationToken ct)
    {
        var eligible = _db.TestCases
            .Where(c => c.ProjectId == projectId && c.Status != TestCaseStatus.Archived);
        var total = await eligible.CountAsync(ct);
        // Automated = latest version per case is Approved (executable through
        // the Slice 5 approval gate); same subquery shape as Slice 8 KPIs.
        var automated = await eligible
            .Where(c => _db.TestCaseVersions
                .Where(v => v.TestCaseId == c.Id)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => v.ReviewStatus)
                .FirstOrDefault() == ReviewStatus.Approved)
            .CountAsync(ct);
        return new CoverageCounts(total, automated);
    }

    public async Task<int> GetOpenCriticalHighDefectCountAsync(Guid projectId, CancellationToken ct)
        => await _db.Defects
            .Where(d => d.ProjectId == projectId
                && (d.Status == DefectStatus.Open || d.Status == DefectStatus.InProgress)
                && (d.Severity == Severity.Critical || d.Severity == Severity.High))
            .CountAsync(ct);

    public async Task<int> GetDefectsCreatedCountAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
        => await _db.Defects
            .Where(d => d.ProjectId == projectId
                && d.CreatedAt >= range.From && d.CreatedAt <= range.To)
            .CountAsync(ct);

    public async Task<IReadOnlyList<NamedCount>> GetOpenDefectAgingAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        // Fixed, documented buckets (no invented SLA targets): under 7 days,
        // 7–30 days, over 30 days. Ages measure against the WINDOW END (UTC),
        // so historical windows report historically instead of drifting with
        // the wall clock — deterministic and testable.
        var now = range.To;
        var week = now.AddDays(-7);
        var month = now.AddDays(-30);
        var baseQuery = _db.Defects.Where(d => d.ProjectId == projectId
            && (d.Status == DefectStatus.Open || d.Status == DefectStatus.InProgress));
        var fresh = await baseQuery.Where(d => d.CreatedAt >= week).CountAsync(ct);
        var aging = await baseQuery.Where(d => d.CreatedAt < week && d.CreatedAt >= month).CountAsync(ct);
        var stale = await baseQuery.Where(d => d.CreatedAt < month).CountAsync(ct);
        return new List<NamedCount>
        {
            new("0-7 days", fresh),
            new("8-30 days", aging),
            new("30+ days", stale),
        };
    }

    public async Task<DurationStats> GetDurationStatsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var terminal = TerminalVerdictStatuses;
        var durations = WindowExecutions(projectId, range)
            .Where(e => terminal.Contains(e.Status.ToString()))
            .Join(_db.ExecutionTests,
                e => e.Id, t => t.ExecutionId,
                (e, t) => t.DurationMs)
            .Where(d => d != null && d >= 0);
        var count = await durations.CountAsync(ct);
        if (count == 0) return new DurationStats(0, null, null, null, null);
        var average = await durations.AverageAsync(d => (double)d!, ct);
        var min = await durations.MinAsync(ct);
        var max = await durations.MaxAsync(ct);
        var total = await durations.SumAsync(d => (long)d!, ct);
        return new DurationStats(count, average, min, max, total);
    }

    public async Task<IReadOnlyList<long>> GetDurationsCappedAsync(
        Guid projectId, ReportDateRange range, int cap, CancellationToken ct)
    {
        var terminal = TerminalVerdictStatuses;
        return await WindowExecutions(projectId, range)
            .Where(e => terminal.Contains(e.Status.ToString()))
            .Join(_db.ExecutionTests,
                e => e.Id, t => t.ExecutionId,
                (e, t) => t.DurationMs)
            .Where(d => d != null && d >= 0)
            .Select(d => d!.Value)
            .OrderBy(d => d)
            .Take(Math.Clamp(cap, 1, 50000))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DurationDayRow>> GetDurationByDayAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var terminal = TerminalVerdictStatuses;
        return await WindowExecutions(projectId, range)
            .Where(e => terminal.Contains(e.Status.ToString()))
            .Join(_db.ExecutionTests,
                e => e.Id, t => t.ExecutionId,
                (e, t) => new { e.CreatedAt, t.DurationMs })
            .Where(x => x.DurationMs != null && x.DurationMs >= 0)
            .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month, x.CreatedAt.Day })
            .Select(g => new DurationDayRow(
                g.Key.Year, g.Key.Month, g.Key.Day,
                g.Count(), g.Average(x => (double)x.DurationMs!)))
            .ToListAsync(ct);
    }

    public async Task<HealingStats> GetHealingStatsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var window = _db.SelfHealingAttempts.Where(a =>
            a.ProjectId == projectId && a.CreatedAt >= range.From && a.CreatedAt <= range.To);
        var attempts = await window.CountAsync(ct);
        var applied = await window.Where(a => a.WasApplied).CountAsync(ct);
        var aiAssisted = await window.Where(a => a.IsAiAssisted).CountAsync(ct);
        var deterministic = attempts - aiAssisted;
        var tests = await window.Select(a => a.TestCaseId).Distinct().CountAsync(ct);
        var executions = await window.Select(a => a.ExecutionId).Distinct().CountAsync(ct);
        return new HealingStats(attempts, applied, deterministic, aiAssisted, tests, executions);
    }

    public async Task<IReadOnlyList<TestCaseMetaRow>> GetTestCaseMetaAsync(
        Guid projectId, IReadOnlyList<Guid> testCaseIds, CancellationToken ct)
    {
        // No arbitrary cap: the id set is exactly the tests with executions
        // in the window (bounded by the date range), and capping here would
        // drop an undefined subset. Simple keyed lookup, one round trip.
        var ids = testCaseIds.Distinct().ToList();
        return await _db.TestCases
            .Where(c => c.ProjectId == projectId && ids.Contains(c.Id))
            .Select(c => new TestCaseMetaRow(
                c.Id, c.TestKey, c.Title, c.Module, c.Priority.ToString(),
                c.Framework, c.Platform))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HealingDayRow>> GetHealingByDayAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        // Two provider-safe groupings merged in memory (avoids conditional
        // aggregates that some providers cannot translate).
        var window = _db.SelfHealingAttempts.Where(a =>
            a.ProjectId == projectId && a.CreatedAt >= range.From && a.CreatedAt <= range.To);
        var totals = await window
            .GroupBy(a => new { a.CreatedAt.Year, a.CreatedAt.Month, a.CreatedAt.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
            .ToListAsync(ct);
        var applied = await window
            .Where(a => a.WasApplied)
            .GroupBy(a => new { a.CreatedAt.Year, a.CreatedAt.Month, a.CreatedAt.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
            .ToListAsync(ct);
        var appliedByDay = applied.ToDictionary(
            a => (a.Year, a.Month, a.Day), a => a.Count);
        return totals
            .Select(t => new HealingDayRow(t.Year, t.Month, t.Day, t.Count,
                appliedByDay.TryGetValue((t.Year, t.Month, t.Day), out var v) ? v : 0))
            .ToList();
    }

    /// <summary>
    /// Jira freshness staleness data for current/latest Jira-origin test case versions (Phase 4 Slice 8).
    /// Returns the latest completed freshness check per current Jira-origin version.
    /// Only jira-change-check.completed events are considered.
    /// </summary>
    public async Task<IReadOnlyList<JiraStalenessRow>> GetJiraStalenessAsync(Guid projectId, CancellationToken ct)
    {
        // 1. Get all test cases in the project with their latest version
        var latestVersions = await _db.TestCases
            .Where(tc => tc.ProjectId == projectId)
            .Select(tc => new
            {
                TestCase = new { tc.Id, tc.TestKey, tc.Title },
                LatestVersion = _db.TestCaseVersions
                    .Where(v => v.TestCaseId == tc.Id)
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => new { v.Id, v.VersionNumber, v.GenerationRequest })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        // 2. Filter to only those with valid Jira provenance on the latest version
        var jiraOriginVersions = new List<(Guid TestCaseId, string TestKey, string Title, Guid VersionId, int VersionNumber, string JiraIssueKey)>();
        foreach (var item in latestVersions)
        {
            if (item.LatestVersion is null) continue;
            var provenance = JiraProvenanceReader.TryRead(item.LatestVersion.GenerationRequest);
            if (provenance is not null && !string.IsNullOrWhiteSpace(provenance.JiraIssueKey))
            {
                jiraOriginVersions.Add((item.TestCase.Id, item.TestCase.TestKey, item.TestCase.Title,
                    item.LatestVersion.Id, item.LatestVersion.VersionNumber, provenance.JiraIssueKey));
            }
        }

        if (jiraOriginVersions.Count == 0)
            return Array.Empty<JiraStalenessRow>();

        // 3. Get the latest completed freshness check for each Jira-origin version
        var versionIds = jiraOriginVersions.Select(v => v.VersionId).ToList();
        var auditEvents = await _db.AuditEvents
            .Where(a => a.ProjectId == projectId
                && a.EntityType == "test_case_version"
                && a.Action == "jira-change-check.completed"
                && versionIds.Contains(Guid.Parse(a.EntityId!)))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                VersionId = Guid.Parse(a.EntityId!),
                a.CreatedAt,
                a.MetadataJson
            })
            .ToListAsync(ct);

        // 4. Process audit events - keep only the latest completed check per version
        var latestChecks = new Dictionary<Guid, (DateTimeOffset CreatedAt, string Status, int ChangedFieldCount)>();
        foreach (var evt in auditEvents)
        {
            if (!latestChecks.ContainsKey(evt.VersionId))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(evt.MetadataJson!);
                    var root = doc.RootElement;
                    var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                    var changedFieldCount = root.TryGetProperty("changedFieldCount", out var countEl) ? countEl.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        latestChecks[evt.VersionId] = (evt.CreatedAt, status, changedFieldCount);
                    }
                }
                catch
                {
                    // Malformed metadata - skip
                }
            }
        }

        // 5. Build staleness rows
        var now = DateTimeOffset.UtcNow;
        var sevenDaysAgo = now.AddDays(-7);
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var weekStart = todayStart.AddDays(-7);

        var results = new List<JiraStalenessRow>();
        foreach (var version in jiraOriginVersions)
        {
            var (testCaseId, testKey, title, versionId, versionNumber, jiraIssueKey) = version;

            if (latestChecks.TryGetValue(versionId, out var check))
            {
                var (checkedAt, status, changedFieldCount) = check;
                string freshnessState;
                if (status == "changed")
                {
                    freshnessState = "changed";
                }
                else if (checkedAt >= todayStart)
                {
                    freshnessState = "current";
                }
                else if (checkedAt >= weekStart)
                {
                    freshnessState = "current";
                }
                else
                {
                    freshnessState = "stale";
                }

                results.Add(new JiraStalenessRow(
                    TestCaseId: testCaseId,
                    TestKey: testKey,
                    Title: title,
                    VersionId: versionId,
                    VersionNumber: versionNumber,
                    JiraIssueKey: jiraIssueKey,
                    FreshnessState: freshnessState,
                    LastCheckedAt: checkedAt,
                    ChangedFieldCount: changedFieldCount));
            }
            else
            {
                // No completed check found
                results.Add(new JiraStalenessRow(
                    TestCaseId: testCaseId,
                    TestKey: testKey,
                    Title: title,
                    VersionId: versionId,
                    VersionNumber: versionNumber,
                    JiraIssueKey: jiraIssueKey,
                    FreshnessState: "neverChecked",
                    LastCheckedAt: null,
                    ChangedFieldCount: null));
            }
        }

        return results;
    }

    /// <summary>
    /// Stale Jira-origin test list (Phase 4 Slice 8). Project-scoped, server-side filtered/paginated.
    /// Returns current/latest Jira-origin test cases with their freshness state.
    /// </summary>
    public async Task<PagedResult<StaleJiraTestItem>> GetStaleJiraTestsAsync(
        Guid projectId, StaleJiraTestsFilters filters, int skip, int take, CancellationToken ct)
    {
        // Reuse the staleness computation logic
        var allRows = await GetJiraStalenessAsync(projectId, CancellationToken.None);

        // Apply filters
        var query = allRows.AsQueryable();
        if (!string.IsNullOrWhiteSpace(filters.FreshnessState))
        {
            query = query.Where(r => r.FreshnessState == filters.FreshnessState);
        }
        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var term = filters.Search.Trim().ToLower();
            query = query.Where(r =>
                r.TestKey.ToLower().Contains(term) ||
                r.Title.ToLower().Contains(term) ||
                r.JiraIssueKey.ToLower().Contains(term));
        }

        // Deterministic ordering: changed -> neverChecked -> stale -> current, then by TestKey
        var stateOrder = new Dictionary<string, int>
        {
            ["changed"] = 0,
            ["neverChecked"] = 1,
            ["stale"] = 2,
            ["current"] = 3
        };
        var ordered = query
            .OrderBy(r => stateOrder.GetValueOrDefault(r.FreshnessState, 4))
            .ThenBy(r => r.TestKey)
            .ToList();

        var total = ordered.Count;
        var page = ordered.Skip(skip).Take(take).ToList();

        var items = page.Select(r => new StaleJiraTestItem(
            r.TestCaseId,
            r.TestKey,
            r.Title,
            r.VersionId,
            r.VersionNumber,
            r.JiraIssueKey,
            r.FreshnessState,
            r.LastCheckedAt,
            r.ChangedFieldCount)).ToList();

        return new PagedResult<StaleJiraTestItem>(items, total, 0, 0);
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
    public Task<PagedResult<AuditEventItem>> QueryAuditEventsAsync(Guid p, ReportDateRange r, AuditEventFilters f, int s, int t, CancellationToken ct) => Fail<PagedResult<AuditEventItem>>();
    public Task<IReadOnlyList<TestOutcomeRow>> GetTestOutcomeRowsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<TestOutcomeRow>>();
    public Task<IReadOnlyList<TestDayOutcomeRow>> GetTestDayOutcomeRowsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<TestDayOutcomeRow>>();
    public Task<IReadOnlyList<TestLastRunRow>> GetTestLastRunsAsync(Guid p, ReportDateRange r, IReadOnlyList<Guid> ids, CancellationToken ct) => Fail<IReadOnlyList<TestLastRunRow>>();
    public Task<IReadOnlyList<TestVerdictRow>> GetTestRecentVerdictsAsync(Guid p, ReportDateRange r, IReadOnlyList<Guid> ids, int perTestTake, CancellationToken ct) => Fail<IReadOnlyList<TestVerdictRow>>();
    public Task<IReadOnlyList<TestHealingRow>> GetTestHealingRowsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<TestHealingRow>>();
    public Task<CoverageCounts> GetCoverageCountsAsync(Guid p, CancellationToken ct) => Fail<CoverageCounts>();
    public Task<int> GetOpenCriticalHighDefectCountAsync(Guid p, CancellationToken ct) => Fail<int>();
    public Task<int> GetDefectsCreatedCountAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<int>();
    public Task<IReadOnlyList<NamedCount>> GetOpenDefectAgingAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<NamedCount>>();
    public Task<DurationStats> GetDurationStatsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<DurationStats>();
    public Task<IReadOnlyList<long>> GetDurationsCappedAsync(Guid p, ReportDateRange r, int cap, CancellationToken ct) => Fail<IReadOnlyList<long>>();
    public Task<IReadOnlyList<DurationDayRow>> GetDurationByDayAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<DurationDayRow>>();
    public Task<HealingStats> GetHealingStatsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<HealingStats>();
    public Task<IReadOnlyList<HealingDayRow>> GetHealingByDayAsync(Guid p, ReportDateRange r, CancellationToken ct) => Fail<IReadOnlyList<HealingDayRow>>();
    public Task<IReadOnlyList<TestCaseMetaRow>> GetTestCaseMetaAsync(Guid p, IReadOnlyList<Guid> ids, CancellationToken ct) => Fail<IReadOnlyList<TestCaseMetaRow>>();

    public Task<IReadOnlyList<JiraStalenessRow>> GetJiraStalenessAsync(Guid p, CancellationToken ct) => Fail<IReadOnlyList<JiraStalenessRow>>();

    public Task<PagedResult<StaleJiraTestItem>> GetStaleJiraTestsAsync(Guid p, StaleJiraTestsFilters f, int s, int t, CancellationToken ct) => Fail<PagedResult<StaleJiraTestItem>>();
}
