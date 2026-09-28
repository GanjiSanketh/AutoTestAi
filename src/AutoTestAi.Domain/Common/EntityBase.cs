namespace AutoTestAi.Domain.Common;

/// <summary>
/// Base class for entities with UUID identifiers and audit timestamps.
/// Follows docs/05-Database-Design.md: UUID PKs, TIMESTAMPTZ timestamps.
/// </summary>
public abstract class EntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
