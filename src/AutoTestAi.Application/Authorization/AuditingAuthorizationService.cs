using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestGeneration;

namespace AutoTestAi.Application.Authorization;

/// <summary>
/// Authorization-denial auditing decorator (Phase 4 Slice 11). Wraps the
/// inner <see cref="IAuthorizationService"/> so every consumer keeps its
/// behavior while authenticated <see cref="ForbiddenException"/> denials
/// with a trustworthy, existing project context gain one bounded
/// <c>authz.project_denied</c> audit row. Anonymous challenges
/// (<see cref="UnauthorizedAccessException"/>) are never recorded, probes of
/// nonexistent projects are skipped (no existence leak, no probe-amplified
/// rows), and the original exception propagates unchanged — the opaque
/// project-access contract is preserved byte-for-byte.
/// </summary>
public sealed class AuditingAuthorizationService : IAuthorizationService
{
    private readonly IAuthorizationService _inner;
    private readonly IAuditService _audit;
    private readonly IProjectStore _projects;
    private readonly ICurrentUserService _currentUser;
    private readonly AuthorizationDenialDedup _dedup;

    public AuditingAuthorizationService(
        IAuthorizationService inner,
        IAuditService audit,
        IProjectStore projects,
        ICurrentUserService currentUser,
        AuthorizationDenialDedup dedup)
    {
        _inner = inner;
        _audit = audit;
        _projects = projects;
        _currentUser = currentUser;
        _dedup = dedup;
    }

    public bool HasPermission(string permission) => _inner.HasPermission(permission);

    public bool IsAdmin() => _inner.IsAdmin();

    public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken cancellationToken)
        => _inner.CanAccessProjectAsync(projectId, cancellationToken);

    public async Task RequireProjectAccessAsync(
        Guid projectId, string? requiredPermission, CancellationToken cancellationToken)
    {
        try
        {
            await _inner.RequireProjectAccessAsync(projectId, requiredPermission, cancellationToken);
        }
        catch (ForbiddenException)
        {
            await TryRecordDenialAsync(projectId, requiredPermission, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Best-effort denial recording. Bookkeeping failures are swallowed so
    /// the caller still receives the identical
    /// <see cref="ForbiddenException"/>; only request cancellation is allowed
    /// to propagate instead (cooperative abort takes precedence over audit
    /// bookkeeping for a request that is already going away).
    /// </summary>
    private async Task TryRecordDenialAsync(
        Guid projectId, string? requiredPermission, CancellationToken cancellationToken)
    {
        try
        {
            // Reason derives from trusted service state in the same order the
            // inner service checks: permission first, membership second.
            // Never from exception text, paths, or client input — and never
            // permission codes.
            var reason = requiredPermission is not null && !_inner.HasPermission(requiredPermission)
                ? "permission"
                : "membership";
            if (!_dedup.ShouldRecord(projectId, _currentUser.ExternalIdentityId, reason))
                return;
            // Skip probes of nonexistent projects: without an existing
            // row there is no project audience for the event.
            var project = await _projects.GetByIdAsync(projectId, cancellationToken);
            if (project is null)
                return;
            // Actor attribution resolves server-side inside the audit
            // writer; EntityId follows the project.Guid-string audit
            // convention. Fixed-shape metadata only.
            await _audit.RecordAsync(
                "authz.project_denied", "project", projectId.ToString(),
                projectId, SafeMeta(reason), cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            // Swallowed unconditionally: audit bookkeeping must never replace
            // the denial. (IAuditService itself is already best-effort; this
            // guards the dedup and existence-lookup path around it.)
        }
    }

    private static string SafeMeta(string reason)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new { reason }));
}
