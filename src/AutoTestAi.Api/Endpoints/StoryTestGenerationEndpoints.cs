using AutoTestAi.Application.TestGeneration;

namespace AutoTestAi.Api.Endpoints;

public sealed record StoryTestGenerationBody(
    string? StoryTitle,
    string? StoryDescription,
    IReadOnlyList<string>? AcceptanceCriteria,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string? Module,
    string? Priority,
    string? AdditionalContext,
    int? MaxProposals);

/// <summary>
/// Manual user-story to test proposals surface (Phase 4 Slice 3, docs/06).
/// Requires authentication; the orchestrator enforces testcases.manage +
/// project membership. Generation only — proposals are NOT persisted here;
/// saving reuses TestCase creation. Responses carry safe proposal metadata
/// only — never provider keys, prompts, or raw payloads.
/// </summary>
public static class StoryTestGenerationEndpoints
{
    public static IEndpointRouteBuilder MapStoryTestGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/story-test-generation", (
                Guid projectId,
                StoryTestGenerationBody? body,
                IAiStoryTestGenerator generator,
                CancellationToken ct) =>
            generator.GenerateStoryProposalsAsync(new GenerateStoryTestsCommand(
                projectId,
                body?.StoryTitle ?? string.Empty,
                body?.StoryDescription,
                body?.AcceptanceCriteria ?? Array.Empty<string>(),
                body?.TargetUrl,
                body?.Framework ?? string.Empty,
                body?.Platform ?? string.Empty,
                body?.Module,
                body?.Priority,
                body?.AdditionalContext,
                body?.MaxProposals ?? StoryTestGenerationService.DefaultProposals), ct))
            .WithName("GenerateStoryTests")
            .WithSummary("Generate up to 10 test proposals from a manual user story; nothing is persisted.");

        return app;
    }
}
