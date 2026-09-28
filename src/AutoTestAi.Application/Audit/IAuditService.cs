namespace AutoTestAi.Application.Audit;

/// <summary>
/// Audit writer for security-relevant operations (docs/05: audit_events).
/// Implementations must never throw — audit must not fail the operation.
/// Never records secrets, tokens, or credentials.
/// </summary>
public interface IAuditService
{
    Task RecordAsync(
        string action,
        string entityType,
        string? entityId,
        Guid? projectId,
        string? metadataJson,
        CancellationToken cancellationToken);
}
