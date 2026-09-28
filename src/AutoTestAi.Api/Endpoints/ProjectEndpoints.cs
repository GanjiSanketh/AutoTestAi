using AutoTestAi.Application.Projects;

namespace AutoTestAi.Api.Endpoints;

// ---------- request bodies (key is immutable: never accepted on update) ----------

public sealed record CreateProjectBody(
    string? Name,
    string? Key,
    string? Description,
    string? RepositoryUrl,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string? Status);

public sealed record UpdateProjectBody(
    string? Name,
    string? Description,
    string? RepositoryUrl,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string? Status,
    Guid? DefaultEnvironmentId);

public sealed record AddMemberBody(Guid? UserId, string? Email, Guid? RoleId);

public sealed record RoleItem(Guid Id, string Name, string? Description);

public sealed record UpdateMemberRoleBody(Guid? RoleId);

public sealed record CreateEnvironmentBody(string? Name, string? BaseUrl, string? Status);

public sealed record UpdateEnvironmentBody(
    string? Name,
    string? BaseUrl,
    string? Status,
    bool? SetAsDefault);

/// <summary>Project management surface (docs/06 §5). All routes require authentication;
/// the service layer enforces permissions + project membership.</summary>
public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/", (
                int? page,
                int? pageSize,
                string? search,
                IProjectService service,
                CancellationToken ct) =>
            service.ListAsync(page ?? 1, pageSize ?? 25, search, ct))
            .WithName("ListProjects")
            .WithSummary("List projects visible to the caller (paginated).");

        projects.MapPost("/", async (
                CreateProjectBody body,
                IProjectService service,
                CancellationToken ct) =>
            {
                var created = await service.CreateAsync(new CreateProjectCommand(
                    body?.Name ?? string.Empty,
                    body?.Key ?? string.Empty,
                    body?.Description,
                    body?.RepositoryUrl,
                    body?.TargetUrl,
                    body?.Framework,
                    body?.Platform,
                    body?.Status), ct);
                return Results.Created($"/api/v1/projects/{created.Id}", created);
            })
            .WithName("CreateProject")
            .WithSummary("Create a project; the creator becomes a member.");

        projects.MapGet("/{projectId:guid}", (
                Guid projectId,
                IProjectService service,
                CancellationToken ct) =>
            service.GetByIdAsync(projectId, ct))
            .WithName("GetProject")
            .WithSummary("Project details for authorized callers.");

        projects.MapPut("/{projectId:guid}", (
                Guid projectId,
                UpdateProjectBody body,
                IProjectService service,
                CancellationToken ct) =>
            service.UpdateAsync(projectId, new UpdateProjectCommand(
                body?.Name ?? string.Empty,
                body?.Description,
                body?.RepositoryUrl,
                body?.TargetUrl,
                body?.Framework,
                body?.Platform,
                body?.Status,
                body?.DefaultEnvironmentId), ct))
            .WithName("UpdateProject")
            .WithSummary("Update project metadata (key is immutable).");

        projects.MapDelete("/{projectId:guid}", async (
                Guid projectId,
                IProjectService service,
                CancellationToken ct) =>
            {
                await service.ArchiveAsync(projectId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteProject")
            .WithSummary("Soft delete: archives the project, preserving history.");

        projects.MapGet("/{projectId:guid}/members", (
                Guid projectId,
                IProjectService service,
                CancellationToken ct) =>
            service.GetMembersAsync(projectId, ct))
            .WithName("ListProjectMembers")
            .WithSummary("Safe member list for authorized callers.");

        projects.MapPost("/{projectId:guid}/members", async (
                Guid projectId,
                AddMemberBody body,
                IProjectService service,
                CancellationToken ct) =>
            {
                if ((body?.UserId is null && string.IsNullOrWhiteSpace(body?.Email)) || body?.RoleId is null)
                    throw new AutoTestAi.Application.Common.ValidationException(
                        "User and role are required.",
                        new[] {
                            new AutoTestAi.Application.Common.FieldError("userId", "Provide a user id or email."),
                            new AutoTestAi.Application.Common.FieldError("roleId", "Role id is required."),
                        });
                var member = await service.AddMemberAsync(projectId,
                    new AddMemberCommand(body!.UserId, body.Email, body.RoleId.Value), ct);
                return Results.Created(
                    $"/api/v1/projects/{projectId}/members/{member.UserId}", member);
            })
            .WithName("AddProjectMember")
            .WithSummary("Add a member with a project role.");

        projects.MapPut("/{projectId:guid}/members/{userId:guid}", (
                Guid projectId,
                Guid userId,
                UpdateMemberRoleBody body,
                IProjectService service,
                CancellationToken ct) =>
            {
                if (body?.RoleId is null)
                    throw new AutoTestAi.Application.Common.ValidationException(
                        "Role is required.",
                        new[] { new AutoTestAi.Application.Common.FieldError("roleId", "Role id is required.") });
                return service.UpdateMemberRoleAsync(projectId, userId,
                    new UpdateMemberRoleCommand(body.RoleId.Value), ct);
            })
            .WithName("UpdateProjectMemberRole")
            .WithSummary("Change a member's project role.");

        projects.MapDelete("/{projectId:guid}/members/{userId:guid}", async (
                Guid projectId,
                Guid userId,
                IProjectService service,
                CancellationToken ct) =>
            {
                await service.RemoveMemberAsync(projectId, userId, ct);
                return Results.NoContent();
            })
            .WithName("RemoveProjectMember")
            .WithSummary("Remove a project member.");

        projects.MapGet("/{projectId:guid}/environments", (
                Guid projectId,
                IProjectService service,
                CancellationToken ct) =>
            service.ListEnvironmentsAsync(projectId, ct))
            .WithName("ListEnvironments")
            .WithSummary("Environment metadata for authorized callers.");

        projects.MapPost("/{projectId:guid}/environments", async (
                Guid projectId,
                CreateEnvironmentBody body,
                IProjectService service,
                CancellationToken ct) =>
            {
                var env = await service.CreateEnvironmentAsync(projectId, new CreateEnvironmentCommand(
                    body?.Name ?? string.Empty, body?.BaseUrl, body?.Status), ct);
                return Results.Created($"/api/v1/environments/{env.Id}", env);
            })
            .WithName("CreateEnvironment")
            .WithSummary("Create environment metadata (no secrets).");

        var environments = app.MapGroup("/api/v1/environments").RequireAuthorization();

        environments.MapPut("/{environmentId:guid}", (
                Guid environmentId,
                UpdateEnvironmentBody body,
                IProjectService service,
                CancellationToken ct) =>
            service.UpdateEnvironmentAsync(environmentId, new UpdateEnvironmentCommand(
                body?.Name, body?.BaseUrl, body?.Status, body?.SetAsDefault), ct))
            .WithName("UpdateEnvironment")
            .WithSummary("Update environment metadata; optionally set as default.");

        app.MapGroup("/api/v1/roles")
            .RequireAuthorization()
            .MapGet("/", async (IProjectService service, CancellationToken ct) =>
            {
                var roles = await service.ListRolesAsync(ct);
                return Results.Ok(roles.Select(r => new RoleItem(r.Id, r.Name, r.Description)).ToList());
            })
            .WithName("ListRoles")
            .WithSummary("Reference data for member role assignment.");

        environments.MapDelete("/{environmentId:guid}", async (
                Guid environmentId,
                IProjectService service,
                CancellationToken ct) =>
            {
                await service.DeleteEnvironmentAsync(environmentId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteEnvironment")
            .WithSummary("Soft delete an environment; clears default if set.");

        return app;
    }
}
