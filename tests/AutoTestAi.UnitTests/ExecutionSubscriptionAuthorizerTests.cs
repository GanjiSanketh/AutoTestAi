using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestExecution;

namespace AutoTestAi.UnitTests;

internal sealed class StubExecutionResolver : IExecutionProjectResolver
{
    private readonly Dictionary<Guid, Guid> _map = new();
    public void Add(Guid executionId, Guid projectId) => _map[executionId] = projectId;
    public Task<Guid?> GetProjectIdAsync(Guid executionId, CancellationToken ct)
        => Task.FromResult(_map.TryGetValue(executionId, out var p) ? (Guid?)p : null);
}

public sealed class ExecutionSubscriptionAuthorizerTests
{
    [Fact]
    public async Task UnknownExecution_ThrowsNotFound()
    {
        var authorizer = new ExecutionSubscriptionAuthorizer(
            new StubExecutionResolver(),
            new AuthorizationService(
                new StubCurrentUser { IsAuthenticated = true, ExternalIdentityId = "u" },
                new StubMembershipStore()));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => authorizer.AuthorizeAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task UnauthorizedCaller_ThrowsForbidden()
    {
        var executionId = Guid.NewGuid();
        var resolver = new StubExecutionResolver();
        resolver.Add(executionId, Guid.NewGuid());
        var authorizer = new ExecutionSubscriptionAuthorizer(
            resolver,
            new AuthorizationService(new StubCurrentUser { IsAuthenticated = false }, new StubMembershipStore()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => authorizer.AuthorizeAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task NonMember_ThrowsForbidden()
    {
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var resolver = new StubExecutionResolver();
        resolver.Add(executionId, projectId);
        var user = new StubCurrentUser
        {
            IsAuthenticated = true,
            ExternalIdentityId = "u",
            Roles = ["tester"],
            Permissions = RolePermissions.Resolve(["tester"]),
        };
        var authorizer = new ExecutionSubscriptionAuthorizer(
            resolver, new AuthorizationService(user, new StubMembershipStore()));
        await Assert.ThrowsAsync<ForbiddenException>(
            () => authorizer.AuthorizeAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task Member_ReturnsProjectId()
    {
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var resolver = new StubExecutionResolver();
        resolver.Add(executionId, projectId);
        var store = new StubMembershipStore();
        store.Add("u", projectId);
        var user = new StubCurrentUser
        {
            IsAuthenticated = true,
            ExternalIdentityId = "u",
            Roles = ["tester"],
            Permissions = RolePermissions.Resolve(["tester"]),
        };
        var authorizer = new ExecutionSubscriptionAuthorizer(
            resolver, new AuthorizationService(user, store));
        Assert.Equal(projectId, await authorizer.AuthorizeAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyExecutionId_ThrowsArgument()
    {
        var authorizer = new ExecutionSubscriptionAuthorizer(
            new StubExecutionResolver(),
            new AuthorizationService(new StubCurrentUser { IsAuthenticated = true }, new StubMembershipStore()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => authorizer.AuthorizeAsync(Guid.Empty, CancellationToken.None));
    }
}
