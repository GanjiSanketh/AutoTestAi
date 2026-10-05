using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Domain.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 3: manual user-story to test proposals — auth matrix,
/// project isolation, sequential fan-out, partial failure, Pending-only
/// save through existing TestCase creation, review/approval reuse,
/// execution-gate preservation, audit, and secret hygiene.
/// Uses the stub provider.
/// </summary>
public sealed class StoryTestGenerationApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public StoryTestGenerationApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(db =>
            {
                if (db.Projects.Any(p => p.Id == pa)) return Task.CompletedTask;
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }

                var mgr = new User { ExternalIdentityId = "story-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "story-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "story-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "story-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);

                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Story Alpha", Key = "STA" },
                    new Project { Id = pb, Name = "Story Beta", Key = "STB" });

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

    private static object ValidBody(int? maxProposals = 2) => new
    {
        storyTitle = "Guest checkout",
        storyDescription = "Allow guests to check out without an account.",
        acceptanceCriteria = new[] { "Guest can place an order", "Order confirmation is shown" },
        targetUrl = "https://example.test/checkout",
        framework = "playwright",
        platform = "web",
        module = "Checkout",
        priority = "High",
        maxProposals,
    };

    private sealed record StepPayload(int Order, string Action, string? Target, string? Value);
    private sealed record ProposalPayload(
        string ProposalId, int Index, string Status,
        string? Title, string? Description, string? Framework, string? Platform,
        string? FocusCriterion, int FocusCriterionIndex,
        IReadOnlyList<StepPayload> StructuredSteps, string? SourceCode,
        IReadOnlyList<string> Assumptions, IReadOnlyList<string> Warnings,
        string? Provider, string? Model, string PromptVersion, long LatencyMs,
        JsonElement? Provenance, string? ErrorCode, string? ErrorMessage);
    private sealed record StoryPayload(
        Guid GenerationId, string PromptVersion, int ProposalCount, int SuccessCount, int FailureCount,
        IReadOnlyList<ProposalPayload> Proposals);
    private sealed record CreatedPayload(Guid Id, string TestKey, string SourceType, string LatestReviewStatus);
    private sealed record VersionPayload(Guid Id, int VersionNumber, string ReviewStatus);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    [Fact]
    public async Task Story_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client(null).PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Story_Outsider_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("story-outsider").PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Story_CrossProject_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("story-manager", ["qa-lead"]).PostAsJsonAsync(
                $"/api/v1/projects/{_projectB}/story-test-generation", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Story_ViewerWithoutManage_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("story-viewer", ["viewer"]).PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Story_Generates_Proposals_WithStoryContract()
    {
        await SeedOnceAsync();
        var response = await Client("story-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<StoryPayload>();
        Assert.NotNull(result);
        Assert.Equal("story-to-tests-v1", result!.PromptVersion);
        Assert.Equal(2, result.ProposalCount);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(new[] { 1, 2 }, result.Proposals.Select(p => p.Index).ToArray());
        Assert.Equal(new[] { 0, 1 }, result.Proposals.Select(p => p.FocusCriterionIndex).ToArray());
        Assert.All(result.Proposals, p => Assert.Equal("Succeeded", p.Status));
        Assert.All(result.Proposals, p => Assert.False(string.IsNullOrWhiteSpace(p.ProposalId)));
        Assert.NotEqual(result.Proposals[0].ProposalId, result.Proposals[1].ProposalId);
        Assert.All(result.Proposals, p => Assert.NotEmpty(p.StructuredSteps));
        Assert.All(result.Proposals, p => Assert.NotNull(p.Provenance));
        // Nothing is persisted by generation: proposals are previews only.
        await _factory.SeedAsync(db =>
        {
            Assert.Empty(db.TestCases.Where(t => t.ProjectId == _projectA));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Story_Defaults_To10_Proposals()
    {
        await SeedOnceAsync();
        var response = await Client("story-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation",
            ValidBody(null));
        var result = await response.Content.ReadFromJsonAsync<StoryPayload>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, result!.ProposalCount);
        Assert.Equal(10, result.SuccessCount);
    }

    [Fact]
    public async Task Story_RateLimit_YieldsPartialResults()
    {
        await SeedOnceAsync();
        using var custom = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<AiOptions>(o => o.MaxGenerationsPerMinutePerProject = 1)));
        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("story-manager", ["qa-lead"]));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody(3));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<StoryPayload>();
        Assert.Equal(1, result!.SuccessCount);
        Assert.Equal(2, result.FailureCount);
        Assert.All(result.Proposals.Where(p => p.Status == "Failed"),
            p => Assert.Equal("RATE_LIMITED", p.ErrorCode));
    }

    [Fact]
    public async Task Story_MaxProposals_Bounds_Rejected()
    {
        await SeedOnceAsync();
        var client = Client("story-manager", ["qa-lead"]);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody(11))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody(0))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody(1))).StatusCode);
    }

    [Fact]
    public async Task Story_EmptyCriteria_Rejected()
    {
        await SeedOnceAsync();
        var response = await Client("story-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation", new
            {
                storyTitle = "Guest checkout",
                acceptanceCriteria = Array.Empty<string>(),
                framework = "playwright",
                platform = "web",
            });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Story_UnsupportedProvider_ReturnsControlledError()
    {
        await SeedOnceAsync();
        using var custom = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<AiOptions>(o => o.Provider = "bogus")));
        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("story-manager", ["qa-lead"]));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("PROVIDER_NOT_SUPPORTED", body?.Error.Code);
    }

    [Fact]
    public async Task Save_Proposal_Creates_Pending_Ai_TestCase_ThroughExistingPath()
    {
        await SeedOnceAsync();
        var client = Client("story-manager", ["qa-lead"]);
        var generated = await (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation", ValidBody(1)))
            .Content.ReadFromJsonAsync<StoryPayload>();
        var proposal = generated!.Proposals.Single();

        var key = $"AI-STORY-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var save = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-cases", new
            {
                testKey = key,
                title = proposal.Title,
                description = proposal.Description,
                framework = proposal.Framework,
                platform = proposal.Platform,
                priority = "High",
                sourceType = "ai",
                sourceCode = proposal.SourceCode,
                structuredSteps = proposal.StructuredSteps,
                generationProvider = proposal.Provider,
                generationModel = proposal.Model,
                generationLatencyMs = proposal.LatencyMs,
                generationRequest = proposal.Provenance,
            });
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var created = await save.Content.ReadFromJsonAsync<CreatedPayload>();
        Assert.Equal("ai", created!.SourceType);
        Assert.Equal("Pending", created.LatestReviewStatus);

        // Existing review approves; the Pending gate still applies before that.
        var versions = await (await client.GetAsync($"/api/v1/test-cases/{created.Id}/versions"))
            .Content.ReadFromJsonAsync<List<VersionPayload>>();
        var versionId = versions!.Single().Id;
        var gated = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { testCaseVersionId = versionId });
        Assert.Equal(HttpStatusCode.Conflict, gated.StatusCode);

        var reviewed = await client.PostAsJsonAsync(
            $"/api/v1/test-cases/{created.Id}/review",
            new { versionId, reviewStatus = "Approved" });
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
    }

    [Fact]
    public async Task Story_Audits_WithoutStoryText_And_NoSecretLeakage()
    {
        await SeedOnceAsync();
        const string secret = "sk-live-xyz-123";
        var response = await Client("story-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation", new
            {
                storyTitle = "Guest checkout",
                storyDescription = $"Seed with api-key={secret} for tests",
                acceptanceCriteria = new[] { "Guest can place an order" },
                framework = "playwright",
                platform = "web",
                maxProposals = 1,
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<StoryPayload>();
        Assert.Equal(1, result!.SuccessCount);
        // User input is echoed in the preview (same as generic generation);
        // redaction applies to persisted provenance and audit metadata.
        var provenance = result.Proposals.Single().Provenance!.Value.GetRawText();
        Assert.DoesNotContain(secret, provenance, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", provenance, StringComparison.Ordinal);

        await _factory.SeedAsync(async db =>
        {
            var events = await db.AuditEvents
                .Where(e => e.ProjectId == _projectA && e.Action.StartsWith("story-generation"))
                .ToListAsync();
            Assert.Contains(events, e => e.Action == "story-generation.requested");
            Assert.Contains(events, e => e.Action == "story-generation.completed");
            foreach (var meta in events.Select(e => e.MetadataJson))
            {
                Assert.DoesNotContain(secret, meta ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("Guest checkout", meta ?? string.Empty, StringComparison.Ordinal);
            }
        });
    }
}
