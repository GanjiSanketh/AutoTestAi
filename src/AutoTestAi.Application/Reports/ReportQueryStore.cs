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

    // ---------- executive analytics primitives (Slice 12; all server-side grouped) ----------

    /// <summary>Per-test verdict counts over terminal executions in the window.</summary>
    Task<IReadOnlyList<TestOutcomeRow>> GetTestOutcomeRowsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>Per-test UTC-day status counts over terminal executions in the window.</summary>
    Task<IReadOnlyList<TestDayOutcomeRow>> GetTestDayOutcomeRowsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>
    /// Latest terminal execution per test case, for the display page only
    /// (page-bounded follow-ups). Dates for sorting come from
    /// <see cref="GetTestOutcomeRowsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TestLastRunRow>> GetTestLastRunsAsync(
        Guid projectId, ReportDateRange range, IReadOnlyList<Guid> testCaseIds,
        CancellationToken ct);

    /// <summary>
    /// Recent terminal pass/fail verdicts (Phase 4 Slice 1 forecasting
    /// input). Flat newest-first rows (CreatedAt desc, id tiebreak,
    /// mirroring GetTestLastRunsAsync) with a server-side total cap; the
    /// caller groups per test and applies perTestTake. Other statuses are
    /// excluded upstream, exactly like the flakiness aggregates.
    /// </summary>
    Task<IReadOnlyList<TestVerdictRow>> GetTestRecentVerdictsAsync(
        Guid projectId, ReportDateRange range, IReadOnlyList<Guid> testCaseIds,
        int perTestTake, CancellationToken ct);

    /// <summary>Healing activity per test case in the window.</summary>
    Task<IReadOnlyList<TestHealingRow>> GetTestHealingRowsAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>Eligible vs. automated test-case counts (review-lifecycle based).</summary>
    Task<CoverageCounts> GetCoverageCountsAsync(Guid projectId, CancellationToken ct);

    /// <summary>Currently open Critical/High defects (window-independent triage signal).</summary>
    Task<int> GetOpenCriticalHighDefectCountAsync(Guid projectId, CancellationToken ct);

    /// <summary>Defects created within the window (density numerator).</summary>
    Task<int> GetDefectsCreatedCountAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>
    /// Open-defect age buckets (SLA-adjacent aging; no invented targets).
    /// Ages measure against the window end so historical windows are stable.
    /// </summary>
    Task<IReadOnlyList<NamedCount>> GetOpenDefectAgingAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>Duration stats over valid (non-null, non-negative) terminal samples.</summary>
    Task<DurationStats> GetDurationStatsAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>Sorted valid durations, capped server-side (percentile input).</summary>
    Task<IReadOnlyList<long>> GetDurationsCappedAsync(
        Guid projectId, ReportDateRange range, int cap, CancellationToken ct);

    /// <summary>Daily duration averages over valid samples.</summary>
    Task<IReadOnlyList<DurationDayRow>> GetDurationByDayAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>Healing totals + distinct tests/executions in the window.</summary>
    Task<HealingStats> GetHealingStatsAsync(Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>Daily healing counts in the window.</summary>
    Task<IReadOnlyList<HealingDayRow>> GetHealingByDayAsync(
        Guid projectId, ReportDateRange range, CancellationToken ct);

    /// <summary>Display metadata for a bounded set of test cases (same project).</summary>
    Task<IReadOnlyList<TestCaseMetaRow>> GetTestCaseMetaAsync(
        Guid projectId, IReadOnlyList<Guid> testCaseIds, CancellationToken ct);
}

/// <summary>Status count for one UTC calendar day (month/day parts keep the
/// grouping translatable on both Npgsql and the InMemory test provider).</summary>
public sealed record StatusDayCount(int Year, int Month, int Day, string Status, int Count);

public sealed record NamedCount(string Name, int Count);
