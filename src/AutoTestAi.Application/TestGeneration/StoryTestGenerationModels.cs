using System.Text.Json;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Manual user-story to test proposals input (Phase 4 Slice 3).
/// Proposals are returned for user selection and are NOT persisted here;
/// saving reuses the existing TestCase creation path. No story entity,
/// no Jira import, no suite attachment.
/// </summary>
public sealed record GenerateStoryTestsCommand(
    Guid ProjectId,
    string StoryTitle,
    string? StoryDescription,
    IReadOnlyList<string> AcceptanceCriteria,
    string? TargetUrl,
    string Framework,
    string Platform,
    string? Module = null,
    string? Priority = null,
    string? AdditionalContext = null,
    int MaxProposals = 10);

/// <summary>
/// One story-generated test proposal (Phase 4 Slice 3). Successful proposals
/// carry preview content plus opaque redacted provenance for the save echo;
/// failed proposals carry only identity, focus, and a user-safe error.
/// Proposal IDs are request-scoped opaque identifiers (never database IDs).
/// </summary>
public sealed record StoryTestProposal(
    string ProposalId,
    int Index,
    string Status,
    string? Title,
    string? Description,
    string? Framework,
    string? Platform,
    string? Module,
    string? Priority,
    string? FocusCriterion,
    int FocusCriterionIndex,
    IReadOnlyList<GeneratedTestStepDto> StructuredSteps,
    string? SourceCode,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Warnings,
    string? Provider,
    string? Model,
    string PromptVersion,
    long LatencyMs,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    JsonElement? Provenance,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Batch story-generation response (Phase 4 Slice 3). Per-proposal results
/// preserve partial success: failed proposals never discard successful ones.
/// </summary>
public sealed record StoryTestGenerationResult(
    Guid GenerationId,
    string PromptVersion,
    int ProposalCount,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<StoryTestProposal> Proposals);
