using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.SelfHealing;

/// <summary>
/// Project-scoped self-healing policy DTOs (Phase 2 Slice 11).
/// No credentials, DOM, or secrets are ever present in these shapes.
/// </summary>
public sealed record SelfHealingPolicyDto(
    Guid ProjectId,
    bool Enabled,
    bool AiFallbackEnabled,
    int MaxAttemptsPerStep,
    int? MinDeterministicScore,
    decimal? MinAiConfidence,
    IReadOnlyList<string> AllowedStrategies,
    DateTimeOffset UpdatedAt);

public sealed record UpsertSelfHealingPolicyCommand(
    Guid ProjectId,
    bool Enabled,
    bool AiFallbackEnabled,
    int? MinDeterministicScore,
    decimal? MinAiConfidence,
    IReadOnlyList<string>? AllowedStrategies);

public sealed record SelfHealingStatusDto(
    Guid ProjectId,
    bool Enabled,
    bool Configured,
    bool AiFallbackEnabled,
    int AttemptCount,
    int AppliedCount,
    DateTimeOffset? LastHealedAt);

public sealed record SelfHealingAttemptDto(
    Guid Id,
    Guid ExecutionId,
    int StepOrder,
    string StepAction,
    string? OriginalStrategy,
    string? OriginalValue,
    string? RecoveredStrategy,
    string? RecoveredValue,
    string HealingStrategy,
    string Status,
    int CandidateCount,
    bool WasApplied,
    bool IsAiAssisted,
    DateTimeOffset CreatedAt);

/// <summary>Policy configuration seam (no authorization here; the service enforces it).</summary>
public interface ISelfHealingPolicyStore
{
    Task<SelfHealingPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct);

    Task AddAsync(SelfHealingPolicy policy, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Attempt persistence seam (no authorization here; the service enforces it).</summary>
public interface ISelfHealingAttemptStore
{
    Task<IReadOnlyList<SelfHealingAttempt>> ListByTestAsync(Guid executionTestId, CancellationToken ct);

    Task<IReadOnlyList<SelfHealingAttempt>> ListByExecutionAsync(Guid executionId, CancellationToken ct);

    Task<SelfHealingAttempt?> FindByTestAndStepAsync(
        Guid executionTestId, int stepOrder, CancellationToken ct);

    Task<HealingAttemptCounts> CountByProjectAsync(Guid projectId, CancellationToken ct);

    Task AddAsync(SelfHealingAttempt attempt, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record HealingAttemptCounts(int AttemptCount, int AppliedCount, DateTimeOffset? LastHealedAt);

/// <summary>User-facing policy administration (project permissions enforced).</summary>
public interface ISelfHealingPolicyService
{
    Task<SelfHealingPolicyDto?> GetAsync(Guid projectId, CancellationToken ct);

    Task<SelfHealingPolicyDto> UpsertAsync(UpsertSelfHealingPolicyCommand command, CancellationToken ct);

    Task<SelfHealingStatusDto> GetStatusAsync(Guid projectId, CancellationToken ct);

    Task<IReadOnlyList<SelfHealingAttemptDto>> ListAttemptsAsync(
        Guid projectId, Guid executionId, CancellationToken ct);
}

/// <summary>
/// System-driven healing seam. Suggest/Record never require a human
/// permission; project scope is validated against execution/assignment
/// records (lease fencing), exactly like Slice 10 automation.
/// </summary>
public interface ISelfHealingService
{
    /// <summary>
    /// Resolves the effective worker-facing policy for an execution.
    /// Missing rows and any misconfiguration mean disabled (safe default).
    /// </summary>
    Task<TestExecution.WorkerHealingPolicyDto> ResolvePolicyAsync(Guid executionId, CancellationToken ct);

    /// <summary>
    /// AI candidate suggestion for the live lease holder. Enforces policy
    /// (enabled + AI fallback), bounds evidence, redacts secrets, and
    /// schema-validates provider output. Returns DATA only.
    /// </summary>
    Task<IReadOnlyList<HealingCandidateDto>> SuggestCandidatesAsync(
        Guid executionId, HealingEvidenceDto evidence, CancellationToken ct);

    /// <summary>
    /// Persists worker-reported healing outcomes with assignment fencing:
    /// reports from a stale lease are rejected with ConflictException.
    /// A null assignment id is accepted only when the test genuinely holds
    /// no grid lease (legacy standalone worker); otherwise it is rejected.
    /// </summary>
    Task<int> RecordAttemptsAsync(
        Guid executionId, Guid? assignmentId,
        IReadOnlyList<TestExecution.WorkerHealingAttemptDto> attempts,
        CancellationToken ct);
}

public sealed record HealingEvidenceDto(
    string Action,
    string OriginalTarget,
    string DomFragment,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> NearbyText);

public sealed record HealingCandidateDto(
    string Strategy,
    string Value,
    string? Reason,
    decimal? Confidence);
