using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.AI;

/// <summary>
/// Deterministic development stub. Validates the IAiProvider architecture
/// without calling any real vendor (Phase 0).
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
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Title is required.", nameof(request));

        var started = _clock.UtcNow;
        var steps = request.Requirements.Count == 0
            ? new List<AiGeneratedStep>
            {
                new("1", $"Navigate to {request.TargetUrl ?? "(no target URL)"}", "Page loads successfully")
            }
            : request.Requirements
                .Select((r, i) => new AiGeneratedStep((i + 1).ToString(), r, "Verified"))
                .ToList();

        var sourceCode =
            """
            import { test, expect } from '@playwright/test';

            // NOTE: Phase-0 stub output. Real AI-generated code arrives in Phase 1
            // after human review (FR-3.2). Never execute unreviewed generated code.
            test('TITLE_PLACEHOLDER', async ({ page }) => {
              // TODO: implement generated steps
            });
            """.Replace("TITLE_PLACEHOLDER", request.Title.Replace("'", "\\'", StringComparison.Ordinal), StringComparison.Ordinal);

        var latencyMs = (long)(_clock.UtcNow - started).TotalMilliseconds;
        return Task.FromResult(new AiGenerationResult(
            Provider: Name,
            Model: "stub-1.0",
            LatencyMs: latencyMs,
            Steps: steps,
            SourceCode: sourceCode));
    }

    public Task<AiAnalysisResult> AnalyzeFailureAsync(
        AiFailureAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new AiAnalysisResult(
            Provider: Name,
            Model: "stub-1.0",
            Classification: "Unknown",
            RootCause: "Phase-0 stub: no real analysis performed.",
            Confidence: 0m));
    }
}
