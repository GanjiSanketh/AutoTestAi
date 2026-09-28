using AutoTestAi.Application.Identity;

namespace AutoTestAi.Infrastructure.Identity;

/// <summary>
/// Fail-closed fallbacks used when no database is configured.
/// Authorization denies; provisioning is a no-op (never invent an identity).
/// </summary>
public sealed class DenyAllProjectMembershipStore : IProjectMembershipStore
{
    public Task<bool> IsMemberAsync(
        string externalIdentityId, Guid projectId, CancellationToken cancellationToken)
        => Task.FromResult(false);
}

public sealed class UnknownExecutionProjectResolver : IExecutionProjectResolver
{
    public Task<Guid?> GetProjectIdAsync(Guid executionId, CancellationToken cancellationToken)
        => Task.FromResult<Guid?>(null);
}

public sealed class NullUserDirectory : IUserDirectory
{
    public Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken cancellationToken)
        => Task.FromResult<Guid?>(null);

    public Task<Guid> EnsureProvisionedAsync(
        string externalIdentityId, string? email, string? displayName,
        CancellationToken cancellationToken)
        => Task.FromResult(Guid.Empty);
}
