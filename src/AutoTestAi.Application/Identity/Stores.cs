namespace AutoTestAi.Application.Identity;

/// <summary>
/// Persistence seams for identity/authorization. Implemented in Infrastructure;
/// deny-all fallbacks are used when no database is configured (fail closed).
/// </summary>
public interface IProjectMembershipStore
{
    Task<bool> IsMemberAsync(string externalIdentityId, Guid projectId, CancellationToken cancellationToken);
}

public interface IExecutionProjectResolver
{
    /// <summary>Returns the owning project id, or null when the execution does not exist.</summary>
    Task<Guid?> GetProjectIdAsync(Guid executionId, CancellationToken cancellationToken);
}

/// <summary>
/// Just-in-time provisioning keyed on the stable external identity id
/// (users.external_identity_id). Email is never used as the identity key.
/// </summary>
public interface IUserDirectory
{
    Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken cancellationToken);
    Task<Guid> EnsureProvisionedAsync(
        string externalIdentityId, string? email, string? displayName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Provisioning with creation outcome (Phase 4 Slice 11). <c>Created</c>
    /// is true only when this call inserted the user row; concurrent losers
    /// surface the unique-identity failure instead of a false creation.
    /// </summary>
    Task<(Guid UserId, bool Created)> EnsureProvisionedWithOutcomeAsync(
        string externalIdentityId, string? email, string? displayName,
        CancellationToken cancellationToken);
}
