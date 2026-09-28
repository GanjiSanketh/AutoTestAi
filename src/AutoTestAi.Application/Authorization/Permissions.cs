namespace AutoTestAi.Application.Authorization;

/// <summary>
/// Central permission identifiers (FR-1.1). Never scatter these strings
/// across endpoints — reference this class.
/// </summary>
public static class Permissions
{
    public const string DashboardRead = "dashboard.read";
    public const string ProjectsRead = "projects.read";
    public const string ProjectsManage = "projects.manage";
    public const string TestCasesRead = "testcases.read";
    public const string TestCasesManage = "testcases.manage";
    public const string ExecutionsRead = "executions.read";
    public const string ExecutionsExecute = "executions.execute";
    public const string BugsRead = "bugs.read";
    public const string BugsManage = "bugs.manage";
    public const string TicketsRead = "tickets.read";
    public const string TicketsCreate = "tickets.create";
    public const string ReportsRead = "reports.read";
    public const string SettingsManage = "settings.manage";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        DashboardRead, ProjectsRead, ProjectsManage,
        TestCasesRead, TestCasesManage,
        ExecutionsRead, ExecutionsExecute,
        BugsRead, BugsManage,
        TicketsRead, TicketsCreate,
        ReportsRead, SettingsManage,
    };
}
