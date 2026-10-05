using AutoTestAi.Application.AI;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Manual user-story to test proposals boundary (Phase 4 Slice 3).
/// Generation only — proposals are NOT persisted here; saving reuses the
/// existing TestCase creation path. Sequential single-test calls through
/// the existing provider abstraction; per-proposal results preserve
/// partial success.
/// </summary>
public interface IAiStoryTestGenerator
{
    Task<StoryTestGenerationResult> GenerateStoryProposalsAsync(
        GenerateStoryTestsCommand command, CancellationToken cancellationToken);
}
