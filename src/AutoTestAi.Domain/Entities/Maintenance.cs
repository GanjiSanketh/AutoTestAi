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

/// <summary>
/// Recurring cadence for the deterministic maintenance scan (Phase 4 Slice
/// 10). Exactly one row per project (UNIQUE(ProjectId)). Temporal owns
/// firing/next-run; this row owns cadence config, lifecycle, and last-run
/// pointers. Scheduled scans only create Proposed rows through the existing
/// detector — approval/application stay human-gated. Never carries secrets,
/// credentials, or proposal contents.
/// </summary>
public sealed class MaintenanceScanSchedule : EntityBase
{
    public Guid ProjectId { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public ScheduleStatus Status { get; set; } = ScheduleStatus.Active;
    public DateTimeOffset? LastTriggeredAt { get; set; }
    public int? LastCandidatesDetected { get; set; }
    public int? LastProposalsCreated { get; set; }
    public Guid? CreatedBy { get; set; }
}
