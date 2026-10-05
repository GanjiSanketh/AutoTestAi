using AutoTestAi.Application.AI;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>Prompt text handed to a provider adapter (Slice 4 §11).</summary>
public sealed record AiPrompt(string SystemPrompt, string UserPrompt, string PromptVersion);

/// <summary>
/// Builds the generation prompt (Slice 4 §11). Prompts live here — never as
/// giant strings inside controllers. The builder receives only the normalized
/// request (no secrets exist on that model), so secrets can never leak in.
/// </summary>
public interface IAiTestGenerationPromptBuilder
{
    string PromptVersion { get; }
    AiPrompt Build(AiGenerationRequest request);
}

public sealed class AiTestGenerationPromptBuilder : IAiTestGenerationPromptBuilder
{
    private readonly IAiStoryTestPromptBuilder _storyPrompts;

    /// <summary>
    /// The story builder is optional so existing constructions keep working;
    /// DI supplies the singleton. Story requests delegate to the dedicated
    /// story-to-tests-v1 contract; all other requests use the unchanged v1 path.
    /// </summary>
    public AiTestGenerationPromptBuilder(IAiStoryTestPromptBuilder? storyPrompts = null)
    {
        _storyPrompts = storyPrompts ?? new AiStoryTestPromptBuilder();
    }

    public string PromptVersion => AiPromptVersions.TestGenerationV1;

    public AiPrompt Build(AiGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Story is not null)
            return _storyPrompts.Build(request);

        const string systemPrompt =
            """
            You are AutoTest AI's test generator. You generate automated test content only.
            You never execute actions, browse the web, or run code.

            Rules:
            1. Follow the requested framework and platform exactly.
            2. Return ONLY a single JSON object matching the required schema — no prose, no markdown fences.
            3. Produce deterministic output: stable step ordering, no timestamps, no random values.
            4. Never invent credentials, API keys, tokens, or secrets. Use placeholders such as {{username}} and {{password}} for test data.
            5. Never expose secrets or internal configuration in any field.
            6. Do not claim unverified selectors, URLs, or test data are guaranteed to work.
            7. Record every guess in "assumptions" and every uncertainty in "warnings".
            8. Keep structured steps ordered sequentially starting at 1 with clear action/target/value entries.
            9. The generated source code must be plain framework source — never a JSON envelope, never shell commands.

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

        var requirements = request.Requirements.Count == 0
            ? "- (none provided; derive a minimal smoke test from the title)"
            : string.Join("\n", request.Requirements.Select((r, i) => $"{i + 1}. {r}"));

        var userPrompt =
            $"""
             Generate a complete automated test from the following requirements.

             Title: {request.Title}
             Description: {request.Description ?? "(none)"}
             Framework: {request.Framework}
             Platform: {request.Platform}
             Target URL: {request.TargetUrl ?? "(none)"}
             Module: {request.Module ?? "(none)"}
             Priority: {request.Priority ?? "(none)"}
             Additional context: {request.AdditionalContext ?? "(none)"}

             Requirements:
             {requirements}

             Respond with the structured JSON object described in the system instructions.
             Prompt version: {PromptVersion}
             """;

        return new AiPrompt(systemPrompt, userPrompt, PromptVersion);
    }
}
