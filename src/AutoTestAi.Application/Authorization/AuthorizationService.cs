using AutoTestAi.Application.Identity;

namespace AutoTestAi.Application.Authorization;

public sealed class AuthorizationService : IAuthorizationService
{
    private readonly ICurrentUserService _currentUser;
    private readonly IProjectMembershipStore _memberships;

    public AuthorizationService(ICurrentUserService currentUser, IProjectMembershipStore memberships)
    {
        _currentUser = currentUser;
        _memberships = memberships;
    }

    public bool HasPermission(string permission)
        => _currentUser.IsAuthenticated &&
           _currentUser.Permissions.Contains(permission, StringComparer.Ordinal);

    public bool IsAdmin()
        => _currentUser.IsAuthenticated && RolePermissions.IsAdmin(_currentUser.Roles);

    public async Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated) return false;
        if (string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)) return false;
        // Platform admins bypass project membership (documented in docs/04).
        if (IsAdmin()) return true;
        return await _memberships.IsMemberAsync(
            _currentUser.ExternalIdentityId!, projectId, cancellationToken);
    }

    public async Task RequireProjectAccessAsync(
        Guid projectId, string? requiredPermission, CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        if (requiredPermission is not null && !HasPermission(requiredPermission))
            throw new ForbiddenException($"Missing required permission '{requiredPermission}'.");
        if (!await CanAccessProjectAsync(projectId, cancellationToken))
            throw new ForbiddenException("The caller has no access to this project.");
    }
}
