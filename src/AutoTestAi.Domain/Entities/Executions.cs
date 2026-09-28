using System.Text.Json;
using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>One test run. Immutable after completion except explicit enrichment.</summary>
public sealed class Execution : EntityBase
{
    public Guid ProjectId { get; set; }
    public Guid? SuiteId { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;
    public TriggerType TriggerType { get; set; } = TriggerType.Manual;
    public Guid? EnvironmentId { get; set; }
    public string? WorkflowId { get; set; }

    /// <summary>
    /// Client-supplied idempotency key (Slice 5 §41). Repeating a start request
    /// with the same key returns the original execution instead of duplicating it.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class ExecutionTest : EntityBase
{
    public Guid ExecutionId { get; set; }
    public Guid TestCaseId { get; set; }

    /// <summary>
    /// Exact immutable version bound at creation (Slice 5 invariant). Never
    /// re-resolved to "latest" after the execution starts.
    /// </summary>
    public Guid? TestCaseVersionId { get; set; }

    public ExecutionTestStatus Status { get; set; } = ExecutionTestStatus.Queued;
    public string? WorkerId { get; set; }
    public int Attempt { get; set; }

    /// <summary>Framework/browser snapshot taken from the bound version at creation.</summary>
    public string? Framework { get; set; }
    public string? Browser { get; set; }

    /// <summary>Failure classification for the result (Slice 5 §19). Unknown until terminal.</summary>
    public FailureClassification FailureClassification { get; set; } = FailureClassification.Unknown;

    public long? DurationMs { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Persisted per-step outcome (Slice 5 §16). Step values are stored redacted:
/// password-like targets never persist plaintext.
/// </summary>
public sealed class ExecutionStepResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExecutionTestId { get; set; }
    public int StepOrder { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Target { get; set; }
    public ExecutionTestStatus Status { get; set; } = ExecutionTestStatus.Queued;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ExecutionLog
{
    public long Id { get; set; }
    public Guid ExecutionTestId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = string.Empty;
    public JsonDocument? Metadata { get; set; }
}

public sealed class ExecutionArtifact : EntityBase
{
    public Guid ExecutionTestId { get; set; }
    public string ArtifactType { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Human-friendly display name; object names never carry secrets.</summary>
    public string? FileName { get; set; }

    /// <summary>Owning step order when the artifact belongs to a step.</summary>
    public int? StepOrder { get; set; }

    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
}

public sealed class FailureAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExecutionTestId { get; set; }

    /// <summary>1-based attempt number; retries append new rows, never overwrite.</summary>
    public int Attempt { get; set; } = 1;

    public AnalysisStatus Status { get; set; } = AnalysisStatus.Running;
    public FailureClassification Classification { get; set; } = FailureClassification.Unknown;

    /// <summary>Probable cause (advisory — never mutates execution history).</summary>
    public string? RootCause { get; set; }

    public string? Summary { get; set; }

    /// <summary>Bounded redacted evidence snapshot (never raw provider input).</summary>
    public JsonDocument? Evidence { get; set; }

    public List<string> Assumptions { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? RecommendedAction { get; set; }
    public bool IsLikelyDefect { get; set; }
    public decimal? Confidence { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public long? LatencyMs { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
