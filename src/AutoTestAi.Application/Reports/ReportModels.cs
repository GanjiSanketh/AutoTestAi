using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.Reports;

/// <summary>
/// Bounded UTC date range for dashboard/report queries (Slice 8).
/// Accepted inputs are ISO-8601 timestamps; date-only values are interpreted
/// as UTC calendar-day boundaries. All persisted timestamps are UTC.
/// </summary>
public sealed record ReportDateRange(DateTimeOffset From, DateTimeOffset To)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;

    public static ReportDateRange Default(IDateTimeProvider clock)
    {
        var to = clock.UtcNow;
        return new ReportDateRange(to.AddDays(-DefaultDays), to);
    }

    /// <summary>Parses and validates from/to; throws ValidationException on misuse.</summary>
    public static ReportDateRange Parse(
        string? from, string? to, IDateTimeProvider clock, int defaultDays = DefaultDays)
    {
        var errors = new List<FieldError>();
        DateTimeOffset? fromValue = ParseBound(from, "from", errors);
        DateTimeOffset? toValue = ParseBound(to, "to", errors);
        ValidationException.ThrowIfInvalid(errors);

        var end = toValue ?? clock.UtcNow;
        var start = fromValue ?? end.AddDays(-defaultDays);
        if (start > end)
            throw new ValidationException("The 'from' date must not be after the 'to' date.",
                new[] { new FieldError("from", "The 'from' date must not be after the 'to' date.") });
        if ((end - start).TotalDays > MaxDays)
            throw new ValidationException($"The date range must not exceed {MaxDays} days.",
                new[] { new FieldError("to", $"The date range must not exceed {MaxDays} days.") });
        return new ReportDateRange(start, end);
    }

    private static DateTimeOffset? ParseBound(string? value, string field, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        // Date-only values denote UTC calendar days (start-of-day for from,
        // end-of-day handled by callers via inclusive upper bound).
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$") &&
            DateOnly.TryParse(text, out var date))
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        if (DateTimeOffset.TryParse(text, out var parsed))
            return parsed.ToUniversalTime();
        errors.Add(new FieldError(field, $"'{field}' must be a valid ISO-8601 date or timestamp (UTC assumed)."));
        return null;
    }
}

// ---------- dashboard DTOs (all database-backed, descriptive only) ----------

public sealed record TestCaseKpis(int Total, int Approved);

public sealed record ExecutionKpis(
    int Total,
    int Passed,
    int Failed,
    int Cancelled,
    int TimedOut,
    int Error,
    int QueuedOrRunning,
    double? PassRate);

public sealed record DefectKpis(
    int Total,
    int Open,
    int InProgress,
    int Resolved,
    int Closed,
    int Rejected,
    int HighSeverity);

public sealed record TicketKpis(
    int Total,
    int Synced,
    int Failed,
    int Pending);

/// <summary>Named aggregate bucket (serialized as {name,count}).</summary>
public sealed record CountItem(string Name, int Count);

public sealed record TrendPoint(
    string Date,
    int Total,
    int Passed,
    int Failed,
    int Cancelled,
    int TimedOut,
    int Error);

public sealed record RecentExecutionItem(
    Guid Id,
    string Status,
    string? TestKey,
    string? TestTitle,
    int? TestCaseVersionNumber,
    string? FailureClassification,
    long? DurationMs,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record RecentDefectItem(
    Guid Id,
    string Title,
    string Severity,
    string Status,
    string? FailureClassification,
    string? JiraKey,
    DateTimeOffset CreatedAt);

public sealed record RecentTicketItem(
    Guid Id,
    string Provider,
    string? ExternalKey,
    string SyncStatus,
    Guid? DefectId,
    DateTimeOffset CreatedAt);

public sealed record ActivityItem(
    string Action,
    string EntityType,
    string? EntityId,
    DateTimeOffset CreatedAt);

public sealed record DashboardSummaryDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    TestCaseKpis TestCases,
    ExecutionKpis Executions,
    DefectKpis Defects,
    TicketKpis Tickets,
    IReadOnlyList<RecentExecutionItem> RecentExecutions,
    IReadOnlyList<RecentDefectItem> RecentDefects,
    IReadOnlyList<RecentTicketItem> RecentTickets,
    IReadOnlyList<ActivityItem> RecentActivity);

public sealed record ExecutionTrendDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    string Granularity,
    IReadOnlyList<TrendPoint> Points);

public sealed record FailureBreakdownDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Total,
    IReadOnlyList<CountItem> Items);

