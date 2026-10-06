using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>
/// A human-gated test-maintenance proposal (Phase 4 Slice 4, web locators
/// only). Created deterministically by a project-scoped scan from persisted
/// self-healing evidence; never created from client-supplied locator data.
/// Approval mints a new Pending test version through the existing version
/// path — proposals never mutate Approved versions and never create
/// Approved versions. Locator values reuse the redacted persisted healing
/// values; never secrets or raw execution values.
/// </summary>
public sealed class MaintenanceProposal : EntityBase
{
    public Guid ProjectId { get; set; }
    public Guid TestCaseId { get; set; }
    public Guid TestCaseVersionId { get; set; }
    public int StepOrder { get; set; }
    public string StepAction { get; set; } = string.Empty;
    public string? OriginalStrategy { get; set; }
    public string? OriginalValue { get; set; }
    public string? ProposedStrategy { get; set; }
    public string? ProposedValue { get; set; }
    public SelfHealingStrategy HealingStrategy { get; set; } = SelfHealingStrategy.None;
    public string SignalType { get; set; } = string.Empty;
    public int Confidence { get; set; }
    public int OccurrenceCount { get; set; }
    public MaintenanceProposalStatus Status { get; set; } = MaintenanceProposalStatus.Proposed;
    public Guid? ProposedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? CreatedVersionId { get; set; }
    public uint RowVersion { get; set; }
}
