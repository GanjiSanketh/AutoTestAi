using AutoTestAi.Application.AI;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// AI gateway/orchestrator boundary (Slice 4 §5, ADR-003). Controllers depend on
/// this — never on <see cref="IAiProvider"/> directly.
/// Flow: validate → authorize → resolve provider → generate → validate output →
/// redact → persist via TestCaseService → audit → normalized result.
/// </summary>
public interface IAiTestGenerator
{
    Task<GenerateAiTestResult> GenerateAsync(
        GenerateAiTestCommand command, CancellationToken cancellationToken);

    AiProviderStatus GetProviderStatus();
}
