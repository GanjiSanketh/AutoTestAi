using AutoTestAi.Application.Common;
using AutoTestAi.Application.Reports;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Project-scoped read-only Audit Explorer (Phase 4 Slice 2).
/// List and bounded CSV export over the existing audit_events table.
/// These endpoints never mutate state, never expose metadata/IP/User-Agent,
/// and never generate audit events. Authorization is reports.read plus
/// existing project access; the project predicate is enforced server-side.
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/audit/events", (
                Guid projectId,
                string? from,
                string? to,
                string? action,
                Guid? actor,
                string? entityType,
                int? page,
                int? pageSize,
                IReportService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            service.GetAuditEventsAsync(projectId, ReportDateRange.Parse(from, to, clock),
                new AuditEventFilters(action, actor, entityType),
                page ?? 1, pageSize ?? 25, ct))
            .WithName("GetAuditEvents")
            .WithSummary("Paginated project audit events with action/actor/entity-type filters (safe fields only).");

        projects.MapGet("/{projectId:guid}/audit/export", async (
                Guid projectId,
                string? from,
                string? to,
                string? action,
                Guid? actor,
                string? entityType,
                IReportService service,
                IDateTimeProvider clock,
                CancellationToken ct) =>
            {
                var export = await service.ExportAuditEventsCsvAsync(
                    projectId, ReportDateRange.Parse(from, to, clock),
                    new AuditEventFilters(action, actor, entityType), ct);
                return Results.File(export.Content, export.ContentType, export.FileName);
            })
            .WithName("ExportAuditEvents")
            .WithSummary("Bounded CSV export of project audit events (same filters, max 5000 rows).");

        return app;
    }
}
