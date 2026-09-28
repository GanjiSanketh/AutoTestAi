namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Project-scoped generation input (Slice 4 §2/§18). The caller supplies test
/// intent only — never provider, model, API key, or system prompt; the server
/// determines those from configuration/policy.
/// </summary>
public sealed record GenerateAiTestCommand(
    Guid ProjectId,
    string Title,
    string? Description,
    IReadOnlyList<string> Requirements,
    string? TargetUrl,
    string Framework,
    string Platform,
    string? Module = null,
    string? Priority = null,
    string? AdditionalContext = null);

public sealed record GeneratedTestStepDto(int Order, string Action, string? Target, string? Value);

/// <summary>
/// Normalized generation response (Slice 4 §19). Contains no provider secrets
/// and no raw provider payloads.
/// </summary>
public sealed record GenerateAiTestResult(
    Guid GenerationId,
    Guid TestCaseId,
    string TestKey,
    Guid VersionId,
    int VersionNumber,
    string Status,
    string Title,
    string? Description,
    string Framework,
    string Platform,
    IReadOnlyList<GeneratedTestStepDto> StructuredSteps,
    string SourceCode,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Warnings,
    string Provider,
    string? Model,
    string PromptVersion,
    long LatencyMs,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    string ReviewStatus);
