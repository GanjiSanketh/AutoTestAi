namespace AutoTestAi.Application.AI;

/// <summary>
/// Provider-independent AI seam (docs/04 §6, ADR-003).
/// Business modules depend ONLY on this interface — never on Ollama/OpenAI/Gemini SDKs.
/// </summary>
public interface IAiProvider
{
    /// <summary>Provider key, e.g. "stub", "ollama", "openai", "gemini".</summary>
    string Name { get; }

    Task<AiGenerationResult> GenerateTestAsync(
        AiGenerationRequest request,
        CancellationToken cancellationToken);

    Task<AiAnalysisResult> AnalyzeFailureAsync(
        AiFailureAnalysisRequest request,
        CancellationToken cancellationToken);
}
