using AutoTestAi.Application.AI;

namespace AutoTestAi.Application.FailureAnalysis;

/// <summary>Prompt handed to a provider adapter for failure analysis (Slice 6 §11).</summary>
public sealed record AiFailurePrompt(string SystemPrompt, string UserPrompt, string PromptVersion);

/// <summary>
/// Builds the failure-analysis prompt. Prompts live here — never in
/// controllers or services. Receives only bounded redacted evidence, so raw
/// credentials can never reach a provider through this path.
/// </summary>
public interface IAiFailureAnalysisPromptBuilder
{
    string PromptVersion { get; }
    AiFailurePrompt Build(AiFailureAnalysisRequest request);
}

public sealed class AiFailureAnalysisPromptBuilder : IAiFailureAnalysisPromptBuilder
{
    public string PromptVersion => AiPromptVersions.FailureAnalysisV1;

    public AiFailurePrompt Build(AiFailureAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;

        const string systemPrompt =
            """
            You are AutoTest AI's failure analyst. You explain automated test failures only.
            You never execute code, modify tests, create tickets, or change configuration.

            Rules:
            1. Analyze ONLY the supplied evidence. Never invent facts, logs, selectors, or URLs.
            2. Distinguish evidence (what the logs/steps show) from inference (what you conclude).
            3. The deterministic execution classification is authoritative evidence; you may SUGGEST
               a different classification, but state explicitly when you disagree and why.
            4. Do not claim certainty without evidence. Record every guess in "assumptions" and
               every gap in "warnings".
            5. Return ONLY a single JSON object matching the required schema — no prose, no markdown fences.
            6. Never include secrets, credentials, tokens, or cookies in any field.
            7. Never generate executable code, shell commands, or test modifications.
            8. Keep the summary short and factual. The recommended action must be an investigation
               step a human can perform (inspect selector, check application logs, verify test data,
               retry the environment, or create a defect) — never an automated fix.
            9. Your analysis is advisory only: a human reviews it and decides whether to file a defect.

            Required JSON schema:
            {
              "classification": "one of: TestFailure, ApplicationDefect, EnvironmentFailure, AutomationFailure, Unknown (required)",
              "summary": "string, at most 500 characters (required)",
              "probableCause": "string, at most 2000 characters (required)",
              "confidence": "number 0..1 (required)",
              "evidence": ["short quoted facts from the supplied evidence"],
              "assumptions": ["string"],
              "warnings": ["string"],
              "recommendedAction": "string, at most 500 characters",
              "isLikelyDefect": "boolean"
            }
            """;

        var userPrompt = context is null
            ? BuildBarePrompt(request)
            : BuildEvidencePrompt(context, request);

        return new AiFailurePrompt(systemPrompt, userPrompt, PromptVersion);
    }

    private string BuildBarePrompt(AiFailureAnalysisRequest request)
        => "Analyze the following test failure.\n\n"
            + "Error type: " + (request.ErrorType ?? "(unknown)") + "\n"
            + "Error message: " + Truncate(request.ErrorMessage, 2000) + "\n"
            + "Test: " + (request.TestTitle ?? "(unknown)") + "\n\n"
            + "Respond with the structured JSON object described in the system instructions.\n"
            + "Prompt version: " + PromptVersion;

    private string BuildEvidencePrompt(AiFailureAnalysisContext context, AiFailureAnalysisRequest request)
    {
        var steps = context.FailedSteps.Count == 0
            ? "(none recorded)"
            : string.Join("\n", context.FailedSteps.Select(FormatStep));
        var logs = context.Logs.Count == 0
            ? "(no logs captured)"
            : string.Join("\n", context.Logs.Select(l => "[" + l.Level + "] " + l.Message));
        var artifacts = context.ArtifactCount == 0
            ? "none referenced"
            : context.ArtifactCount + " referenced (" + string.Join(", ", context.ArtifactNames) + ")";
        var completeness = context.Truncated
            ? "NOTE: some evidence was truncated to fit limits; say so in warnings if it matters."
            : "Evidence is complete within the configured bounds.";
        return "Analyze the following test failure. Evidence was truncated to fit provider limits where marked.\n\n"
            + "Test: " + context.TestKey + " — " + context.TestTitle + "\n"
            + "Framework: " + context.Framework + " | Platform: " + context.Platform + " | Browser: " + context.Browser + "\n"
            + "Deterministic execution classification: " + context.ExecutionClassification + " (authoritative)\n"
            + "Attempt: " + context.Attempt + "\n"
            + "Failed step: " + (context.FailedStepSummary ?? "(none recorded)") + "\n"
            + "Error type: " + (request.ErrorType ?? "(unknown)") + "\n"
            + "Error message: " + Truncate(request.ErrorMessage, 2000) + "\n\n"
            + "Failed steps (at most evidence bound):\n" + steps + "\n\n"
            + "Logs (bounded, most relevant last):\n" + logs + "\n\n"
            + "Artifacts: " + artifacts + "\n"
            + completeness + "\n\n"
            + "Respond with the structured JSON object described in the system instructions.\n"
            + "Prompt version: " + PromptVersion;
    }

    private static string FormatStep(AiFailureEvidenceStep step)
    {
        var text = "- order " + step.Order + ": " + step.Action + " '" + step.Target + "' [" + step.Status + "]";
        return string.IsNullOrWhiteSpace(step.ErrorMessage) ? text : text + " — " + step.ErrorMessage;
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? "(none)"
            : value.Length <= max ? value : value[..max] + " [truncated]";
}
