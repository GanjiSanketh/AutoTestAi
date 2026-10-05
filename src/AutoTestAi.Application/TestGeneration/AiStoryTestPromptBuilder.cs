using AutoTestAi.Application.AI;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Dedicated user-story prompt contract (Phase 4 Slice 3).
/// Implemented by <see cref="AiStoryTestPromptBuilder"/>; the generic
/// test-generation-v1 builder delegates here only when the request carries
/// story context, so generic behavior is unchanged.
/// </summary>
public interface IAiStoryTestPromptBuilder
{
    string PromptVersion { get; }
    AiPrompt Build(AiGenerationRequest request);
}

/// <summary>
/// Builds the story-to-tests-v1 prompt (Phase 4 Slice 3). One test per
/// invocation, focused on a single acceptance criterion within the full
/// story context. Receives only the normalized request (no secrets exist
/// on that model), so secrets can never leak in.
/// </summary>
public sealed class AiStoryTestPromptBuilder : IAiStoryTestPromptBuilder
{
    public string PromptVersion => AiPromptVersions.StoryToTestsV1;

    public AiPrompt Build(AiGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var story = request.Story
            ?? throw new ArgumentException("Story context is required for the story prompt.", nameof(request));

        const string systemPrompt =
            """
            You are AutoTest AI's user-story test generator. You generate automated test content only.
            You never execute actions, browse the web, or run code.

            Rules:
            1. Analyze the supplied user story and its acceptance criteria.
            2. Produce exactly ONE test case per invocation, focused on the given focus criterion within the full story context.
            3. Prefer criteria not already covered in this batch; never duplicate an already-covered criterion.
            4. Follow the requested framework and platform exactly.
            5. Return ONLY a single JSON object matching the required schema — no prose, no markdown fences.
            6. Produce deterministic output: stable step ordering, no timestamps, no random values.
            7. Use common web automation actions (navigate, click, fill, type, select, check, uncheck, press, wait, assertVisible, assertText, assertValue, screenshot).
            8. Never invent credentials, API keys, tokens, or secrets. Use placeholders such as {{username}} and {{password}} for test data.
            9. Never expose secrets or internal configuration in any field.
            10. Do not claim unverified selectors, URLs, or test data are guaranteed to work.
            11. Record every guess in "assumptions" and every uncertainty in "warnings".
            12. Keep structured steps ordered sequentially starting at 1 with clear action/target/value entries.
            13. The generated source code must be plain framework source — never a JSON envelope, never shell commands.
            14. Never create Jira or ticketing content.
            15. Never mutate, approve, or reference existing tests; this is a new proposal only.
            16. Never claim execution evidence and never claim that a test passed or was executed.

            Required JSON schema:
            {
              "title": "string (required)",
              "description": "string",
              "framework": "string (required, must match the requested framework)",
              "platform": "string (required, must match the requested platform)",
              "structuredSteps": [{"order": 1, "action": "navigate", "target": "...", "value": "..."}],
              "sourceCode": "string (required, framework source code)",
              "assumptions": ["string"],
              "warnings": ["string"]
            }
            """;

        var criteria = story.AcceptanceCriteria.Count == 0
            ? "- (none provided)"
            : string.Join("\n", story.AcceptanceCriteria.Select((c, i) => $"{i + 1}. {c}"));
        var covered = story.CoveredCriterionIndexes.Count == 0
            ? "(none yet)"
            : string.Join(", ", story.CoveredCriterionIndexes.OrderBy(i => i).Select(i => $"#{i + 1}"));

        var userPrompt =
            $"""
             Generate one automated test from the following user story, focused on the focus criterion.

             Story title: {story.StoryTitle}
             Story description: {story.StoryDescription ?? "(none)"}
             Framework: {request.Framework}
             Platform: {request.Platform}
             Target URL: {request.TargetUrl ?? "(none)"}
             Module: {request.Module ?? "(none)"}
             Priority: {request.Priority ?? "(none)"}
             Additional context: {request.AdditionalContext ?? "(none)"}

             Acceptance criteria:
             {criteria}

             Focus criterion #{story.FocusCriterionIndex + 1}: {story.FocusCriterion}
             Already covered in this batch: {covered}

             Respond with the structured JSON object described in the system instructions.
             Prompt version: {PromptVersion}
             """;

        return new AiPrompt(systemPrompt, userPrompt, PromptVersion);
    }
}
