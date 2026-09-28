using AutoTestAi.Application.AI;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>Phase-1 seam for AI test generation (FR-3.2). Implemented in Phase 1.</summary>
public interface ITestGenerationService
{
    Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken);
}
