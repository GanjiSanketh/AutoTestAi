namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Safe Jira provenance for story import (Phase 4 Slice 5 §§14–15).
/// Host only — never the full base URL, path, token, or email.
/// </summary>
public sealed record JiraImportMetadata(
    string IssueKey,
    string IssueType,
    string BaseUrlHost,
    string FetchedAt,
    string? NormalizerVersion = null);

/// <summary>
/// Jira story-import input (Phase 4 Slice 5 §17). The client supplies only
/// the issue key plus existing story-generation override fields — never a
/// Jira URL, project key, integration id, or credentials.
/// </summary>
public sealed record JiraStoryImportCommand(
    Guid ProjectId,
    string IssueKey,
    string Framework,
    string Platform,
    string? TargetUrl = null,
    string? Module = null,
    string? Priority = null,
    string? AdditionalContext = null,
    int MaxProposals = 10);

/// <summary>
/// Transient Jira story-import boundary (Phase 4 Slice 5). Generation only —
/// proposals are NOT persisted here; saving reuses the existing TestCase
/// creation path. No Story entity, no JQL, no bulk import.
/// </summary>
public interface IJiraStoryImportService
{
    Task<StoryTestGenerationResult> ImportAndGenerateAsync(
        JiraStoryImportCommand command, CancellationToken cancellationToken);
}
