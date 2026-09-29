using System.Net;
using System.Net.Http.Json;
using AutoTestAi.IntegrationTests;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Proves the API starts with NO external dependencies configured
/// and serves the Phase-0 health surface.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<Slice1ApiFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(Slice1ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ApiHealth_ReturnsHealthy_WithDependencyStates()
    {
        var response = await _client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiHealthPayload>();
        Assert.NotNull(body);
        Assert.Equal("healthy", body!.Status);
        Assert.NotEmpty(body.Dependencies);
        Assert.Contains(body.Dependencies, d => d.Name == "postgres");
        Assert.Contains(body.Dependencies, d => d.Name == "temporal");
    }

    [Fact]
    public async Task Liveness_ReturnsAlive()
    {
        var response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record DependencyPayload(string Name, string Status, string? Detail);
    private sealed record ApiHealthPayload(
        string Status,
        string Version,
        DateTimeOffset Timestamp,
        IReadOnlyList<DependencyPayload> Dependencies);
}
