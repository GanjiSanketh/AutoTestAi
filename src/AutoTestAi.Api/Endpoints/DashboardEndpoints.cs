using AutoTestAi.Application.Common;
using AutoTestAi.Application.Reports;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Read-only quality dashboard and operational reports (Slice 8, docs/06).
/// Descriptive aggregates over persisted Slices 1–7 data. These endpoints
/// never mutate state and never contact AI providers or Jira.
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/dashboard/summary", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetSummaryAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetDashboardSummary")
            .WithSummary("Project quality overview: KPIs plus bounded recent executions, defects, tickets and activity.");

        projects.MapGet("/{projectId:guid}/dashboard/execution-trend", (
                Guid projectId,
                string? from,
                string? to,
                string? granularity,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetExecutionTrendAsync(projectId, ReportDateRange.Parse(from, to, clock), granularity, ct))
            .WithName("GetExecutionTrend")
            .WithSummary("Time-bucketed execution counts (daily, or weekly for longer ranges).");

        projects.MapGet("/{projectId:guid}/dashboard/failure-breakdown", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetFailureBreakdownAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetFailureBreakdown")
            .WithSummary("Deterministic execution failure-classification distribution.");

        projects.MapGet("/{projectId:guid}/dashboard/defects", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetDefectOverviewAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetDefectOverview")
            .WithSummary("Defect totals with severity/classification distributions and recent defects.");

        projects.MapGet("/{projectId:guid}/dashboard/tickets", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetTicketOverviewAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetTicketOverview")
            .WithSummary("Ticket totals from internal records (never contacts Jira) with recent tickets.");

        projects.MapGet("/{projectId:guid}/reports/executions", (
                Guid projectId,
                string? from,
                string? to,
                int? page,
                int? pageSize,
                string? status,
                Guid? testCaseId,
                string? classification,
                IReportService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetExecutionsAsync(projectId, ReportDateRange.Parse(from, to, clock),
                new ExecutionReportFilters(status, testCaseId, classification),
                page ?? 1, pageSize ?? 25, ct))
            .WithName("GetExecutionReport")
            .WithSummary("Paginated execution report with status/test-case/classification filters.");

        projects.MapGet("/{projectId:guid}/reports/defects", (
                Guid projectId,
                string? from,
                string? to,
                int? page,
                int? pageSize,
                string? status,
                string? severity,
                string? classification,
                string? search,
                IReportService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetDefectsAsync(projectId, ReportDateRange.Parse(from, to, clock),
                new DefectReportFilters(status, severity, classification, search),
                page ?? 1, pageSize ?? 25, ct))
            .WithName("GetDefectReport")
            .WithSummary("Paginated defect report with status/severity/classification/search filters.");

        projects.MapGet("/{projectId:guid}/reports/tickets", (
                Guid projectId,
                string? from,
                string? to,
                int? page,
                int? pageSize,
                string? provider,
                string? syncStatus,
                IReportService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetTicketsAsync(projectId, ReportDateRange.Parse(from, to, clock),
                new TicketReportFilters(provider, syncStatus),
                page ?? 1, pageSize ?? 25, ct))
            .WithName("GetTicketReport")
            .WithSummary("Paginated ticket report from internal records (works when Jira is down).");

        projects.MapGet("/{projectId:guid}/dashboard/executive-overview", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetExecutiveOverviewAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetExecutiveOverview")
            .WithSummary("Executive quality metrics: pass/fail, flakiness, coverage, readiness, density, durations, healing (deterministic).");

        projects.MapGet("/{projectId:guid}/dashboard/flakiness-trend", (
                Guid projectId,
                string? from,
                string? to,
                string? granularity,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetFlakinessTrendAsync(projectId, ReportDateRange.Parse(from, to, clock), granularity, ct))
            .WithName("GetFlakinessTrend")
            .WithSummary("Daily (or weekly) flakiness index trend; null index means no data, never zero.");

        projects.MapGet("/{projectId:guid}/dashboard/healing", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetHealingAnalyticsAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetHealingAnalytics")
            .WithSummary("Self-healing outcomes, deterministic vs AI-assisted split, and trend (descriptive only).");

        projects.MapGet("/{projectId:guid}/dashboard/durations", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetDurationAnalyticsAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetDurationAnalytics")
            .WithSummary("Execution duration stats, percentiles, trend, and open-defect aging (no SLA config exists).");

        projects.MapGet("/{projectId:guid}/dashboard/readiness", (
                Guid projectId,
                string? from,
                string? to,
                IDashboardService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetReleaseReadinessAsync(projectId, ReportDateRange.Parse(from, to, clock), ct))
            .WithName("GetReleaseReadiness")
            .WithSummary("Deterministic release-readiness score with transparent component breakdown (informational only).");

        projects.MapGet("/{projectId:guid}/reports/flakiness", (
                Guid projectId,
                string? from,
                string? to,
                int? page,
                int? pageSize,
                string? search,
                bool? flakyOnly,
                int? minExecutions,
                string? module,
                string? priority,
                string? framework,
                bool? healedOnly,
                string? sort,
                bool? descending,
                IReportService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetFlakyTestsAsync(projectId, ReportDateRange.Parse(from, to, clock),
                new FlakyTestsFilters(search, flakyOnly ?? false, minExecutions ?? 0,
                    module, priority, framework, healedOnly ?? false),
                sort, descending ?? false, page ?? 1, pageSize ?? 25, ct))
            .WithName("GetFlakinessReport")
            .WithSummary("Paginated test-level flakiness report with deterministic sorting (no AI ranking).");

        projects.MapGet("/{projectId:guid}/reports/flakiness/export", async (
                Guid projectId,
                string? from,
                string? to,
                string? search,
                bool? flakyOnly,
                int? minExecutions,
                string? module,
                string? priority,
                string? framework,
                bool? healedOnly,
                IReportService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            {
                var export = await service.ExportFlakyTestsCsvAsync(
                    projectId, ReportDateRange.Parse(from, to, clock),
                    new FlakyTestsFilters(search, flakyOnly ?? false, minExecutions ?? 0,
                        module, priority, framework, healedOnly ?? false), ct);
                return Results.File(export.Content, export.ContentType, export.FileName);
            })
            .WithName("ExportFlakinessReport")
            .WithSummary("Bounded CSV export of the flakiness report (same filters, TestKey order, max 5000 rows).");

        // Stale Jira test list (Phase 4 Slice 8)
        projects.MapGet("/{projectId:guid}/test-cases/stale-jira", (
                Guid projectId,
                string? freshnessState,
                string? search,
                int? page,
                int? pageSize,
                IReportService service,
                CancellationToken ct) =>
            service.GetStaleJiraTestsAsync(projectId,
                new StaleJiraTestsFilters(freshnessState, search),
                page ?? 1, pageSize ?? 25, ct))
            .WithName("GetStaleJiraTests")
            .WithSummary("Paginated list of Jira-origin test cases with freshness state (changed, stale, neverChecked, current). No Jira calls.");

        return app;
    }
}
