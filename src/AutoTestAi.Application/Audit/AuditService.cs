using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AutoTestAi.Application.Audit;

/// <summary>Best-effort audit writer; swallows persistence failures so audited operations still succeed.</summary>
public sealed class AuditService : IAuditService
{
    private readonly IProjectStore _store;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        IProjectStore store,
        ICurrentUserService currentUser,
        IUserDirectory users,
        ILogger<AuditService> logger)
    {
        _store = store;
        _currentUser = currentUser;
        _users = users;
        _logger = logger;
    }

    public async Task RecordAsync(
        string action,
        string entityType,
        string? entityId,
        Guid? projectId,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        try
        {
            Guid? actorUserId = null;
            if (!string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId))
                actorUserId = await _users.FindAppUserIdAsync(
                    _currentUser.ExternalIdentityId!, cancellationToken);

            await _store.RecordAuditAsync(new AuditEvent
            {
                ActorUserId = actorUserId,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                ProjectId = projectId,
                MetadataJson = metadataJson,
            }, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Audit is best-effort: log category only, never fail the caller.
            _logger.LogWarning(ex, "Audit event {Action} for {EntityType} was not recorded.", action, entityType);
        }
    }
}
