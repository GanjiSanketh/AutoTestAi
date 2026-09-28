using AutoTestAi.Application.TestGeneration;

namespace AutoTestAi.Api.Endpoints;

public sealed record GenerateTestBody(
    string? Title,
    string? Description,
    IReadOnlyList<string>? Requirements,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string? Module,
    string? Priority,
    string? AdditionalContext);

/// <summary>
/// AI test generation surface (docs/06 §7). Requires authentication; the
/// orchestrator enforces testcases.manage + project membership. Responses carry
/// safe generation metadata only — never provider keys, prompts, or raw payloads.
/// Generated code is stored for human review and never executed.
/// </summary>
public static class TestGenerationEndpoints
{
    public static IEndpointRouteBuilder MapTestGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/test-generation", (
                Guid projectId,
                GenerateTestBody? body,
                IAiTestGenerator generator,
                CancellationToken ct) =>
            generator.GenerateAsync(new GenerateAiTestCommand(
                projectId,
                body?.Title ?? string.Empty,
                body?.Description,
                body?.Requirements ?? Array.Empty<string>(),
                body?.TargetUrl,
                body?.Framework ?? string.Empty,
                body?.Platform ?? string.Empty,
                body?.Module,
                body?.Priority,
                body?.AdditionalContext), ct))
            .WithName("GenerateTest")
            .WithSummary("Generate a test from requirements via the configured AI provider; saves a Pending AI version.");

        projects.MapGet("/{projectId:guid}/ai-provider-status", async (
                Guid projectId,
                AutoTestAi.Application.Authorization.IAuthorizationService authorization,
                IAiTestGenerator generator,
                CancellationToken ct) =>
            {
                await authorization.RequireProjectAccessAsync(
                    projectId,
                    AutoTestAi.Application.Authorization.Permissions.TestCasesRead,
                    ct);
                return Results.Ok(generator.GetProviderStatus());
            })
            .WithName("GetAiProviderStatus")
            .WithSummary("Safe AI provider metadata (provider/model/prompt version; no secrets).");

        return app;
    }
}
