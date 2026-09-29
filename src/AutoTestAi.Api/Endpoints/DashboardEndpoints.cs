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

        return app;
    }
}
