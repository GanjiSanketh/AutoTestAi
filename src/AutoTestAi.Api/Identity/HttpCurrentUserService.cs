using AutoTestAi.Application.Identity;

namespace AutoTestAi.Api.Identity;

/// <summary>
/// Bridges HttpContext.User to <see cref="ICurrentUserService"/>.
/// The only place where application code touches the HTTP principal.
/// </summary>
public sealed class HttpCurrentUserService : ICurrentUserService
{
    private readonly CurrentUserSnapshot _snapshot;

    public HttpCurrentUserService(IHttpContextAccessor accessor)
        => _snapshot = CurrentUserFactory.FromPrincipal(
            accessor.HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal());

    public bool IsAuthenticated => _snapshot.IsAuthenticated;
    public string? ExternalIdentityId => _snapshot.ExternalIdentityId;
    public string? Email => _snapshot.Email;
    public string? DisplayName => _snapshot.DisplayName;
    public IReadOnlyCollection<string> Roles => _snapshot.Roles;
    public IReadOnlyCollection<string> Permissions => _snapshot.Permissions;
}
