using System.Text.Json;
using AutoTestAi.Application.AI;

namespace AutoTestAi.Application.FailureAnalysis;

/// <summary>Normalized advisory analysis (Slice 6). Advisory only: never
/// mutates execution history and never creates defects by itself.</summary>
public sealed record FailureAnalysisDto(
    Guid Id,
    Guid ExecutionId,
    Guid ExecutionTestId,
    int Attempt,
    string Status,
    string Classification,
    string? Summary,
    string? ProbableCause,
    decimal? Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Warnings,
    string? RecommendedAction,
    bool IsLikelyDefect,
    string? Provider,
    string? Model,
    string? PromptVersion,
    long? LatencyMs,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    DateTimeOffset CreatedAt);

/// <summary>
/// AI-assisted failure analysis over bounded redacted evidence (Slice 6).
/// Synchronous MVP: the HTTP request spans the provider call (bounded by the
/// analysis timeout); retries create new attempts without rerunning the test.
/// </summary>
public interface IFailureAnalysisService
{
    Task<FailureAnalysisDto> AnalyzeAsync(Guid executionId, CancellationToken cancellationToken);

    Task<FailureAnalysisDto> GetLatestAsync(Guid executionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FailureAnalysisDto>> ListAttemptsAsync(
        Guid executionId, CancellationToken cancellationToken);
}
