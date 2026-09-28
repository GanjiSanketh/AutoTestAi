using System.Security.Claims;
using AutoTestAi.Application.Identity;

namespace AutoTestAi.UnitTests;

public sealed class CurrentUserFactoryTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void AnonymousPrincipal_IsNotAuthenticated()
    {
        var snapshot = CurrentUserFactory.FromPrincipal(new ClaimsPrincipal());
        Assert.False(snapshot.IsAuthenticated);
        Assert.Empty(snapshot.Roles);
        Assert.Empty(snapshot.Permissions);
    }

    [Fact]
    public void KeycloakPrincipal_MapsSubEmailName_AndRealmRoles()
    {
        var principal = Principal(
            new Claim("sub", "kc-sub-1"),
            new Claim("email", "qa@example.com"),
            new Claim("name", "QA Engineer"),
            new Claim("realm_access", """{"roles":["tester"]}"""));

        var snapshot = CurrentUserFactory.FromPrincipal(principal);

        Assert.True(snapshot.IsAuthenticated);
        Assert.Equal("kc-sub-1", snapshot.ExternalIdentityId);
        Assert.Equal("qa@example.com", snapshot.Email);
        Assert.Equal("QA Engineer", snapshot.DisplayName);
        Assert.Contains("tester", snapshot.Roles);
        Assert.Contains("testcases.manage", snapshot.Permissions);
    }

    [Fact]
    public void ResourceAccessRoles_AreIncluded()
    {
        var principal = Principal(
            new Claim("sub", "kc-sub-2"),
            new Claim("resource_access", """{"autotestai-web":{"roles":["viewer"]}}"""));

        var snapshot = CurrentUserFactory.FromPrincipal(principal);

        Assert.Contains("viewer", snapshot.Roles);
        Assert.Contains("projects.read", snapshot.Permissions);
        Assert.DoesNotContain("projects.manage", snapshot.Permissions);
    }

    [Fact]
    public void MalformedRolePayload_DoesNotFailAuthentication()
    {
        var principal = Principal(
            new Claim("sub", "kc-sub-3"),
            new Claim("realm_access", "not-json{{{"),
            new Claim("role", "qa-lead"));

        var snapshot = CurrentUserFactory.FromPrincipal(principal);

        Assert.True(snapshot.IsAuthenticated);
        Assert.Contains("qa-lead", snapshot.Roles);
    }

    [Fact]
    public void EmailUsedAsDisplayNameFallback_NeverAsIdentityKey()
    {
        var principal = Principal(
            new Claim("sub", "kc-sub-4"),
            new Claim("email", "dev@example.com"));

        var snapshot = CurrentUserFactory.FromPrincipal(principal);

        Assert.Equal("kc-sub-4", snapshot.ExternalIdentityId);
        Assert.Equal("dev@example.com", snapshot.DisplayName);
    }
}
