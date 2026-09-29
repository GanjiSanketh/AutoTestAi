using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Reports;

/// <summary>
/// Read-only quality dashboard (Slice 8). Descriptive aggregates over
/// persisted Slices 1–7 data. Never mutates state, never calls AI or Jira.
/// </summary>
public sealed class DashboardService : IDashboardService
{
    private const int RecentTake = 8;
    private const int ActivityTake = 10;

    private static readonly IReadOnlySet<string> ExecutionStatuses =
        new HashSet<string>(Enum.GetNames<ExecutionStatus>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> Classifications =
        new HashSet<string>(Enum.GetNames<FailureClassification>(), StringComparer.OrdinalIgnoreCase);

    private readonly IReportQueryStore _store;
    private readonly IAuthorizationService _authorization;

    public DashboardService(IReportQueryStore store, IAuthorizationService authorization)
    {
        _store = store;
        _authorization = authorization;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.DashboardRead, ct);

        var testCases = await _store.GetTestCaseKpisAsync(projectId, ct);
        var executions = await _store.GetExecutionKpisAsync(projectId, range, ct);
        var defects = await _store.GetDefectKpisAsync(projectId, range, ct);
        var tickets = await _store.GetTicketKpisAsync(projectId, range, ct);
        var recentExecutions = await _store.GetRecentExecutionsAsync(projectId, RecentTake, ct);
        var recentDefects = await _store.GetRecentDefectsAsync(projectId, RecentTake, ct);
        var recentTickets = await _store.GetRecentTicketsAsync(projectId, RecentTake, ct);
        var activity = await _store.GetRecentActivityAsync(projectId, range, ActivityTake, ct);
        return new DashboardSummaryDto(
            projectId, range.From, range.To,
            testCases, executions, defects, tickets,
            recentExecutions, recentDefects, recentTickets, activity);
    }

    public async Task<ExecutionTrendDto> GetExecutionTrendAsync(
        Guid projectId, ReportDateRange range, string? granularity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.DashboardRead, ct);
        var mode = NormalizeGranularity(granularity, range);
        var buckets = await _store.GetExecutionStatusByDayAsync(projectId, range, ct);
        var points = TrendBuilder.Build(range, mode, buckets);
        return new ExecutionTrendDto(projectId, range.From, range.To, mode, points);
    }

    public async Task<FailureBreakdownDto> GetFailureBreakdownAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.DashboardRead, ct);
        var items = await _store.GetFailureClassificationCountsAsync(projectId, range, ct);
        return new FailureBreakdownDto(projectId, range.From, range.To, items.Sum(i => i.Count), ToCountItems(items));
    }

    public async Task<DefectOverviewDto> GetDefectOverviewAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.DashboardRead, ct);
        var totals = await _store.GetDefectKpisAsync(projectId, range, ct);
        var bySeverity = await _store.GetDefectSeverityCountsAsync(projectId, range, ct);
        var byClassification = await _store.GetDefectClassificationCountsAsync(projectId, range, ct);
        var recent = await _store.GetRecentDefectsAsync(projectId, RecentTake, ct);
        return new DefectOverviewDto(projectId, range.From, range.To, totals,
            ToCountItems(bySeverity), ToCountItems(byClassification), recent);
    }

    public async Task<TicketOverviewDto> GetTicketOverviewAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.DashboardRead, ct);
        var totals = await _store.GetTicketKpisAsync(projectId, range, ct);
        var byProvider = await _store.GetTicketProviderCountsAsync(projectId, range, ct);
        var recent = await _store.GetRecentTicketsAsync(projectId, RecentTake, ct);
        return new TicketOverviewDto(projectId, range.From, range.To, totals, ToCountItems(byProvider), recent);
    }

    internal static string NormalizeGranularity(string? granularity, ReportDateRange range)
    {
        if (!string.IsNullOrWhiteSpace(granularity))
        {
            var mode = granularity.Trim().ToLowerInvariant();
            if (mode is "day" or "week") return mode;
            throw new ValidationException("Granularity must be 'day' or 'week'.",
                new[] { new FieldError("granularity", "Granularity must be 'day' or 'week'.") });
        }
        return (range.To - range.From).TotalDays > 62 ? "week" : "day";
    }

    internal static void ValidateClassification(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Classifications.Contains(value.Trim()) && !string.Equals(value.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Classification filter is invalid.",
                new[] { new FieldError("classification", "Classification must be a valid failure classification.") });
    }

    private static IReadOnlyList<CountItem> ToCountItems(IReadOnlyList<NamedCount> items)
        => items.Select(i => new CountItem(i.Name, i.Count)).ToList();

    internal static void ValidateExecutionStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!ExecutionStatuses.Contains(value.Trim()))
            throw new ValidationException("Status filter is invalid.",
                new[] { new FieldError("status", "Status must be a valid execution status.") });
    }
}

/// <summary>Deterministic UTC bucket builder shared by trend shaping (pure, unit-tested).</summary>
public static class TrendBuilder
{
    public static IReadOnlyList<TrendPoint> Build(
        ReportDateRange range, string granularity, IReadOnlyList<StatusDayCount> buckets)
    {
        var start = StartOfDayUtc(range.From);
        var end = StartOfDayUtc(range.To);
        var points = new List<TrendPoint>();
        if (string.Equals(granularity, "week", StringComparison.OrdinalIgnoreCase))
        {
            // Week buckets starting Monday (UTC).
            var cursor = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
            while (cursor <= end)
            {
                var weekEnd = cursor.AddDays(7);
                points.Add(SumBucket(cursor, cursor, weekEnd, buckets));
                cursor = weekEnd;
            }
            return points;
        }
        for (var day = start; day <= end; day = day.AddDays(1))
            points.Add(SumBucket(day, day, day.AddDays(1), buckets));
        return points;
    }

    private static TrendPoint SumBucket(
        DateTime label, DateTime from, DateTime to, IReadOnlyList<StatusDayCount> buckets)
    {
        int passed = 0, failed = 0, cancelled = 0, timedOut = 0, error = 0, other = 0;
        foreach (var b in buckets)
        {
            var day = new DateTime(b.Year, b.Month, b.Day, 0, 0, 0, DateTimeKind.Utc);
            if (day < from || day >= to) continue;
            switch (b.Status)
            {
                case nameof(ExecutionStatus.Passed): passed += b.Count; break;
                case nameof(ExecutionStatus.Failed): failed += b.Count; break;
                case nameof(ExecutionStatus.Cancelled): cancelled += b.Count; break;
                case nameof(ExecutionStatus.TimedOut): timedOut += b.Count; break;
                case nameof(ExecutionStatus.Error): error += b.Count; break;
                default: other += b.Count; break;
            }
        }
        return new TrendPoint(
            label.ToString("yyyy-MM-dd"),
            passed + failed + cancelled + timedOut + error + other,
            passed, failed, cancelled, timedOut, error);
    }

    private static DateTime StartOfDayUtc(DateTimeOffset value)
        => new(value.UtcDateTime.Year, value.UtcDateTime.Month, value.UtcDateTime.Day,
            0, 0, 0, DateTimeKind.Utc);
}
