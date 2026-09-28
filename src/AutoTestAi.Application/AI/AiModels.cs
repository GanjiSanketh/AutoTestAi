namespace AutoTestAi.Application.AI;

/// <summary>
/// Which provider served a request. The application never branches on vendor SDKs —
/// it only records where a result came from (ADR-003).
/// </summary>
public enum AiProviderType
{
    Stub,
    Ollama,
    OpenAI,
    Gemini
}

/// <summary>Prompt versions for traceability (Slices 4/6). Never change prompt
/// behavior without bumping the version; the version is stored in metadata.</summary>
public static class AiPromptVersions
{
    public const string TestGenerationV1 = "test-generation-v1";
    public const string FailureAnalysisV1 = "failure-analysis-v1";
}

/// <summary>
/// Provider input for test generation. The server builds this — callers never
/// supply provider, model, API key or system prompt (Slice 4 §2).
/// Optional trailing parameters were added in Slice 4; existing 6-argument
/// construction keeps working.
/// </summary>
public sealed record AiGenerationRequest(
    string Title,
    string? Description,
    string? TargetUrl,
    string Framework,
    string Platform,
    IReadOnlyList<string> Requirements,
    string? Module = null,
    string? Priority = null,
    string? AdditionalContext = null);

/// <summary>
/// Legacy step shape kept for backward compatibility with Phase-0 consumers.
/// New code prefers <see cref="AiStructuredStep"/>, which mirrors
/// <c>Domain.TestCases.TestStep</c> (order/action/target/value).
/// </summary>
public sealed record AiGeneratedStep(string Order, string Action, string Expected);

/// <summary>
/// Canonical structured step returned by providers. Mirrors the persisted
/// TestStep representation (order/action/target/value) — no competing model.
/// </summary>
public sealed record AiStructuredStep(int Order, string Action, string? Target, string? Value);

/// <summary>
/// Normalized structured provider output (Slice 4 §3). Providers return data,
/// never raw prose; the orchestrator validates before persisting. Optional
/// trailing parameters were added in Slice 4; existing 5-argument construction
/// keeps working.
/// </summary>
public sealed record AiGenerationResult(
    string Provider,
    string? Model,
    long LatencyMs,
    IReadOnlyList<AiGeneratedStep> Steps,
    string SourceCode,
    string? Title = null,
    string? Description = null,
    string? Framework = null,
    string? Platform = null,
    IReadOnlyList<AiStructuredStep>? StructuredSteps = null,
    IReadOnlyList<string>? Assumptions = null,
    IReadOnlyList<string>? Warnings = null,
    string? PromptVersion = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null)
{
    /// <summary>Effective structured steps: canonical form when present,
    /// otherwise mapped from the legacy step shape.</summary>
    public IReadOnlyList<AiStructuredStep> EffectiveStructuredSteps()
    {
        if (StructuredSteps is { Count: > 0 })
            return StructuredSteps;
        var mapped = new List<AiStructuredStep>();
        foreach (var step in Steps)
        {
            if (!int.TryParse(step.Order, out var order))
                order = mapped.Count + 1;
            mapped.Add(new AiStructuredStep(order, step.Action, Target: null, Value: step.Expected));
        }
        return mapped;
    }
}

public sealed record AiFailureAnalysisRequest(
    Guid ExecutionTestId,
    string? ErrorType,
    string? ErrorMessage,
    string? TestTitle,
    AiFailureAnalysisContext? Context = null);

/// <summary>
/// Bounded redacted failure evidence handed to a provider (Slice 6 §5).
/// No raw credentials, no full histories, no artifact bytes — metadata only.
/// </summary>
public sealed record AiFailureEvidenceStep(
    int Order,
    string Action,
    string? Target,
    string Status,
    string? ErrorMessage);

public sealed record AiFailureEvidenceLog(
    string Level,
    string Message);

/// <summary>
/// Structured execution evidence for failure analysis. Produced by the
/// evidence service (bounded + redacted); the provider must analyze ONLY this.
/// </summary>
public sealed record AiFailureAnalysisContext(
    Guid ExecutionId,
    Guid ExecutionTestId,
    string TestKey,
    string TestTitle,
    string Framework,
    string Platform,
    string Browser,
    string ExecutionClassification,
    string? FailedStepSummary,
    IReadOnlyList<AiFailureEvidenceStep> FailedSteps,
    IReadOnlyList<AiFailureEvidenceLog> Logs,
    int ArtifactCount,
    IReadOnlyList<string> ArtifactNames,
    int Attempt,
    bool Truncated);

public sealed record AiAnalysisResult(
    string Provider,
    string? Model,
    string Classification,
    string RootCause,
    decimal Confidence,
    string? Summary = null,
    IReadOnlyList<string>? Evidence = null,
    IReadOnlyList<string>? Assumptions = null,
    IReadOnlyList<string>? Warnings = null,
    string? RecommendedAction = null,
    bool IsLikelyDefect = false,
    string? PromptVersion = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    long LatencyMs = 0);
