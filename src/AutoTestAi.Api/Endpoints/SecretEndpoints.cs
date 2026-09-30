using AutoTestAi.Application.Secrets;

namespace AutoTestAi.Api.Endpoints;

public sealed record CreateSecretBody(
    Guid? EnvironmentId,
    string? Name,
    string? Value,
    string? Description);

public sealed record UpdateSecretBody(
    string? Name,
    string? Value,
    string? Description,
    string? RowVersion);

/// <summary>
/// Secret metadata surface (Slice 3A §9). Values flow in on create/replace
/// only and are never returned by any endpoint.
/// </summary>
public static class SecretEndpoints
{
    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/secrets", async (
                Guid projectId,
                CreateSecretBody? body,
                ISecretMetadataService service,
                CancellationToken ct) =>
            {
                if (body?.EnvironmentId is null || body.EnvironmentId == Guid.Empty)
                    throw new AutoTestAi.Application.Common.ValidationException("Environment is required.",
                        new[] { new AutoTestAi.Application.Common.FieldError("environmentId", "Environment id is required.") });
                var created = await service.CreateAsync(projectId,
                    body.EnvironmentId.Value,
                    body?.Name ?? string.Empty,
                    body?.Value ?? string.Empty,
                    body?.Description, ct);
                return Results.Created($"/api/v1/secrets/{created.Id}", created);
            })
            .WithName("CreateSecret")
            .WithSummary("Store a secret value (metadata returned; value never returned).");

        projects.MapGet("/{projectId:guid}/secrets", (
                Guid projectId,
                Guid? environmentId,
                ISecretMetadataService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, environmentId, ct))
            .WithName("ListSecrets")
            .WithSummary("Secret metadata for a project (never values).");

        var secrets = app.MapGroup("/api/v1/secrets").RequireAuthorization();

        secrets.MapGet("/{id:guid}/exists", (
                Guid id,
                ISecretMetadataService service,
                CancellationToken ct) =>
            service.ExistsAsync(id, ct))
            .WithName("SecretExists")
            .WithSummary("Whether a secret has a stored value (boolean only).");

        secrets.MapPut("/{id:guid}", (
                Guid id,
                UpdateSecretBody? body,
                ISecretMetadataService service,
                CancellationToken ct) =>
            service.UpdateAsync(id,
                body?.Name,
                body?.Value,
                body?.Description,
                ParseRowVersion(body?.RowVersion), ct))
            .WithName("UpdateSecret")
            .WithSummary("Rename, replace value, or update description (metadata returned).");

        secrets.MapDelete("/{id:guid}", async (
                Guid id,
                ISecretMetadataService service,
                CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .WithName("DeleteSecret")
            .WithSummary("Delete a secret and its stored value.");

        return app;
    }

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
