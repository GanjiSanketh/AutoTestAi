using AutoTestAi.Application.TestGeneration;

namespace AutoTestAi.Api.Endpoints;

public sealed record StoryTestGenerationFromJiraBody(
    string? IssueKey,
    string? Framework,
    string? Platform,
    string? TargetUrl,
    string? Module,
    string? Priority,
    string? AdditionalContext,
    int? MaxProposals);

/// <summary>
/// Transient Jira story-import surface (Phase 4 Slice 5, docs/06 §7.2).
/// Requires authentication; the orchestrator enforces testcases.manage +
/// project membership. The client supplies only the issue key plus
/// story-generation overrides — never a Jira URL, project key,
/// integration id, or credentials. Generation only — proposals are NOT
/// persisted here; saving reuses TestCase creation. Responses carry safe
/// proposal metadata only — never provider keys, prompts, or raw payloads.
/// </summary>
public static class JiraStoryImportEndpoints
{
    public static IEndpointRouteBuilder MapJiraStoryImportEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/story-test-generation-from-jira", (
                Guid projectId,
                StoryTestGenerationFromJiraBody? body,
                IJiraStoryImportService importer,
                CancellationToken ct) =>
            importer.ImportAndGenerateAsync(new JiraStoryImportCommand(
                projectId,
                body?.IssueKey ?? string.Empty,
                body?.Framework ?? string.Empty,
                body?.Platform ?? string.Empty,
                body?.TargetUrl,
                body?.Module,
                body?.Priority,
                body?.AdditionalContext,
                body?.MaxProposals ?? StoryTestGenerationService.DefaultProposals), ct))
            .WithName("GenerateStoryTestsFromJira")
            .WithSummary("Generate up to 10 test proposals from a Jira issue; nothing is persisted.");

        return app;
    }
}
