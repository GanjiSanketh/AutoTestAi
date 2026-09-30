using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>
/// Project-scoped self-healing policy (Phase 2 Slice 11). Safe by default:
/// absence of a row means healing is disabled. Never contains credentials.
/// </summary>
public sealed class SelfHealingPolicy : EntityBase
{
    public Guid ProjectId { get; set; }

    /// <summary>Master switch. Default false: healing runs only when explicitly enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>AI candidate fallback. Default false; requires Enabled.</summary>
    public bool AiFallbackEnabled { get; set; } = false;

    /// <summary>Maximum healing retries per failed step. Always 1 in Slice 11.</summary>
    public int MaxAttemptsPerStep { get; set; } = 1;

    /// <summary>Minimum deterministic candidate score (0-100). Null = conservative default (50).</summary>
    public int? MinDeterministicScore { get; set; }

    /// <summary>
    /// Minimum AI confidence (0-1). Advisory only: never overrides deterministic
    /// validation. Null disables the filter.
    /// </summary>
    public decimal? MinAiConfidence { get; set; }

    /// <summary>Comma-joined allowlisted locator strategies. Empty = safe default set.</summary>
    public string AllowedStrategies { get; set; } = string.Empty;

    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// One persisted healing outcome per (execution test, step). Append-only intent:
/// retries create worker-side candidates but only the final outcome persists,
/// guarded by a unique index on (ExecutionTestId, StepOrder) so concurrent
/// workers cannot create duplicate authoritative state. Never stores raw DOM,
/// secrets, or full AI prompts — bounded redacted metadata only.
/// </summary>
public sealed class SelfHealingAttempt : EntityBase
{
    public Guid ProjectId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid ExecutionTestId { get; set; }
    public Guid TestCaseId { get; set; }
    public Guid? TestCaseVersionId { get; set; }
    public int StepOrder { get; set; }
    public string StepAction { get; set; } = string.Empty;
    public string? OriginalStrategy { get; set; }
    public string? OriginalValue { get; set; }
    public string? RecoveredStrategy { get; set; }
    public string? RecoveredValue { get; set; }
    public SelfHealingStrategy HealingStrategy { get; set; } = SelfHealingStrategy.None;
    public SelfHealingStatus Status { get; set; } = SelfHealingStatus.Failed;
    public int CandidateCount { get; set; }
    public bool WasApplied { get; set; }
    public bool IsAiAssisted { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Lease that owned the execution when healing ran (Phase 2 Slice 9 fencing).
    /// A stale worker's report for a superseded assignment is rejected.
    /// </summary>
    public Guid? AssignmentId { get; set; }
}
