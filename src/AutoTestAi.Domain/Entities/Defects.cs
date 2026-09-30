using System.Text.Json;
using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

public sealed class Defect : EntityBase
{
    public Guid ProjectId { get; set; }
    public Guid? ExecutionTestId { get; set; }
    /// <summary>
    /// Advisory analysis this defect was created from (human decision, audited).
    /// Null when created without AI analysis.
    /// </summary>
    public Guid? FailureAnalysisId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Severity Severity { get; set; } = Severity.Medium;
    public DefectStatus Status { get; set; } = DefectStatus.Open;
    public FailureClassification? RootCauseType { get; set; }
    public decimal? AiConfidence { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class Ticket : EntityBase
{
    public Guid ProjectId { get; set; }
    public Guid? DefectId { get; set; }
    /// <summary>
    /// Jira (or other provider) integration this ticket was created through.
    /// Nullable for pre-Slice-7 rows; required for Slice-7 manual Jira tickets.
    /// </summary>
    public Guid? IntegrationId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ExternalTicketId { get; set; }
    /// <summary>Human-readable provider key (e.g. Jira ABC-123).</summary>
    public string? ExternalKey { get; set; }
    public string? ExternalUrl { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Status { get; set; }
    public TicketSyncStatus SyncStatus { get; set; } = TicketSyncStatus.Pending;
    /// <summary>
    /// How this ticket was created (Phase 2 Slice 10). Pre-Slice-10 rows
    /// read as Manual. Automation sets Automatic.
    /// </summary>
    public TicketOrigin Origin { get; set; } = TicketOrigin.Manual;
    /// <summary>Number of Jira creation attempts for this row (automation retry bookkeeping).</summary>
    public int AttemptCount { get; set; }
    /// <summary>Earliest time a failed automatic attempt may be retried (null = no retry scheduled).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>
    /// Slice 10 automation lease: token of the claimant currently allowed to
    /// execute this intent. Null means unclaimed. Correctness across API
    /// instances comes from this persisted lease plus optimistic concurrency,
    /// never from in-process locks alone.
    /// </summary>
    public Guid? ClaimToken { get; set; }
    /// <summary>UTC expiry of the current claim. Expired claims are reclaimable (crash recovery).</summary>
    public DateTimeOffset? ClaimExpiresAt { get; set; }
    /// <summary>Optimistic concurrency token (compare-and-set for claim/finish writes).</summary>
    public uint RowVersion { get; set; }
    public Guid? CreatedBy { get; set; }
    /// <summary>Safe diagnostic for the last failed creation attempt (never secrets).</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// External integration config. Provider secrets are referenced, never stored
/// as plaintext in this table (docs/05 §integrations).
/// </summary>
public sealed class Integration : EntityBase
{
    public Guid? ProjectId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string IntegrationType { get; set; } = string.Empty;
    public JsonDocument? Configuration { get; set; }
    public string? SecretReference { get; set; }
    public IntegrationStatus Status { get; set; } = IntegrationStatus.Active;
}

/// <summary>
/// Project-scoped automatic Jira ticket policy (Phase 2 Slice 10).
/// One row per project at most. Absence of a row means automation is
/// disabled. Evaluation is deterministic; no AI is involved.
/// </summary>
public sealed class AutoTicketPolicy : EntityBase
{
    public Guid ProjectId { get; set; }
    public bool Enabled { get; set; }
    /// <summary>
    /// Explicit Jira integration to ticket through. Null resolves to the
    /// project's default Jira integration.
    /// </summary>
    public Guid? IntegrationId { get; set; }
    /// <summary>Comma-separated eligible severities (e.g. "Critical,High").</summary>
    public string Severities { get; set; } = string.Empty;
    /// <summary>Comma-separated eligible defect statuses (e.g. "Open").</summary>
    public string DefectStatuses { get; set; } = string.Empty;
    /// <summary>Comma-separated eligible failure classifications.</summary>
    public string Classifications { get; set; } = string.Empty;
    /// <summary>Optional minimum AI confidence (0-1). Null disables the filter.</summary>
    public decimal? MinimumConfidence { get; set; }
    public Guid? UpdatedBy { get; set; }
}
