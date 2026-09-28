using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 4: AI test generation endpoint — auth matrix, project scoping,
/// TestCaseVersion integration (sourceType=ai, Pending, metadata, redaction),
/// audit events, validation, provider errors, and secret hygiene (docs/06 §7).
/// Uses the stub provider; live providers are covered by unit tests with
/// mocked HTTP.
/// </summary>
public sealed class TestGenerationApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public TestGenerationApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }

                var mgr = new User { ExternalIdentityId = "gen-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "gen-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "gen-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "gen-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);

                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Gen Alpha", Key = "GENA" },
                    new Project { Id = pb, Name = "Gen Beta", Key = "GENB" });

                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });
                return Task.CompletedTask;
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(string? sub, string[]? roles = null)
    {
        var client = _factory.CreateClient();
        if (sub is not null)
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles ?? ["tester"]));
        return client;
    }

    private static object ValidBody() => new
    {
        title = "Successful user login",
        description = "Verify that a registered user can log in.",
        targetUrl = "https://example.test/login",
        framework = "playwright",
        platform = "web",
        module = "Authentication",
        priority = "High",
        requirements = new[] { "Validate successful login", "Validate dashboard navigation" },
    };

    private sealed record GenerationPayload(
        Guid GenerationId, Guid TestCaseId, string TestKey, Guid VersionId, int VersionNumber,
        string Status, string Title, string Framework, string Platform,
        IReadOnlyList<StepPayload> StructuredSteps, string SourceCode,
        IReadOnlyList<string> Assumptions, IReadOnlyList<string> Warnings,
        string Provider, string? Model, string PromptVersion, long LatencyMs, string ReviewStatus);
    private sealed record StepPayload(int Order, string Action, string? Target, string? Value);
    private sealed record CaseDetails(Guid Id, string TestKey, string? SourceType, int LatestVersionNumber, string LatestReviewStatus);
    private sealed record VersionPayload(Guid Id, int VersionNumber, string? SourceCode, string? GenerationProvider, string? GenerationModel, long? GenerationLatencyMs, string ReviewStatus);
    private sealed record StatusPayload(string Provider, string? Model, bool Configured, string? Detail, string PromptVersion);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    [Fact]
    public async Task Generate_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        var response = await Client(null).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Generate_OutsiderAndViewer_Return403()
    {
        await SeedOnceAsync();

        var outsider = await Client("gen-outsider").PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);

        var viewer = _factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("gen-viewer", ["viewer"]));
        var denied = await viewer.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await denied.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("FORBIDDEN", body?.Error.Code);
    }

    [Fact]
    public async Task Generate_ForOtherProject_Returns403()
    {
        await SeedOnceAsync();
        var client = Client("gen-manager", ["qa-lead"]);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectB}/test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Generate_AsManager_Succeeds_PersistsPendingAiVersion()
    {
        await SeedOnceAsync();
        var client = Client("gen-manager", ["qa-lead"]);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var generated = await response.Content.ReadFromJsonAsync<GenerationPayload>();
        Assert.NotNull(generated);
        Assert.Equal("Succeeded", generated!.Status);
        Assert.Equal("stub", generated.Provider);
        Assert.Equal("stub-1.0", generated.Model);
        Assert.Equal("test-generation-v1", generated.PromptVersion);
        Assert.Equal("Pending", generated.ReviewStatus);
        Assert.Equal(1, generated.VersionNumber);
        Assert.Equal(2, generated.StructuredSteps.Count);
        Assert.Contains("@playwright/test", generated.SourceCode, StringComparison.Ordinal);
        Assert.NotEmpty(generated.Assumptions);
        Assert.True(generated.LatencyMs >= 0);
        Assert.StartsWith("AI-", generated.TestKey, StringComparison.Ordinal);

        // TestCaseService integration: sourceType=ai, version 1, Pending.
        var details = await client.GetFromJsonAsync<CaseDetails>($"/api/v1/test-cases/{generated.TestCaseId}");
        Assert.Equal("ai", details!.SourceType);
        Assert.Equal(1, details.LatestVersionNumber);
        Assert.Equal("Pending", details.LatestReviewStatus);

        var versions = await client.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{generated.TestCaseId}/versions");
        var v1 = Assert.Single(versions!);
        Assert.Equal("stub", v1.GenerationProvider);
        Assert.Equal("stub-1.0", v1.GenerationModel);
        Assert.NotNull(v1.GenerationLatencyMs);

        // Audit events recorded with safe metadata.
        await _factory.SeedAsync(async db =>
        {
            var audits = await db.AuditEvents
                .Where(e => e.Action.StartsWith("test-generation", StringComparison.Ordinal))
                .ToListAsync();
            Assert.Contains(audits, e => e.Action == "test-generation.requested");
            Assert.Contains(audits, e => e.Action == "test-generation.completed");
            foreach (var audit in audits)
                Assert.DoesNotContain("sk-", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Generate_RedactsSecrets_BeforePersistence()
    {
        await SeedOnceAsync();
        var client = Client("gen-manager", ["qa-lead"]);
        const string secret = "super-secret-value-xyz";

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", new
            {
                title = "Secret handling check",
                framework = "playwright",
                platform = "web",
                requirements = new[] { "Validate login" },
                additionalContext = $$"""{"password": "{{secret}}"}""",
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var generated = await response.Content.ReadFromJsonAsync<GenerationPayload>();
        Assert.NotNull(generated);

        await _factory.SeedAsync(async db =>
        {
            var version = await db.TestCaseVersions.FirstAsync(v => v.TestCaseId == generated!.TestCaseId);
            var stored = version.GenerationRequest?.RootElement.GetRawText() ?? string.Empty;
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", stored, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Generate_Response_NeverContainsServerSecrets()
    {
        await SeedOnceAsync();
        var client = Client("gen-manager", ["qa-lead"]);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", ValidBody());
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Provider credentials, internal prompts, and raw payloads never reach the client.
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemPrompt", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_InvalidBody_Returns400_WithFieldDetails()
    {
        await SeedOnceAsync();
        var client = Client("gen-manager", ["qa-lead"]);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", new
            {
                title = "",
                framework = "",
                platform = "web",
                targetUrl = "not-a-url",
                priority = "Bogus",
                requirements = Array.Empty<string>(),
            });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var fields = doc!.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToHashSet();
        Assert.Contains("title", fields);
        Assert.Contains("framework", fields);
        Assert.Contains("targetUrl", fields);
        Assert.Contains("priority", fields);
    }

    [Fact]
    public async Task ProviderStatus_ReturnsSafeMetadata_Only()
    {
        await SeedOnceAsync();
        var client = Client("gen-tester", ["tester"]);

        var response = await client.GetAsync($"/api/v1/projects/{_projectA}/ai-provider-status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<StatusPayload>();
        Assert.Equal("stub", status!.Provider);
        Assert.True(status.Configured);
        Assert.Equal("test-generation-v1", status.PromptVersion);

        var raw = await client.GetStringAsync($"/api/v1/projects/{_projectA}/ai-provider-status");
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baseUrl", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderStatus_Anonymous_Returns401_And_Outsider_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client(null).GetAsync($"/api/v1/projects/{_projectA}/ai-provider-status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("gen-outsider").GetAsync($"/api/v1/projects/{_projectA}/ai-provider-status")).StatusCode);
    }

    [Fact]
    public async Task Generate_UnsupportedProvider_ReturnsControlledError()
    {
        await SeedOnceAsync();
        using var custom = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<AiOptions>(o => o.Provider = "bogus")));
        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("gen-manager", ["qa-lead"]));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("PROVIDER_NOT_SUPPORTED", body?.Error.Code);
    }

    [Fact]
    public async Task Generate_OpenAiWithoutKey_ReturnsProviderNotConfigured()
    {
        await SeedOnceAsync();
        using var custom = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<AiOptions>(o =>
                {
                    o.Provider = "openai";
                    o.Model = "gpt-4o-mini";
                    o.ApiKey = "";
                })));
        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("gen-manager", ["qa-lead"]));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("PROVIDER_NOT_CONFIGURED", body?.Error.Code);
    }
}
