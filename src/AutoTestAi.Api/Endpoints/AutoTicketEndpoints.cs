using AutoTestAi.Application.Tickets;

namespace AutoTestAi.Api.Endpoints;

public sealed record UpsertAutoTicketPolicyBody(
    bool? Enabled,
    Guid? IntegrationId,
    IReadOnlyList<string>? Severities,
    IReadOnlyList<string>? DefectStatuses,
    IReadOnlyList<string>? Classifications,
    decimal? MinimumConfidence);

/// <summary>
/// Automated defect ticketing: policy-controlled Jira creation
/// (Phase 2 Slice 10, docs/06 §10.1). Manual Slice 7 endpoints are unchanged.
/// </summary>
public static class AutoTicketEndpoints
{
    public static IEndpointRouteBuilder MapAutoTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/auto-ticket-policy", (
                Guid projectId,
                IAutoTicketPolicyService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, ct))
            .WithName("GetAutoTicketPolicy")
            .WithSummary("Get the project automatic Jira ticket policy (null when never configured).");

        projects.MapPut("/{projectId:guid}/auto-ticket-policy", (
                Guid projectId,
                UpsertAutoTicketPolicyBody? body,
                IAutoTicketPolicyService service,
                CancellationToken ct) =>
            service.UpsertAsync(new UpsertAutoTicketPolicyCommand(
                projectId,
                body?.Enabled ?? false,
                body?.IntegrationId,
                body?.Severities,
                body?.DefectStatuses,
                body?.Classifications,
                body?.MinimumConfidence), ct))
            .WithName("UpsertAutoTicketPolicy")
            .WithSummary("Configure automatic Jira ticket creation policy (admin, secret-safe).");

        projects.MapGet("/{projectId:guid}/auto-ticket-policy/status", (
                Guid projectId,
                IAutoTicketPolicyService service,
                CancellationToken ct) =>
            service.GetStatusAsync(projectId, ct))
            .WithName("GetAutoTicketStatus")
            .WithSummary("Automation status and counts (never includes secrets).");

        projects.MapPost("/{projectId:guid}/defects/{defectId:guid}/ticket/automation/retry", async (
                Guid projectId,
                Guid defectId,
                IAutomatedTicketService service,
                CancellationToken ct) =>
            {
                var ticket = await service.RetryFailedAsync(projectId, defectId, ct);
                return ticket.AlreadyExisted && ticket.SyncStatus == "Synced"
                    ? Results.Ok(ticket)
                    : Results.Accepted($"/api/v1/projects/{projectId}/defects/{defectId}/ticket", ticket);
            })
            .WithName("RetryAutoTicket")
            .WithSummary("Retry a failed automatic ticket (requeues; returns existing synced ticket idempotently).");

        return app;
    }
}
