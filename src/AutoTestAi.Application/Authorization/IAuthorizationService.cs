namespace AutoTestAi.Application.Authorization;

/// <summary>Thrown when an authenticated caller lacks permission. Maps to 403.</summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }
}

/// <summary>
/// Server-side authorization boundary (docs/04 §10, docs/06 §3).
/// Frontend visibility is UX only — every check here is enforced by the backend.
/// </summary>
public interface IAuthorizationService
{
    bool HasPermission(string permission);

    bool IsAdmin();

    Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> when anonymous,
    /// <see cref="ForbiddenException"/> when the caller lacks the permission
    /// or project access. Never returns 500 for authorization failures.
    /// </summary>
    Task RequireProjectAccessAsync(
        Guid projectId, string? requiredPermission, CancellationToken cancellationToken);
}
