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
