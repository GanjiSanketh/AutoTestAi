using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Identity;

namespace AutoTestAi.UnitTests;

internal sealed class StubCurrentUser : ICurrentUserService
{
    public bool IsAuthenticated { get; init; }
    public string? ExternalIdentityId { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyCollection<string> Roles { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();
}

internal sealed class StubMembershipStore : IProjectMembershipStore
{
    private readonly HashSet<(string, Guid)> _members = new();
    public void Add(string externalId, Guid projectId) => _members.Add((externalId, projectId));
    public Task<bool> IsMemberAsync(string externalIdentityId, Guid projectId, CancellationToken ct)
        => Task.FromResult(_members.Contains((externalIdentityId, projectId)));
}

public sealed class AuthorizationServiceTests
{
    private static IAuthorizationService Service(ICurrentUserService user, StubMembershipStore store)
        => new AuthorizationService(user, store);

    private static StubCurrentUser Tester(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["tester"],
        Permissions = RolePermissions.Resolve(["tester"]),
    };

    [Fact]
    public async Task Anonymous_CannotAccessProject()
    {
        var service = Service(new StubCurrentUser { IsAuthenticated = false }, new StubMembershipStore());
        Assert.False(await service.CanAccessProjectAsync(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.RequireProjectAccessAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Member_WithPermission_IsAllowed()
    {
        var projectId = Guid.NewGuid();
        var store = new StubMembershipStore();
        store.Add("user-1", projectId);
        var service = Service(Tester(), store);
        await service.RequireProjectAccessAsync(projectId, Permissions.ExecutionsExecute, CancellationToken.None);
    }

    [Fact]
    public async Task NonMember_GetsForbidden_NotNotFound()
    {
        var service = Service(Tester(), new StubMembershipStore());
        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => service.RequireProjectAccessAsync(Guid.NewGuid(), null, CancellationToken.None));
        Assert.Contains("no access", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Member_WithoutPermission_GetsForbidden()
    {
        var projectId = Guid.NewGuid();
        var store = new StubMembershipStore();
        store.Add("user-1", projectId);
        var viewer = new StubCurrentUser
        {
            IsAuthenticated = true,
            ExternalIdentityId = "user-1",
            Roles = ["viewer"],
            Permissions = RolePermissions.Resolve(["viewer"]),
        };
        var service = Service(viewer, store);
        await Assert.ThrowsAsync<ForbiddenException>(
            () => service.RequireProjectAccessAsync(projectId, Permissions.ExecutionsExecute, CancellationToken.None));
        // Reading is still allowed for the same project.
        await service.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, CancellationToken.None);
    }

    [Fact]
    public async Task Admin_BypassesMembership()
    {
        var admin = new StubCurrentUser
        {
            IsAuthenticated = true,
            ExternalIdentityId = "admin-1",
            Roles = ["admin"],
            Permissions = RolePermissions.Resolve(["admin"]),
        };
        var service = Service(admin, new StubMembershipStore());
        Assert.True(service.IsAdmin());
        await service.RequireProjectAccessAsync(Guid.NewGuid(), Permissions.SettingsManage, CancellationToken.None);
    }
}
