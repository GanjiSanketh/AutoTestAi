using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>
/// Project-scoped visual baseline reference image (Phase 3 Slice 3C-4D-1).
/// Reference metadata only: image bytes live in object storage under a
/// server-generated key. Identity is test-case version plus step order;
/// exactly one Active row exists per (version, step) enforced by a
/// filtered unique index, with service-level enforcement for stores that
/// do not honor partial indexes. Superseded rows remain for history;
/// rejected candidates are removed. Comparison logic belongs to 3C-4D-2;
/// this slice manages lifecycle only and execution never consults it.
/// </summary>
public sealed class VisualBaseline : EntityBase
{
    public Guid ProjectId { get; set; }

    public Guid TestCaseId { get; set; }

    public Guid TestCaseVersionId { get; set; }

    /// <summary>1-based order of the verifyScreenshot step this baseline anchors.</summary>
    public int StepOrder { get; set; }

    public VisualBaselineStatus Status { get; set; } = VisualBaselineStatus.Candidate;

    /// <summary>Server-generated object-storage key for the reference PNG bytes.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the reference bytes (idempotency key).</summary>
    public string Sha256 { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public string ContentType { get; set; } = "image/png";

    /// <summary>
    /// Per-baseline mismatch tolerance in basis points (0-10000). Null
    /// means the slice default applies at comparison time (3C-4D-2).
    /// </summary>
    public int? MismatchThresholdBps { get; set; }

    public Guid? CreatedBy { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }
}
