using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Identity;

namespace AutoTestAi.Api.Middleware;

/// <summary>
/// Just-in-time provisioning for authenticated API callers (docs/05: users).
/// Maps the Keycloak subject to users.external_identity_id and creates the row
/// on first access. The first successful creation also records one
/// <c>auth.provisioned</c> audit event (Phase 4 Slice 11); existing users
/// never re-emit it. Never fails the request: provisioning and its audit are
/// best-effort while authorization itself stays fail-closed downstream.
/// </summary>
public sealed class UserProvisioningMiddleware(
    RequestDelegate next,
    ILogger<UserProvisioningMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUserService currentUser,
        IUserDirectory directory,
        IAuditService audit)
    {
        if (currentUser.IsAuthenticated &&
            !string.IsNullOrWhiteSpace(currentUser.ExternalIdentityId) &&
            context.Request.Path.StartsWithSegments("/api/v1") &&
            !context.Request.Path.StartsWithSegments("/api/v1/health"))
        {
            try
            {
                var (userId, created) = await directory.EnsureProvisionedWithOutcomeAsync(
                    currentUser.ExternalIdentityId!,
                    currentUser.Email,
                    currentUser.DisplayName,
                    context.RequestAborted);
                if (created)
                {
                    // Actor attribution resolves server-side from the same
                    // identity; ProjectId is null (platform-level first access);
                    // no email, token, or request contents are recorded.
                    await audit.RecordAsync(
                        "auth.provisioned", "user", userId.ToString(),
                        null, null, context.RequestAborted);
                }
            }
            catch (Exception ex)
            {
                // Category-only logging: no tokens, secrets or contact details.
                logger.LogWarning(ex, "User provisioning skipped for {Method} {Path}.",
                    context.Request.Method, context.Request.Path);
            }
        }

        await next(context);
    }
}
