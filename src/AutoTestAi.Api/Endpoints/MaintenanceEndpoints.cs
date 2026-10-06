using AutoTestAi.Application.Maintenance;

namespace AutoTestAi.Api.Endpoints;

public sealed record RejectProposalBody(string? Reason);

/// <summary>
/// Human-gated test-maintenance proposals (Phase 4 Slice 4, web locators
/// only). All routes are project-scoped; viewing needs testcases.read,
/// scan/approve/reject need testcases.manage. Approval creates a new
/// Pending test version — never an Approved version.
/// </summary>
public static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/maintenance/proposals", (
                Guid projectId,
                string? status,
                string? signal,
                string? search,
                int? page,
                int? pageSize,
                IMaintenanceService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, new MaintenanceProposalFilters(status, signal, search),
                page ?? 1, pageSize ?? 25, ct))
            .WithName("ListMaintenanceProposals")
            .WithSummary("Paginated maintenance proposals with status/signal/search filters.");

        projects.MapPost("/{projectId:guid}/maintenance/scan", (
                Guid projectId,
                IMaintenanceService service,
                CancellationToken ct) =>
            service.ScanAsync(projectId, ct))
            .WithName("ScanMaintenanceProposals")
            .WithSummary("Run a throttled deterministic maintenance scan for the project.");

        projects.MapGet("/{projectId:guid}/maintenance/proposals/{proposalId:guid}", (
                Guid projectId,
                Guid proposalId,
                IMaintenanceService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, proposalId, ct))
            .WithName("GetMaintenanceProposal")
            .WithSummary("Maintenance proposal detail with evidence for human review.");

        projects.MapPost("/{projectId:guid}/maintenance/proposals/{proposalId:guid}/approve", (
                Guid projectId,
                Guid proposalId,
                IMaintenanceService service,
                CancellationToken ct) =>
            service.ApproveAsync(projectId, proposalId, ct))
            .WithName("ApproveMaintenanceProposal")
            .WithSummary("Approve a proposal: revalidates and creates a new Pending test version.");

        projects.MapPost("/{projectId:guid}/maintenance/proposals/{proposalId:guid}/reject", (
                Guid projectId,
                Guid proposalId,
                RejectProposalBody? body,
                IMaintenanceService service,
                CancellationToken ct) =>
            service.RejectAsync(projectId, proposalId, body?.Reason, ct))
            .WithName("RejectMaintenanceProposal")
            .WithSummary("Reject a proposal with a bounded reason; creates no version.");

        return app;
    }
}
