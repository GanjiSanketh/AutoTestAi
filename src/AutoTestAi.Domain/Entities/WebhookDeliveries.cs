using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>
/// Durable CI/CD webhook delivery record (Phase 3 Slice 3B).
/// The authoritative idempotency boundary for provider deliveries:
/// one row per (IntegrationId, DeliveryId). Never carries secret values,
/// signatures, authorization headers, or raw provider payloads — only the
/// SHA-256 payload hash and redacted normalized metadata.
/// </summary>
public sealed class WebhookDelivery : EntityBase
{
    public Guid IntegrationId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>CI provider: github, gitlab, jenkins, azure.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Provider-scoped delivery identifier (GitHub delivery UUID, GitLab event
    /// UUID, Azure notification id, Jenkins caller-supplied idempotency key or
    /// derived hash). Unique per integration.
    /// </summary>
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>Normalized provider event type (e.g. push, Push Hook).</summary>
    public string EventType { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Hex-encoded SHA-256 of the exact raw request body.</summary>
    public string PayloadHash { get; set; } = string.Empty;

    public WebhookVerificationStatus VerificationStatus { get; set; } = WebhookVerificationStatus.Pending;

    public WebhookProcessingStatus ProcessingStatus { get; set; } = WebhookProcessingStatus.Received;

    /// <summary>
    /// Redacted normalized event metadata (branch, commit, repository, actor,
    /// filter outcome). Never secrets, headers, signatures, or raw payloads.
    /// </summary>
    public string? NormalizedMetadataJson { get; set; }

    /// <summary>First execution started for this delivery (suite fan-out may start more; see TriggeredCount).</summary>
    public Guid? ExecutionId { get; set; }

    /// <summary>How many executions this delivery triggered (suite fan-out).</summary>
    public int TriggeredCount { get; set; }

    /// <summary>Safe diagnostic for rejected/failed deliveries (never secrets).</summary>
    public string? FailureReason { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>
    /// Crash-recovery lease: token of the processor currently allowed to
    /// execute this delivery. Null means unclaimed.
    /// </summary>
    public Guid? ClaimToken { get; set; }

    /// <summary>UTC expiry of the current claim. Expired claims are reclaimable.</summary>
    public DateTimeOffset? ClaimExpiresAt { get; set; }

    /// <summary>Optimistic concurrency token (compare-and-set for claim/finish writes).</summary>
    public uint RowVersion { get; set; }
}
