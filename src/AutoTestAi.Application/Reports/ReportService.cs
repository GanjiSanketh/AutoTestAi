using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Reports;

/// <summary>
/// Read-only operational reports (Slice 8). Server-side filtering and
/// pagination over persisted data. Never mutates state.
/// </summary>
public sealed class ReportService : IReportService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int AuditExportCap = 5000;

    private static readonly IReadOnlySet<string> ExecutionStatuses =
        new HashSet<string>(Enum.GetNames<ExecutionStatus>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> DefectStatuses =
        new HashSet<string>(Enum.GetNames<DefectStatus>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> Severities =
        new HashSet<string>(Enum.GetNames<Severity>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> Classifications =
        new HashSet<string>(Enum.GetNames<FailureClassification>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> SyncStatuses =
        new HashSet<string>(Enum.GetNames<TicketSyncStatus>(), StringComparer.OrdinalIgnoreCase);

    private readonly IReportQueryStore _store;
    private readonly IAuthorizationService _authorization;

    public ReportService(IReportQueryStore store, IAuthorizationService authorization)
    {
        _store = store;
        _authorization = authorization;
    }

    public async Task<PagedResult<ExecutionReportItem>> GetExecutionsAsync(
        Guid projectId, ReportDateRange range, ExecutionReportFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        DashboardService.ValidateExecutionStatus(filters.Status);
        DashboardService.ValidateClassification(filters.Classification);
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.QueryExecutionsAsync(projectId, range,
            new ExecutionReportFilters(
                Normalize(filters.Status), filters.TestCaseId, Normalize(filters.Classification)),
            skip, take, ct);
        return new PagedResult<ExecutionReportItem>(result.Items, result.TotalCount, pageNumber, size);
    }

    public async Task<PagedResult<DefectReportItem>> GetDefectsAsync(
        Guid projectId, ReportDateRange range, DefectReportFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        ValidateEnum(filters.Status, DefectStatuses, "status", "Status must be a valid defect status.");
        ValidateEnum(filters.Severity, Severities, "severity", "Severity must be 'Critical', 'High', 'Medium' or 'Low'.");
        if (!string.IsNullOrWhiteSpace(filters.Classification) &&
            !Classifications.Contains(filters.Classification.Trim()) &&
            !string.Equals(filters.Classification.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Classification filter is invalid.",
                new[] { new FieldError("classification", "Classification must be a valid failure classification.") });
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.QueryDefectsAsync(projectId, range,
            new DefectReportFilters(
                Normalize(filters.Status), Normalize(filters.Severity),
                Normalize(filters.Classification), filters.Search?.Trim()),
            skip, take, ct);
        return new PagedResult<DefectReportItem>(result.Items, result.TotalCount, pageNumber, size);
    }

    public async Task<PagedResult<TicketReportItem>> GetTicketsAsync(
        Guid projectId, ReportDateRange range, TicketReportFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        ValidateEnum(filters.SyncStatus, SyncStatuses, "syncStatus", "Sync status must be 'Pending', 'Synced' or 'Failed'.");
        if (!string.IsNullOrWhiteSpace(filters.Provider) && filters.Provider.Trim().Length > 60)
            throw new ValidationException("Provider filter is invalid.",
                new[] { new FieldError("provider", "Provider must be at most 60 characters.") });
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.QueryTicketsAsync(projectId, range,
            new TicketReportFilters(filters.Provider?.Trim(), Normalize(filters.SyncStatus)),
            skip, take, ct);
        return new PagedResult<TicketReportItem>(result.Items, result.TotalCount, pageNumber, size);
    }

    // ---------- test-level flakiness (Slice 12; deterministic, read-only) ----------

    public async Task<PagedResult<FlakyTestDto>> GetFlakyTestsAsync(
        Guid projectId, ReportDateRange range, FlakyTestsFilters filters,
        string? sort, bool descending, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        var normalized = NormalizeFilters(filters);
        var sortKey = NormalizeSort(sort);
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var candidates = await LoadCandidatesAsync(projectId, range, ct);
        var filtered = FlakyReportShaper.ApplyFilters(candidates, normalized);
        filtered = await AttachForecastsAsync(projectId, range, filtered, ct);
        var sorted = FlakyReportShaper.ApplySort(filtered, sortKey, descending);
        var pageIds = sorted.Skip(skip).Take(take).Select(c => c.TestCaseId).ToList();
        var lastRuns = (await _store.GetTestLastRunsAsync(projectId, range, pageIds, ct))
            .ToDictionary(r => r.TestCaseId, r => r);
        var items = sorted.Skip(skip).Take(take)
            .Select(c => MapFlakyTest(c, lastRuns.TryGetValue(c.TestCaseId, out var last) ? last : null))
            .ToList();
        return new PagedResult<FlakyTestDto>(items, filtered.Count, pageNumber, size);
    }

    public async Task<FlakyTestsExport> ExportFlakyTestsCsvAsync(
        Guid projectId, ReportDateRange range, FlakyTestsFilters filters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        var normalized = NormalizeFilters(filters);
        var candidates = await LoadCandidatesAsync(projectId, range, ct);
        // Bounded deterministic export: same filters, TestKey order, hard cap.
        var filtered = FlakyReportShaper.ApplyFilters(candidates, normalized);
        filtered = await AttachForecastsAsync(projectId, range, filtered, ct);
        var sorted = FlakyReportShaper.ApplySort(filtered, "testKey", descending: false);
        var capped = sorted.Take(5000).ToList();
        var pageIds = capped.Select(c => c.TestCaseId).ToList();
        var lastRuns = (await _store.GetTestLastRunsAsync(projectId, range, pageIds, ct))
            .ToDictionary(r => r.TestCaseId, r => r);
        var rows = capped
            .Select(c => MapFlakyTest(c, lastRuns.TryGetValue(c.TestCaseId, out var last) ? last : null))
            .ToList();
        var fileName = $"flakiness-{projectId:N}-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}.csv";
        return new FlakyTestsExport(fileName, "text/csv", CsvExporter.ExportFlakyTests(rows));
    }

    // ---------- audit explorer (Phase 4 Slice 2; deterministic, read-only) ----------

    public async Task<PagedResult<AuditEventItem>> GetAuditEventsAsync(
        Guid projectId, ReportDateRange range, AuditEventFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        var normalized = NormalizeAuditFilters(filters);
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.QueryAuditEventsAsync(projectId, range, normalized, skip, take, ct);
        return new PagedResult<AuditEventItem>(result.Items, result.TotalCount, pageNumber, size);
    }

    public async Task<AuditEventsExport> ExportAuditEventsCsvAsync(
        Guid projectId, ReportDateRange range, AuditEventFilters filters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        var normalized = NormalizeAuditFilters(filters);
        // Bounded deterministic export: same filters, CreatedAt/Id order, hard cap.
        var result = await _store.QueryAuditEventsAsync(projectId, range, normalized, 0, AuditExportCap, ct);
        var fileName = $"audit-{projectId:N}-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}.csv";
        return new AuditEventsExport(fileName, "text/csv", CsvExporter.ExportAuditEvents(result.Items));
    }

    private static AuditEventFilters NormalizeAuditFilters(AuditEventFilters filters)
    {
        ValidateFilterLength(filters.Action, 100, "action", "Action filter must be at most 100 characters.");
        ValidateFilterLength(filters.EntityType, 100, "entityType", "Entity type filter must be at most 100 characters.");
        return new AuditEventFilters(
            Normalize(filters.Action), filters.ActorUserId, Normalize(filters.EntityType));
    }

    private async Task<IReadOnlyList<FlakyCandidate>> LoadCandidatesAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        var outcomes = await _store.GetTestOutcomeRowsAsync(projectId, range, ct);
        if (outcomes.Count == 0) return Array.Empty<FlakyCandidate>();
        var healing = (await _store.GetTestHealingRowsAsync(projectId, range, ct))
            .ToDictionary(h => h.TestCaseId, h => h);
        var meta = (await _store.GetTestCaseMetaAsync(
                projectId, outcomes.Select(o => o.TestCaseId).ToList(), ct))
            .ToDictionary(m => m.TestCaseId, m => m);
        var candidates = new List<FlakyCandidate>(outcomes.Count);
        foreach (var outcome in outcomes)
        {
            // Project predicate is enforced store-side; a missing meta row
            // (deleted case) is skipped rather than surfaced without identity.
            if (!meta.TryGetValue(outcome.TestCaseId, out var m)) continue;
            healing.TryGetValue(outcome.TestCaseId, out var h);
            candidates.Add(new FlakyCandidate(
                outcome.TestCaseId, m.TestKey, m.Title, m.Module, m.Priority,
                m.Framework, m.Platform,
                outcome.Passed, outcome.Failed, outcome.Other, outcome.LastRunAt,
                h?.Attempts ?? 0, h?.Applied ?? 0));
        }
        return candidates;
    }

    private static FlakyTestDto MapFlakyTest(FlakyCandidate candidate, TestLastRunRow? last)
        => new(candidate.TestCaseId, candidate.TestKey, candidate.Title,
            candidate.Module, candidate.Priority, candidate.Framework, candidate.Platform,
            candidate.TotalExecutions, candidate.Passed, candidate.Failed, candidate.Other,
            candidate.IsFlaky, candidate.FlakinessRate,
            last?.Status, candidate.LastRunAt,
            candidate.HealingAttempts, candidate.HealedRuns,
            candidate.Forecast?.RiskScore, candidate.Forecast?.RiskBand,
            candidate.Forecast?.RiskFactors ?? Array.Empty<string>());

    /// <summary>
    /// Phase 4 Slice 1: advisory risk forecasts over the existing verdict
    /// history. One bounded read; pure per-test computation; read-only.
    /// </summary>
    private async Task<IReadOnlyList<FlakyCandidate>> AttachForecastsAsync(
        Guid projectId, ReportDateRange range, IReadOnlyList<FlakyCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return candidates;
        var ids = candidates.Select(c => c.TestCaseId).ToList();
        var rows = await _store.GetTestRecentVerdictsAsync(projectId, range, ids, FlakinessForecast.MaxVerdictsPerTest, ct);
        var byTest = rows
            .GroupBy(r => r.TestCaseId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.CreatedAt).Select(r => r.Passed).ToList());
        return candidates.Select(c =>
        {
            if (!byTest.TryGetValue(c.TestCaseId, out var verdicts))
                verdicts = new List<bool>();
            var forecast = FlakinessForecast.Forecast(verdicts, c.Passed, c.Failed);
            return c with { Forecast = forecast };
        }).ToList();
    }

    private static FlakyTestsFilters NormalizeFilters(FlakyTestsFilters filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.Priority))
            ValidateEnum(filters.Priority, Priorities, "priority", "Priority must be 'Critical', 'High', 'Medium' or 'Low'.");
        return new FlakyTestsFilters(
            string.IsNullOrWhiteSpace(filters.Search) ? null : filters.Search.Trim(),
            filters.FlakyOnly,
            Math.Max(0, filters.MinExecutions),
            Normalize(filters.Module),
            Normalize(filters.Priority),
            Normalize(filters.Framework),
            filters.HealedOnly);
    }

    private static string NormalizeSort(string? sort)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? FlakyReportShaper.DefaultSort : sort.Trim();
        if (!FlakyReportShaper.SortKeys.Contains(key))
            throw new ValidationException("Sort must be one of 'testKey', 'title', 'executions', 'flakinessRate', 'lastRun', 'riskScore'.",
                new[] { new FieldError("sort", "Sort must be one of 'testKey', 'title', 'executions', 'flakinessRate', 'lastRun', 'riskScore'.") });
        return key;
    }

    private static readonly IReadOnlySet<string> Priorities =
        new HashSet<string>(Enum.GetNames<Priority>(), StringComparer.OrdinalIgnoreCase);

    private static (int Skip, int Take, int Page, int Size) Paginate(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        return ((page - 1) * pageSize, pageSize, page, pageSize);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateEnum(
        string? value, IReadOnlySet<string> allowed, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!allowed.Contains(value.Trim()))
            throw new ValidationException(message, new[] { new FieldError(field, message) });
    }

    private static void ValidateFilterLength(string? value, int maxLength, string field, string message)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > maxLength)
            throw new ValidationException(message, new[] { new FieldError(field, message) });
    }

    /// <summary>
    /// Stale Jira-origin test list (Phase 4 Slice 8). Project-scoped, server-side
    /// filtered/paginated. Read-only, no external Jira calls.
    /// </summary>
    public async Task<PagedResult<StaleJiraTestItem>> GetStaleJiraTestsAsync(
        Guid projectId, StaleJiraTestsFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);

        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.GetStaleJiraTestsAsync(projectId, filters ?? new StaleJiraTestsFilters(null, null), skip, take, ct);
        return new PagedResult<StaleJiraTestItem>(result.Items, result.TotalCount, pageNumber, size);
    }
}
