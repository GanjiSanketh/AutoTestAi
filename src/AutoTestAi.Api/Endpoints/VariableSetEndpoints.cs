using AutoTestAi.Application.Variables;

namespace AutoTestAi.Api.Endpoints;

public sealed record CreateVariableSetBody(
    string? ScopeType,
    Guid? ScopeId,
    string? Name,
    object? Variables);

public sealed record UpdateVariableSetBody(
    string? Name,
    object? Variables,
    string? RowVersion);

/// <summary>
/// Variable-set management surface (Slice 3A). All routes require
/// authentication; the service layer enforces permissions + project membership.
/// Raw secret values are never accepted — only { value } / { secretRef } entries.
/// </summary>
public static class VariableSetEndpoints
{
    public static IEndpointRouteBuilder MapVariableSetEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/variable-sets", async (
                Guid projectId,
                CreateVariableSetBody? body,
                IVariableSetService service,
                CancellationToken ct) =>
            {
                var created = await service.CreateAsync(projectId,
                    body?.ScopeType ?? "Project",
                    body?.ScopeId,
                    body?.Name ?? string.Empty,
                    ToJson(body?.Variables), ct);
                return Results.Created($"/api/v1/variable-sets/{created.Id}", created);
            })
            .WithName("CreateVariableSet")
            .WithSummary("Create a project/environment/suite variable set (no raw secrets).");

        projects.MapGet("/{projectId:guid}/variable-sets", (
                Guid projectId,
                IVariableSetService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, ct))
            .WithName("ListVariableSets")
            .WithSummary("Variable sets for a project (keys + secret-key flags, never values).");

        var sets = app.MapGroup("/api/v1/variable-sets").RequireAuthorization();

        sets.MapGet("/{id:guid}", (
                Guid id,
                IVariableSetService service,
                CancellationToken ct) =>
            service.GetAsync(id, ct))
            .WithName("GetVariableSet")
            .WithSummary("One variable set (never exposes secret values).");

        sets.MapPut("/{id:guid}", (
                Guid id,
                UpdateVariableSetBody? body,
                IVariableSetService service,
                CancellationToken ct) =>
            service.UpdateAsync(id,
                body?.Name ?? string.Empty,
                ToJson(body?.Variables),
                ParseRowVersion(body?.RowVersion), ct))
            .WithName("UpdateVariableSet")
            .WithSummary("Update a variable set (optimistic concurrency via rowVersion).");

        sets.MapDelete("/{id:guid}", async (
                Guid id,
                IVariableSetService service,
                CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .WithName("DeleteVariableSet")
            .WithSummary("Delete a variable set.");

        return app;
    }

    private static string ToJson(object? variables)
        => variables is null
            ? "{}"
            : System.Text.Json.JsonSerializer.Serialize(variables);

    private static byte[]? ParseRowVersion(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)) return null;
        try { return Convert.FromBase64String(rowVersion.Trim()); }
        catch (FormatException)
        {
            throw new AutoTestAi.Application.Common.ValidationException("Row version is invalid.",
                new[] { new AutoTestAi.Application.Common.FieldError("rowVersion", "Must be base64.") });
        }
    }
}
