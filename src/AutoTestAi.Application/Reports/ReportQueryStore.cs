using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.Reports;

/// <summary>
/// Read-only aggregate/query seam for Slice 8 (implemented with EF Core
/// server-side aggregation in Infrastructure). Methods must never mutate
/// state, call AI providers, or call external ticket systems.
/// </summary>
public interface IReportQueryStore
{
    Task<TestCaseKpis> GetTestCaseKpisAsync(Guid projectId, CancellationToken ct);

    Task<ExecutionKpis> GetExecutionKpisAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<DefectKpis> GetDefectKpisAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<TicketKpis> GetTicketKpisAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<IReadOnlyList<StatusDayCount>> GetExecutionStatusByDayAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<IReadOnlyList<NamedCount>> GetFailureClassificationCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<IReadOnlyList<NamedCount>> GetDefectSeverityCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<IReadOnlyList<NamedCount>> GetDefectClassificationCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<IReadOnlyList<NamedCount>> GetTicketProviderCountsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    Task<IReadOnlyList<RecentExecutionItem>> GetRecentExecutionsAsync(
        Guid projectId, int take, CancellationToken ct);

    Task<IReadOnlyList<RecentDefectItem>> GetRecentDefectsAsync(
        Guid projectId, int take, CancellationToken ct);

    Task<IReadOnlyList<RecentTicketItem>> GetRecentTicketsAsync(
        Guid projectId, int take, CancellationToken ct);

    Task<IReadOnlyList<ActivityItem>> GetRecentActivityAsync(
        Guid projectId, ReportDateRange range, int take, CancellationToken ct);

    Task<PagedResult<ExecutionReportItem>> QueryExecutionsAsync(
        Guid projectId, ReportDateRange range, ExecutionReportFilters filters,
        int skip, int take, CancellationToken ct);

    Task<PagedResult<DefectReportItem>> QueryDefectsAsync(
        Guid projectId, ReportDateRange range, DefectReportFilters filters,
        int skip, int take, CancellationToken ct);

    Task<PagedResult<TicketReportItem>> QueryTicketsAsync(
        Guid projectId, ReportDateRange range, TicketReportFilters filters,
        int skip, int take, CancellationToken ct);
}

/// <summary>Status count for one UTC calendar day (month/day parts keep the
/// grouping translatable on both Npgsql and the InMemory test provider).</summary>
public sealed record StatusDayCount(int Year, int Month, int Day, string Status, int Count);

public sealed record NamedCount(string Name, int Count);
