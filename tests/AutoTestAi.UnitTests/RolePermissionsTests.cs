using AutoTestAi.Application.Authorization;

namespace AutoTestAi.UnitTests;

public sealed class RolePermissionsTests
{
    [Fact]
    public void Admin_HasEveryPermission()
    {
        var permissions = RolePermissions.Resolve(["admin"]);
        Assert.Equal(Permissions.All.OrderBy(p => p), permissions.OrderBy(p => p));
    }

    [Fact]
    public void Viewer_IsReadOnly()
    {
        var permissions = RolePermissions.Resolve(["viewer"]);
        Assert.Contains(Permissions.ProjectsRead, permissions);
        Assert.DoesNotContain(Permissions.ProjectsManage, permissions);
        Assert.DoesNotContain(Permissions.ExecutionsExecute, permissions);
        Assert.DoesNotContain(Permissions.SettingsManage, permissions);
    }

    [Fact]
    public void QaLead_CannotManageSettings()
    {
        var permissions = RolePermissions.Resolve(["qa-lead"]);
        Assert.Contains(Permissions.ExecutionsExecute, permissions);
        Assert.DoesNotContain(Permissions.SettingsManage, permissions);
    }

    [Fact]
    public void UnknownRole_GrantsNothing()
    {
        Assert.Empty(RolePermissions.Resolve(["superhero"]));
    }

    [Fact]
    public void RoleMatching_IsCaseInsensitive()
    {
        Assert.True(RolePermissions.IsAdmin(["ADMIN"]));
        Assert.Contains(Permissions.DashboardRead, RolePermissions.Resolve(["Tester"]));
    }
}
