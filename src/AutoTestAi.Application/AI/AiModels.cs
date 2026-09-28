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

public sealed record AiGenerationRequest(
    string Title,
    string? Description,
    string? TargetUrl,
    string Framework,
    string Platform,
    IReadOnlyList<string> Requirements);

public sealed record AiGeneratedStep(string Order, string Action, string Expected);

public sealed record AiGenerationResult(
    string Provider,
    string? Model,
    long LatencyMs,
    IReadOnlyList<AiGeneratedStep> Steps,
    string SourceCode);

public sealed record AiFailureAnalysisRequest(
    Guid ExecutionTestId,
    string? ErrorType,
    string? ErrorMessage,
    string? TestTitle);

public sealed record AiAnalysisResult(
    string Provider,
    string? Model,
    string Classification,
    string RootCause,
    decimal Confidence);
