namespace AutoTestAi.Application.Reports;

/// <summary>
/// Project-scoped read-only audit exploration (Phase 4 Slice 2).
/// Filters for the audit_events table. The backend remains authoritative
/// for action strings; no second taxonomy is introduced here.
/// </summary>
public sealed record AuditEventFilters(
    string? Action,
    Guid? ActorUserId,
    string? EntityType);

/// <summary>
/// Safe audit-event projection for the Audit Explorer list response.
/// Metadata, IP address, and User-Agent are never exposed.
/// ActorUserId preserves the entity nullability (system-originated rows).
/// </summary>
public sealed record AuditEventItem(
    long Id,
    DateTimeOffset Timestamp,
    string Action,
    string EntityType,
    string? EntityId,
    Guid? ActorUserId);

/// <summary>CSV export envelope (bounded, deterministic order, safe fields only).</summary>
public sealed record AuditEventsExport(string FileName, string ContentType, byte[] Content);