public sealed record DefectOverviewDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    DefectKpis Totals,
    IReadOnlyList<CountItem> BySeverity,
    IReadOnlyList<CountItem> ByClassification,
    IReadOnlyList<RecentDefectItem> Recent);

public sealed record TicketOverviewDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    TicketKpis Totals,
    IReadOnlyList<CountItem> ByProvider,
    IReadOnlyList<RecentTicketItem> Recent);

// ---------- report DTOs (paginated, server-side filtered) ----------

public sealed record ExecutionReportFilters(
    string? Status,
    Guid? TestCaseId,
    string? Classification);

public sealed record DefectReportFilters(
    string? Status,
    string? Severity,
    string? Classification,
    string? Search);

public sealed record TicketReportFilters(
    string? Provider,
    string? SyncStatus);

public sealed record ExecutionReportItem(
    Guid Id,
    string Status,
    Guid? TestCaseId,
    string? TestKey,
    string? TestTitle,
    int? TestCaseVersionNumber,
    string? FailureClassification,
    long? DurationMs,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record DefectReportItem(
    Guid Id,
    string Title,
    string Severity,
    string Status,
    string? FailureClassification,
    string? JiraKey,
    DateTimeOffset CreatedAt);

public sealed record TicketReportItem(
    Guid Id,
    string Provider,
    string? ExternalKey,
    string? ExternalUrl,
    string SyncStatus,
    Guid? DefectId,
    string? DefectTitle,
    DateTimeOffset CreatedAt);

/// <summary>
/// Read-only quality dashboard surface (Slice 8). Descriptive operational
/// analytics over persisted Slices 1–7 data. Implementations must never
/// mutate application state and must never call AI providers or Jira.
/// </summary>
public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<ExecutionTrendDto> GetExecutionTrendAsync(
        Guid projectId, ReportDateRange range, string? granularity, CancellationToken ct);

    Task<FailureBreakdownDto> GetFailureBreakdownAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<DefectOverviewDto> GetDefectOverviewAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<TicketOverviewDto> GetTicketOverviewAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>
    /// Executive analytics (Slice 12): pass/fail ratios, flakiness index,
    /// automation coverage, release readiness, defect density, durations and
    /// healing — all deterministic over persisted data, never AI.
    /// </summary>
    Task<ExecutiveAnalyticsDto> GetExecutiveOverviewAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<FlakinessTrendDto> GetFlakinessTrendAsync(
        Guid projectId, ReportDateRange range, string? granularity, CancellationToken ct);

    Task<HealingAnalyticsDto> GetHealingAnalyticsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<DurationAnalyticsDto> GetDurationAnalyticsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<ReleaseReadinessDto> GetReleaseReadinessAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);
}

/// <summary>Read-only operational reports (Slice 8). Paginated, server-side filtered.</summary>
public interface IReportService
{
    Task<PagedResult<ExecutionReportItem>> GetExecutionsAsync(
        Guid projectId, ReportDateRange range, ExecutionReportFilters filters,
        int page, int pageSize, CancellationToken ct);

    Task<PagedResult<DefectReportItem>> GetDefectsAsync(
        Guid projectId, ReportDateRange range, DefectReportFilters filters,
        int page, int pageSize, CancellationToken ct);

    Task<PagedResult<TicketReportItem>> GetTicketsAsync(
        Guid projectId, ReportDateRange range, TicketReportFilters filters,
        int page, int pageSize, CancellationToken ct);

    /// <summary>Test-level flakiness report (Slice 12): server-side filtered/sorted/paginated.</summary>
    Task<PagedResult<FlakyTestDto>> GetFlakyTestsAsync(
        Guid projectId, ReportDateRange range, FlakyTestsFilters filters,
        string? sort, bool descending, int page, int pageSize, CancellationToken ct);

    /// <summary>Bounded CSV export of the flaky-tests report (safe fields only).</summary>
    Task<FlakyTestsExport> ExportFlakyTestsCsvAsync(
        Guid projectId, ReportDateRange range, FlakyTestsFilters filters,
        CancellationToken ct);

    /// <summary>
    /// Audit Explorer list (Phase 4 Slice 2): project-scoped, server-side
    /// filtered/paginated audit events. Reads never generate audit events.
    /// </summary>
    Task<PagedResult<AuditEventItem>> GetAuditEventsAsync(
        Guid projectId, ReportDateRange range, AuditEventFilters filters,
        int page, int pageSize, CancellationToken ct);

    /// <summary>Bounded CSV export of the audit explorer (safe fields only).</summary>
    Task<AuditEventsExport> ExportAuditEventsCsvAsync(
        Guid projectId, ReportDateRange range, AuditEventFilters filters,
        CancellationToken ct);
}
