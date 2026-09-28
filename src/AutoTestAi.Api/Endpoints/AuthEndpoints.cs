using AutoTestAi.Application.Identity;

namespace AutoTestAi.Api.Endpoints;

public sealed record CurrentUserProfile(
    Guid? Id,
    string? ExternalIdentityId,
    string? Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

/// <summary>Authenticated identity surface (docs/06 §3). Never returns tokens or secrets.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth");

        auth.MapGet("/me", async (
                ICurrentUserService currentUser,
                IUserDirectory directory,
                CancellationToken cancellationToken) =>
            {
                var appUserId = currentUser.ExternalIdentityId is null
                    ? null
                    : await directory.FindAppUserIdAsync(
                        currentUser.ExternalIdentityId, cancellationToken);
                return Results.Ok(new CurrentUserProfile(
                    appUserId,
                    currentUser.ExternalIdentityId,
                    currentUser.Email,
                    currentUser.DisplayName,
                    currentUser.Roles.ToList(),
                    currentUser.Permissions.ToList()));
            })
            .WithName("GetCurrentUser")
            .WithSummary("Current authenticated user profile (safe fields only).")
            .RequireAuthorization();

        return app;
    }
}
