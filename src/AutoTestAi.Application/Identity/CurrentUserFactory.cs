using System.Security.Claims;
using System.Text.Json;
using AutoTestAi.Application.Authorization;

namespace AutoTestAi.Application.Identity;

/// <summary>Safe, serializable snapshot of the caller. No tokens or secrets.</summary>
public sealed record CurrentUserSnapshot(
    bool IsAuthenticated,
    string? ExternalIdentityId,
    string? Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

/// <summary>
/// Pure claims mapping shared by the API implementation and unit tests.
/// Reads standard OIDC claims plus Keycloak's realm_access/resource_access shapes.
/// </summary>
public static class CurrentUserFactory
{
    public static CurrentUserSnapshot FromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return new CurrentUserSnapshot(
                false, null, null, null, Array.Empty<string>(), Array.Empty<string>());
        }

        var externalId = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst("email")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value;
        var displayName = principal.FindFirst("name")?.Value
            ?? principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value
            ?? email;

        var roles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.FindAll("role")) AddRole(roles, claim.Value);
        foreach (var claim in principal.FindAll("roles")) AddRole(roles, claim.Value);
        foreach (var claim in principal.FindAll(ClaimTypes.Role)) AddRole(roles, claim.Value);
        foreach (var claim in principal.FindAll("realm_access")) AddKeycloakRoles(roles, claim.Value);
        foreach (var claim in principal.FindAll("resource_access")) AddKeycloakRoles(roles, claim.Value);

        var roleList = roles.ToList();
        return new CurrentUserSnapshot(
            true, externalId, email, displayName, roleList,
            RolePermissions.Resolve(roleList));
    }

    private static void AddRole(ISet<string> roles, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            roles.Add(value.Trim());
    }

    /// <summary>
    /// Parses Keycloak shapes: {"roles":[...]} or {"client":{"roles":[...]}}.
    /// Malformed values are ignored — never fail authentication on role parsing.
    /// </summary>
    private static void AddKeycloakRoles(ISet<string> roles, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) AddRole(roles, item.GetString());
                return;
            }
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("roles", out var direct) && direct.ValueKind == JsonValueKind.Array)
                foreach (var item in direct.EnumerateArray()) AddRole(roles, item.GetString());
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object &&
                    property.Value.TryGetProperty("roles", out var nested) &&
                    nested.ValueKind == JsonValueKind.Array)
                    foreach (var item in nested.EnumerateArray()) AddRole(roles, item.GetString());
            }
        }
        catch (JsonException)
        {
            // Ignore malformed role payloads.
        }
    }
}
