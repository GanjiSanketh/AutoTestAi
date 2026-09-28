using AutoTestAi.Application.Identity;

namespace AutoTestAi.Api.Middleware;

/// <summary>
/// Just-in-time provisioning for authenticated API callers (docs/05: users).
/// Maps the Keycloak subject to users.external_identity_id and creates the row
/// on first access. Never fails the request: provisioning is best-effort while
/// authorization itself stays fail-closed downstream.
/// </summary>
public sealed class UserProvisioningMiddleware(
    RequestDelegate next,
    ILogger<UserProvisioningMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ICurrentUserService currentUser, IUserDirectory directory)
    {
        if (currentUser.IsAuthenticated &&
            !string.IsNullOrWhiteSpace(currentUser.ExternalIdentityId) &&
            context.Request.Path.StartsWithSegments("/api/v1") &&
            !context.Request.Path.StartsWithSegments("/api/v1/health"))
        {
            try
            {
                await directory.EnsureProvisionedAsync(
                    currentUser.ExternalIdentityId!,
                    currentUser.Email,
                    currentUser.DisplayName,
                    context.RequestAborted);
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
