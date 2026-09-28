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
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class ExecutionTest : EntityBase
{
    public Guid ExecutionId { get; set; }
    public Guid TestCaseId { get; set; }
    public Guid? TestCaseVersionId { get; set; }
    public ExecutionTestStatus Status { get; set; } = ExecutionTestStatus.Queued;
    public string? WorkerId { get; set; }
    public int Attempt { get; set; }
    public long? DurationMs { get; set; }
    public string? ErrorType { get; set; }
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
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
}

public sealed class FailureAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExecutionTestId { get; set; }
    public FailureClassification Classification { get; set; } = FailureClassification.Unknown;
    public string? RootCause { get; set; }
    public JsonDocument? Evidence { get; set; }
    public decimal? Confidence { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
