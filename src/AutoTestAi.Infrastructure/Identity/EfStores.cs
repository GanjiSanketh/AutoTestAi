using AutoTestAi.Application.Identity;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AutoTestAi.Infrastructure.Identity;

/// <summary>EF Core-backed identity stores (docs/05: users, project_members, executions).</summary>
public sealed class EfProjectMembershipStore : IProjectMembershipStore
{
    private readonly AutoTestAiDbContext _db;

    public EfProjectMembershipStore(AutoTestAiDbContext db) => _db = db;

    public Task<bool> IsMemberAsync(
        string externalIdentityId, Guid projectId, CancellationToken cancellationToken)
        => (from member in _db.ProjectMembers
            join user in _db.Users on member.UserId equals user.Id
            where member.ProjectId == projectId
               && user.ExternalIdentityId == externalIdentityId
               && user.IsActive
            select member).AnyAsync(cancellationToken);
}

public sealed class EfExecutionProjectResolver : IExecutionProjectResolver
{
    private readonly AutoTestAiDbContext _db;

    public EfExecutionProjectResolver(AutoTestAiDbContext db) => _db = db;

    public async Task<Guid?> GetProjectIdAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await _db.Executions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);
        return execution?.ProjectId;
    }
}

public sealed class EfUserDirectory : IUserDirectory
{
    private readonly AutoTestAiDbContext _db;
    private readonly ILogger<EfUserDirectory> _logger;

    public EfUserDirectory(AutoTestAiDbContext db, ILogger<EfUserDirectory> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.ExternalIdentityId == externalIdentityId, cancellationToken);
        return user?.Id;
    }

    public async Task<Guid> EnsureProvisionedAsync(
        string externalIdentityId, string? email, string? displayName,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.ExternalIdentityId == externalIdentityId, cancellationToken);
        if (user is not null)
        {
            // Synchronize safe profile fields only. External id is the stable key —
            // email is never used as the identity key.
            if (email is not null && user.Email != email) user.Email = email;
            var name = string.IsNullOrWhiteSpace(displayName) ? email : displayName;
            if (name is not null && user.DisplayName != name) user.DisplayName = name;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return user.Id;
        }

        user = new Domain.Entities.User
        {
            ExternalIdentityId = externalIdentityId,
            Email = email ?? string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? (email ?? externalIdentityId) : displayName!,
            IsActive = true,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        // Log the category only — never tokens, secrets or contact details beyond the stable id.
        _logger.LogInformation("Provisioned application user for external identity {ExternalIdentityId}.", externalIdentityId);
        return user.Id;
    }
}
