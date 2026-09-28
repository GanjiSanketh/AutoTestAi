using AutoTestAi.Domain.Common;

namespace AutoTestAi.Domain.Entities;

/// <summary>Application user. Identity itself is owned by Keycloak; this mirrors the local profile.</summary>
public sealed class User : EntityBase
{
    public string ExternalIdentityId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class Role : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class Permission : EntityBase
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public sealed class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Security-relevant administrative action (docs/05: audit_events).</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public Guid? ProjectId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
