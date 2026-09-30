using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.AI;

/// <summary>
/// Deterministic development stub. Validates the IAiProvider architecture
/// without calling any real vendor (Phase 0). Populates the Slice-4 structured
/// contract so gateway/orchestrator tests run without live providers.
/// </summary>
public sealed class StubAiProvider : IAiProvider
{
    private readonly IDateTimeProvider _clock;

    public StubAiProvider(IDateTimeProvider clock) => _clock = clock;

    public string Name => "stub";

    public Task<AiGenerationResult> GenerateTestAsync(
        AiGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Title is required.", nameof(request));

        var started = _clock.UtcNow;
        var title = request.Title.Trim();
        var framework = string.IsNullOrWhiteSpace(request.Framework) ? "playwright" : request.Framework.Trim();
        var platform = string.IsNullOrWhiteSpace(request.Platform) ? "web" : request.Platform.Trim();

        List<AiStructuredStep> structured;
        List<AiGeneratedStep> legacy;
        if (request.Requirements.Count == 0)
        {
            structured = new List<AiStructuredStep>
            {
                new(1, "navigate", request.TargetUrl, "Page loads successfully"),
            };
            legacy = new List<AiGeneratedStep>
            {
                new("1", $"Navigate to {request.TargetUrl ?? "(no target URL)"}", "Page loads successfully"),
            };
        }
        else
        {
            structured = request.Requirements
                .Select((r, i) => new AiStructuredStep(i + 1, r.Trim(), Target: null, Value: "Verified"))
                .ToList();
            legacy = request.Requirements
                .Select((r, i) => new AiGeneratedStep((i + 1).ToString(), r.Trim(), "Verified"))
                .ToList();
        }

        var sourceCode =
            """
            import { test, expect } from '@playwright/test';

            // NOTE: Stub output. Real AI-generated code requires human review (FR-3.2).
            // Never execute unreviewed generated code.
            test('TITLE_PLACEHOLDER', async ({ page }) => {
              // TODO: implement generated steps
            });
            """.Replace("TITLE_PLACEHOLDER", title.Replace("'", "\\'", StringComparison.Ordinal), StringComparison.Ordinal);

        var latencyMs = (long)(_clock.UtcNow - started).TotalMilliseconds;
        return Task.FromResult(new AiGenerationResult(
            Provider: Name,
            Model: "stub-1.0",
            LatencyMs: latencyMs,
            Steps: legacy,
            SourceCode: sourceCode,
            Title: title,
            Description: request.Description,
            Framework: framework,
            Platform: platform,
            StructuredSteps: structured,
            Assumptions: ["Stub output assumes the target page and test data exist."],
            Warnings: ["Selectors were not verified against a live application."],
            PromptVersion: AiPromptVersions.TestGenerationV1));
    }

    public Task<AiHealingResult> SuggestHealingCandidatesAsync(
        AiHealingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Deterministic stub: never fabricates locators. Echoes at most one
        // text candidate when the evidence names visible text, otherwise
        // returns no candidates so callers exercise the safe-failure path.
        // Never reports confidence: 0 (unknown) unless evidence is present.
        var texts = request.Evidence.NearbyText
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Take(1)
            .ToList();
        var candidates = new List<AiHealingCandidate>();
        if (texts.Count > 0 && request.AllowedStrategies.Contains("text"))
            candidates.Add(new AiHealingCandidate("text", texts[0].Trim(), "Stub echo of observed text.", 0m));
        return Task.FromResult(new AiHealingResult(
            Provider: Name,
            Model: "stub-1.0",
            Candidates: candidates,
            PromptVersion: AiPromptVersions.SelfHealingV1));
    }

    public Task<AiAnalysisResult> AnalyzeFailureAsync(
        AiFailureAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Deterministic stub: mirrors the execution classification when bounded
        // evidence is supplied, otherwise the legacy Unknown placeholder.
        // Never fabricates confidence: 0 unless evidence is present.
        var context = request.Context;
        if (context is null)
            return Task.FromResult(new AiAnalysisResult(
                Provider: Name,
                Model: "stub-1.0",
                Classification: "Unknown",
                RootCause: "Phase-0 stub: no real analysis performed.",
                Confidence: 0m));

        var classification = context.ExecutionClassification switch
        {
            "ApplicationDefect" => "ApplicationDefect",
            "EnvironmentFailure" => "EnvironmentFailure",
            "AutomationFailure" => "AutomationFailure",
            "TestFailure" => "TestFailure",
            _ => "Unknown",
        };
        var summary = string.IsNullOrWhiteSpace(context.FailedStepSummary)
            ? $"Stub analysis of {context.TestKey} failure."
            : $"Stub analysis: {context.FailedStepSummary}.";
        return Task.FromResult(new AiAnalysisResult(
            Provider: Name,
            Model: "stub-1.0",
            Classification: classification,
            RootCause: $"Stub heuristic: deterministic classification is {context.ExecutionClassification}.",
            Confidence: 0.5m,
            Summary: summary,
            Assumptions: ["Stub output assumes the supplied evidence is complete."],
            Warnings: ["Configure ollama or openai for real analysis."],
            RecommendedAction: "Review the failed step and logs, then decide whether to file a defect.",
            IsLikelyDefect: string.Equals(classification, "ApplicationDefect", StringComparison.Ordinal),
            PromptVersion: AiPromptVersions.FailureAnalysisV1));
    }
}
