namespace AutoTestAi.Application.Authorization;

/// <summary>
/// Keycloak realm role names (must match deploy/docker/keycloak/realm-autotestai.json).
/// </summary>
public static class AppRoles
{
    public const string Admin = "admin";
    public const string QaLead = "qa-lead";
    public const string Tester = "tester";
    public const string Viewer = "viewer";
}

/// <summary>
/// Maps realm roles to permissions (FR-1.1). Platform admins hold every permission
/// and bypass project membership; all other roles are project-scoped.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Map =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [AppRoles.Admin] = Permissions.All,
            [AppRoles.QaLead] = new[]
            {
                Permissions.DashboardRead,
                Permissions.ProjectsRead, Permissions.ProjectsManage,
                Permissions.TestCasesRead, Permissions.TestCasesManage,
                Permissions.ExecutionsRead, Permissions.ExecutionsExecute,
                Permissions.BugsRead, Permissions.BugsManage,
                Permissions.TicketsRead, Permissions.TicketsCreate,
                Permissions.ReportsRead,
            },
            [AppRoles.Tester] = new[]
            {
                Permissions.DashboardRead,
                Permissions.ProjectsRead,
                Permissions.TestCasesRead, Permissions.TestCasesManage,
                Permissions.ExecutionsRead, Permissions.ExecutionsExecute,
                Permissions.BugsRead, Permissions.BugsManage,
                Permissions.TicketsRead, Permissions.TicketsCreate,
                Permissions.ReportsRead,
            },
            [AppRoles.Viewer] = new[]
            {
                Permissions.DashboardRead,
                Permissions.ProjectsRead,
                Permissions.TestCasesRead,
                Permissions.ExecutionsRead,
                Permissions.BugsRead,
                Permissions.TicketsRead,
                Permissions.ReportsRead,
            },
        };

    public static IReadOnlyList<string> Resolve(IEnumerable<string> roles)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
            if (Map.TryGetValue(role, out var permissions))
                foreach (var permission in permissions)
                    result.Add(permission);
        return result.ToList();
    }

    public static bool IsAdmin(IEnumerable<string> roles)
        => roles.Contains(AppRoles.Admin, StringComparer.OrdinalIgnoreCase);
}
