using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AutoTestAi.IntegrationTests;

/// <summary>STEP 14: /auth/me behavior — 401 anonymous, 200 authenticated.</summary>
public sealed class AuthEndpointTests : IClassFixture<Slice1ApiFactory>
{
    private readonly Slice1ApiFactory _factory;

    public AuthEndpointTests(Slice1ApiFactory factory) => _factory = factory;

    private static HttpClient AnonymousClient(Slice1ApiFactory factory) => factory.CreateClient();

    private static HttpClient AuthenticatedClient(Slice1ApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var response = await AnonymousClient(_factory).GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_Returns200_WithRolesPermissions_AndProvisionsUser()
    {
        var token = TestTokens.Create("slice1-tester-1", ["tester"], "tester1@example.com");
        var response = await AuthenticatedClient(_factory, token).GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserProfilePayload>();
        Assert.NotNull(body);
        Assert.Equal("slice1-tester-1", body!.ExternalIdentityId);
        Assert.Equal("tester1@example.com", body.Email);
        Assert.Contains("tester", body.Roles);
        Assert.Contains("executions.execute", body.Permissions);
        Assert.NotNull(body.Id);

        // JIT provisioning keyed on the external identity id.
        var user = await _factory.FindUserAsync("slice1-tester-1");
        Assert.NotNull(user);
        Assert.Equal(body.Id, user!.Id);
        Assert.Equal("tester1@example.com", user.Email);
    }

    [Fact]
    public async Task Me_WithExpiredToken_Returns401()
    {
        var token = TestTokens.Create(
            "slice1-expired", ["viewer"], expires: DateTime.UtcNow.AddMinutes(-5));
        var response = await AuthenticatedClient(_factory, token).GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithWrongSignature_Returns401()
    {
        var token = TestTokens.WithWrongSignature("slice1-forged", ["admin"]);
        var response = await AuthenticatedClient(_factory, token).GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_StaysAnonymous()
    {
        var response = await AnonymousClient(_factory).GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record UserProfilePayload(
        Guid? Id,
        string? ExternalIdentityId,
        string? Email,
        string? DisplayName,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions);
}
