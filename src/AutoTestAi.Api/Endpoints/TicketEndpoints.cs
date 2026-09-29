using AutoTestAi.Application.Tickets;

namespace AutoTestAi.Api.Endpoints;

public sealed record UpsertJiraIntegrationBody(
    string? BaseUrl,
    string? ProjectKey,
    string? Email,
    string? ApiToken,
    string? IssueType,
    Dictionary<string, string>? PriorityMapping,
    bool? Enabled);

/// <summary>
/// Manual Jira ticket creation from internal defects (Slice 7, docs/06 §10).
/// Human-triggered only; no automatic creation path exists.
/// </summary>
public static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/defects/{defectId:guid}/ticket", async (
                Guid projectId,
                Guid defectId,
                ITicketService service,
                CancellationToken ct) =>
            {
                var ticket = await service.CreateFromDefectAsync(projectId, defectId, ct);
                return ticket.AlreadyExisted
                    ? Results.Ok(ticket)
                    : Results.Created($"/api/v1/projects/{projectId}/defects/{defectId}/ticket", ticket);
            })
            .WithName("CreateDefectJiraTicket")
            .WithSummary("Manually create a Jira ticket from an internal defect (human action).");

        projects.MapGet("/{projectId:guid}/defects/{defectId:guid}/ticket", (
                Guid projectId,
                Guid defectId,
                ITicketService service,
                CancellationToken ct) =>
            service.GetForDefectAsync(projectId, defectId, ct))
            .WithName("GetDefectJiraTicket")
            .WithSummary("Get the synced Jira ticket for a defect, if one exists.");

        projects.MapGet("/{projectId:guid}/integrations/jira/status", (
                Guid projectId,
                IJiraIntegrationService service,
                CancellationToken ct) =>
            service.GetStatusAsync(projectId, ct))
            .WithName("GetJiraIntegrationStatus")
            .WithSummary("Safe Jira configuration status (never includes secrets).");

        projects.MapPut("/{projectId:guid}/integrations/jira", (
                Guid projectId,
                UpsertJiraIntegrationBody? body,
                IJiraIntegrationService service,
                CancellationToken ct) =>
            service.UpsertAsync(new UpsertJiraIntegrationCommand(
                projectId,
                body?.BaseUrl ?? string.Empty,
                body?.ProjectKey ?? string.Empty,
                body?.Email ?? string.Empty,
                body?.ApiToken,
                body?.IssueType,
                body?.PriorityMapping,
                body?.Enabled ?? true), ct))
            .WithName("UpsertJiraIntegration")
            .WithSummary("Configure the project Jira integration (admin, secret-safe).");

        return app;
    }
}
