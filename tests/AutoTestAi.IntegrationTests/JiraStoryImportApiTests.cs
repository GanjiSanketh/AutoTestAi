using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 5: transient Jira story import — auth matrix, project
/// isolation, validation, Jira error mapping, ADF handling, secret hygiene,
/// generation delegation, provenance, lifecycle, and repeat-import behavior.
/// Jira HTTP and AI generation are faked/stubbed; no live Jira tenant.
/// </summary>
public sealed class JiraStoryImportApiTests : IClassFixture<JiraStoryImportApiTests.ImportFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public sealed class FakeJiraIssues : IJiraTicketProvider
    {
        public int CreateCalls;
        public int GetCalls;
        public JiraIssueRequest? LastIssueRequest;
        public string? LastEmail;
        public string? LastToken;
        public Func<JiraIssueRequest, Task<JiraIssueDto>>? GetHandler;

        public Task<JiraCreateResult> CreateIssueAsync(JiraCreateRequest request, string email, string apiToken, CancellationToken ct)
        {
            CreateCalls++;
            throw new NotImplementedException("Import tests never create Jira issues.");
        }

        public Task<JiraIssueDto> GetIssueAsync(JiraIssueRequest request, string email, string apiToken, CancellationToken ct)
        {
            GetCalls++;
            LastIssueRequest = request;
            LastEmail = email;
            LastToken = apiToken;
            if (GetHandler is not null) return GetHandler(request);
            return Task.FromResult(DefaultIssue());
        }

        public static JiraIssueDto DefaultIssue() => new(
            "ABC-123",
            "Guest checkout",
            """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Allow guests to check out."}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Guest can place an order"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Order confirmation is shown"}]}]}]}]}""",
            "Story",
            "ABC");
    }

    public sealed class ImportFactory : Slice1ApiFactory
    {
        public FakeJiraIssues Jira { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJiraTicketProvider>();
                services.AddSingleton<IJiraTicketProvider>(Jira);
            });
        }
    }

    private readonly ImportFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly Guid _projectNoJira = Guid.NewGuid();
    private readonly Guid _projectDisabled = Guid.NewGuid();
    private readonly Guid _projectBudget = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public JiraStoryImportApiTests(ImportFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb, pc, pd, pe) = (_projectA, _projectB, _projectNoJira, _projectDisabled, _projectBudget);
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
                var mgr = new User { ExternalIdentityId = "ji-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "ji-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "ji-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "ji-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Jira Import Alpha", Key = "JIA" },
                    new Project { Id = pb, Name = "Jira Import Beta", Key = "JIB" },
                    new Project { Id = pc, Name = "Jira Import NoJira", Key = "JIC" },
                    new Project { Id = pd, Name = "Jira Import Disabled", Key = "JID" },
                    new Project { Id = pe, Name = "Jira Import Budget", Key = "JIE" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId },
                    new ProjectMember { ProjectId = pc, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pd, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pe, UserId = tester.Id, RoleId = TesterRoleId });
                db.Integrations.Add(new Integration
                {
                    ProjectId = pa, Provider = "jira", IntegrationType = "ticketing",
                    Configuration = JsonDocument.Parse(
                        """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
                    SecretReference = "integration-secret-xyz",
                    Status = IntegrationStatus.Active,
                });
                db.Integrations.Add(new Integration
                {
                    ProjectId = pd, Provider = "jira", IntegrationType = "ticketing",
                    Configuration = JsonDocument.Parse(
                        """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
                    SecretReference = "integration-secret-xyz",
                    Status = IntegrationStatus.Disabled,
                });
                db.Integrations.Add(new Integration
                {
                    ProjectId = pe, Provider = "jira", IntegrationType = "ticketing",
                    Configuration = JsonDocument.Parse(
                        """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
                    SecretReference = "integration-secret-xyz",
                    Status = IntegrationStatus.Active,
                });
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

    private static object ValidBody(string issueKey = "ABC-123", int? maxProposals = 2) => new
    {
        issueKey,
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
    private sealed record ImportPayload(
        Guid GenerationId, string PromptVersion, int ProposalCount, int SuccessCount, int FailureCount,
        IReadOnlyList<ProposalPayload> Proposals);
    private sealed record CreatedPayload(Guid Id, string TestKey, string SourceType, string LatestReviewStatus);
    private sealed record VersionPayload(Guid Id, int VersionNumber, string ReviewStatus);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    private void ResetFake() => _factory.Jira.GetHandler = null;

    // ---------- authorization ----------

    [Fact]
    public async Task Import_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        ResetFake();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client(null).PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Import_Viewer_Returns403()
    {
        await SeedOnceAsync();
        ResetFake();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("ji-viewer", ["viewer"]).PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Import_CrossProject_Returns403()
    {
        await SeedOnceAsync();
        ResetFake();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("ji-tester", ["tester"]).PostAsJsonAsync(
                $"/api/v1/projects/{_projectB}/story-test-generation-from-jira", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Import_TesterMember_Succeeds()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- validation ----------

    [Theory]
    [InlineData("nope")]
    [InlineData("PROJ123")]
    [InlineData("PROJ-")]
    [InlineData("-123")]
    [InlineData("https://jira.test/browse/ABC-123")]
    [InlineData("")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ-123456")]
    public async Task Import_InvalidKey_Returns400(string issueKey)
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(issueKey));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("VALIDATION_ERROR", body?.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task Import_MaxProposals_OutOfRange_Returns400(int maxProposals)
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: maxProposals));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_InvalidOverride_Returns400()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", new
            {
                issueKey = "ABC-123",
                framework = "playwright",
                platform = "web",
                targetUrl = "not-a-url",
                maxProposals = 2,
            });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Jira integration / errors ----------

    [Fact]
    public async Task Import_MissingIntegration_Returns409()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectNoJira}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Import_DisabledIntegration_Returns409()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectDisabled}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Import_Jira404_Returns404()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw new JiraProviderException(JiraErrorKind.NotFound, "gone");
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Import_Jira401_Returns502Auth()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw JiraProviderException.Authentication("bad creds");
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("JIRA_AUTH_FAILED", (await response.Content.ReadFromJsonAsync<ErrorPayload>())?.Error.Code);
        ResetFake();
    }

    [Fact]
    public async Task Import_Jira403_Returns502Forbidden()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw JiraProviderException.Permission("denied");
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("JIRA_FORBIDDEN", (await response.Content.ReadFromJsonAsync<ErrorPayload>())?.Error.Code);
        ResetFake();
    }

    [Fact]
    public async Task Import_Jira429_Returns429()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw new JiraProviderException(JiraErrorKind.RateLimited, "slow down");
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Import_Jira500_Returns502()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw JiraProviderException.Unavailable("down");
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Import_JiraTimeout_Returns503()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw JiraProviderException.Timeout("slow");
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Import_MalformedResponse_Returns502()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw new JiraProviderException(JiraErrorKind.MalformedResponse, "bad shape");
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Import_ProjectMismatch_ReturnsOpaque404()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(FakeJiraIssues.DefaultIssue() with { ProjectKey = "OTHER" });
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("OTHER", raw, StringComparison.Ordinal);
        ResetFake();
    }

    // ---------- ADF / security ----------

    [Fact]
    public async Task Import_UnsupportedAdfNodes_Excluded_FromProvenance()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => Task.FromResult(FakeJiraIssues.DefaultIssue() with
        {
            DescriptionAdfJson =
                """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Kept paragraph"}]},{"type":"media","attrs":{"id":"att-1"},"content":[{"type":"text","text":"DROP-ME-MEDIA"}]}]}""",
        });
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportPayload>();
        var provenance = result!.Proposals.Single().Provenance!.Value.GetRawText();
        Assert.DoesNotContain("DROP-ME-MEDIA", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("bulletList", provenance, StringComparison.Ordinal);
        ResetFake();
    }

    [Fact]
    public async Task Import_NeverLeaksSecrets_InResponseOrAudit()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("integration-secret-xyz", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("qa@example.com", raw, StringComparison.Ordinal);

        await _factory.SeedAsync(async db =>
        {
            var events = await db.AuditEvents
                .Where(e => e.ProjectId == _projectA && e.Action.StartsWith("jira-story-import"))
                .ToListAsync();
            Assert.Contains(events, e => e.Action == "jira-story-import.requested");
            Assert.Contains(events, e => e.Action == "jira-story-import.completed");
            foreach (var meta in events.Select(e => e.MetadataJson))
            {
                Assert.DoesNotContain("integration-secret-xyz", meta ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("qa@example.com", meta ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("Guest can place an order", meta ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("bulletList", meta ?? string.Empty, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task Import_ClientCannotOverride_JiraTarget()
    {
        await SeedOnceAsync();
        ResetFake();
        var before = _factory.Jira.GetCalls;
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", new
            {
                issueKey = "abc-123",
                framework = "playwright",
                platform = "web",
                maxProposals = 1,
                baseUrl = "https://evil.test",
                projectKey = "EVIL",
                integrationId = Guid.NewGuid(),
                apiToken = "attacker-token",
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 1, _factory.Jira.GetCalls);
        Assert.Equal("https://jira.test", _factory.Jira.LastIssueRequest?.BaseUrl);
        Assert.Equal("ABC-123", _factory.Jira.LastIssueRequest?.IssueKey);
        var result = await response.Content.ReadFromJsonAsync<ImportPayload>();
        var provenance = result!.Proposals.Single().Provenance!.Value.GetRawText();
        Assert.Contains("jira.test", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", provenance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attacker-token", provenance, StringComparison.Ordinal);
    }

    // ---------- generation ----------

    [Fact]
    public async Task Import_Delegates_ToStoryGeneration_WithMaxProposals()
    {
        await SeedOnceAsync();
        ResetFake();
        var before = _factory.Jira.GetCalls;
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: 3));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportPayload>();
        Assert.Equal("story-to-tests-v1", result!.PromptVersion);
        Assert.Equal(3, result.ProposalCount);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.All(result.Proposals, p => Assert.Equal("Succeeded", p.Status));
        Assert.Equal(before + 1, _factory.Jira.GetCalls);
    }

    [Fact]
    public async Task Import_JiraGet_DoesNotConsume_AiBudget()
    {
        await SeedOnceAsync();
        ResetFake();
        // Dedicated project keeps this budget proof independent of test order:
        // 10 import AI calls + 10 manual AI calls = 20 (the per-minute budget).
        // Both fully succeed only if the Jira GET consumed none of it.
        var client = Client("ji-tester", ["tester"]);
        var imported = await (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectBudget}/story-test-generation-from-jira", ValidBody(maxProposals: 10)))
            .Content.ReadFromJsonAsync<ImportPayload>();
        Assert.Equal(10, imported!.SuccessCount);
        // 10 AI calls spent; the shared 20/min budget still has room for 10
        // more only if the Jira GET did not consume it.
        var manual = await (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectBudget}/story-test-generation", new
                {
                    storyTitle = "Budget check",
                    acceptanceCriteria = new[] { "Something works" },
                    framework = "playwright",
                    platform = "web",
                    maxProposals = 10,
                }))
            .Content.ReadFromJsonAsync<ImportPayload>();
        Assert.Equal(10, manual!.SuccessCount);
    }

    // ---------- provenance ----------

    [Fact]
    public async Task Import_Provenance_ContainsSafeJiraMetadata()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await Client("ji-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: 1));
        var result = await response.Content.ReadFromJsonAsync<ImportPayload>();
        var provenance = result!.Proposals.Single().Provenance!.Value.GetRawText();
        Assert.Contains("jira-import", provenance, StringComparison.Ordinal);
        Assert.Contains("ABC-123", provenance, StringComparison.Ordinal);
        Assert.Contains("\"jiraIssueType\":\"Story\"", provenance, StringComparison.Ordinal);
        Assert.Contains("\"jiraBaseUrlHost\":\"jira.test\"", provenance, StringComparison.Ordinal);
        Assert.Contains("jiraFetchedAt", provenance, StringComparison.Ordinal);
        Assert.Contains("story-to-tests-v1", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("integration-secret-xyz", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("qa@example.com", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("https://jira.test/rest", provenance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_JiraProposal_PersistsProvenance_ThroughExistingPath()
    {
        await SeedOnceAsync();
        ResetFake();
        var client = Client("ji-tester", ["tester"]);
        var generated = await (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: 1)))
            .Content.ReadFromJsonAsync<ImportPayload>();
        var proposal = generated!.Proposals.Single();

        var key = $"AI-JIRA-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
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

        await _factory.SeedAsync(async db =>
        {
            var version = await db.TestCaseVersions.FirstAsync(v => v.TestCaseId == created.Id);
            var stored = version.GenerationRequest?.RootElement.GetRawText() ?? string.Empty;
            Assert.Contains("ABC-123", stored, StringComparison.Ordinal);
            Assert.Contains("jira-import", stored, StringComparison.Ordinal);
        });
    }

    // ---------- lifecycle ----------

    [Fact]
    public async Task Save_JiraProposal_PendingGate_And_Approval_Unchanged()
    {
        await SeedOnceAsync();
        ResetFake();
        var client = Client("ji-manager", ["qa-lead"]);
        var generated = await (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: 1)))
            .Content.ReadFromJsonAsync<ImportPayload>();
        var proposal = generated!.Proposals.Single();

        var key = $"AI-JIRB-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var save = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-cases", new
            {
                testKey = key,
                title = proposal.Title,
                framework = proposal.Framework,
                platform = proposal.Platform,
                sourceType = "ai",
                sourceCode = proposal.SourceCode,
                structuredSteps = proposal.StructuredSteps,
                generationRequest = proposal.Provenance,
            });
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var created = await save.Content.ReadFromJsonAsync<CreatedPayload>();

        var versions = await (await client.GetAsync($"/api/v1/test-cases/{created!.Id}/versions"))
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
    public async Task RepeatImport_ProducesIndependentResults_WithoutPersisting()
    {
        await SeedOnceAsync();
        ResetFake();
        var client = Client("ji-tester", ["tester"]);
        var before = 0;
        await _factory.SeedAsync(async db =>
            before = await db.TestCases.CountAsync(t => t.ProjectId == _projectA));
        var first = await (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: 1)))
            .Content.ReadFromJsonAsync<ImportPayload>();
        var second = await (await client.PostAsJsonAsync(
                $"/api/v1/projects/{_projectA}/story-test-generation-from-jira", ValidBody(maxProposals: 1)))
            .Content.ReadFromJsonAsync<ImportPayload>();
        Assert.NotEqual(first!.GenerationId, second!.GenerationId);

        await _factory.SeedAsync(async db =>
            Assert.Equal(before, await db.TestCases.CountAsync(t => t.ProjectId == _projectA)));
    }
}
