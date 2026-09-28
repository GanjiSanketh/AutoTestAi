namespace AutoTestAi.Application.Identity;

/// <summary>
/// Current authenticated user. Application services depend on this abstraction —
/// never on HttpContext directly (docs/04 §4).
/// </summary>
public interface ICurrentUserService
{
    bool IsAuthenticated { get; }

    /// <summary>Stable Keycloak subject (users.external_identity_id). Never email.</summary>
    string? ExternalIdentityId { get; }

    string? Email { get; }

    string? DisplayName { get; }

    IReadOnlyCollection<string> Roles { get; }

    IReadOnlyCollection<string> Permissions { get; }
}
