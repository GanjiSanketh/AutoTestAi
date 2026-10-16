using AutoTestAi.Application.Sla;

namespace AutoTestAi.Api.Endpoints;

public sealed record UpsertSlaTargetsBody(
    int? CriticalDays,
    int? HighDays,
    int? MediumDays,
    int? LowDays);

/// <summary>
/// Project-scoped SLA resolve-time targets (Phase 4 Slice 13).
/// Reads require reports.read; writes require settings.manage. The GET
/// response carries the targets plus the current open-defect breach
/// evaluation (null when unconfigured — never fabricated defaults).
/// </summary>
public static class SlaTargetEndpoints
{
    public static IEndpointRouteBuilder MapSlaTargetEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/sla-targets", (
                Guid projectId,
                ISlaTargetService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, ct))
            .WithName("GetSlaTargets")
            .WithSummary("Get the project SLA targets with current breach evaluation (null when never configured).");

        projects.MapPut("/{projectId:guid}/sla-targets", (
                Guid projectId,
                UpsertSlaTargetsBody? body,
                ISlaTargetService service,
                CancellationToken ct) =>
            service.UpsertAsync(new UpsertSlaTargetsCommand(
                projectId,
                body?.CriticalDays,
                body?.HighDays,
                body?.MediumDays,
                body?.LowDays), ct))
            .WithName("UpsertSlaTargets")
            .WithSummary("Configure per-severity SLA day thresholds (admin, idempotent).");

        return app;
    }
}
