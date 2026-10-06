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
/// Phase 4 Slice 7: Jira freshness check — auth, baseline gating, field
/// comparison, provider reuse, rate limiting, isolation, audit safety, and
/// version immutability. Jira HTTP is faked; AI is never called.
/// </summary>
public sealed class JiraChangeCheckApiTests : IClassFixture<JiraChangeCheckApiTests.CheckFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public sealed class FakeJiraIssues : IJiraTicketProvider
    {
        public int GetCalls;
        public JiraIssueRequest? LastIssueRequest;
        public Func<JiraIssueRequest, Task<JiraIssueDto>>? GetHandler;

        public Task<JiraCreateResult> CreateIssueAsync(JiraCreateRequest request, string email, string apiToken, CancellationToken ct)
            => throw new NotImplementedException("Check tests never create Jira issues.");

        public Task<JiraIssueDto> GetIssueAsync(JiraIssueRequest request, string email, string apiToken, CancellationToken ct)
        {
            GetCalls++;
            LastIssueRequest = request;
            if (GetHandler is not null) return GetHandler(request);
            return Task.FromResult(DefaultIssue());
        }

        public static JiraIssueDto DefaultIssue() => new(
            "ABC-123",
            "Guest checkout",
            """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Server is up."}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Pay now"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Receipt shown"}]}]}]}]}""",
            "Story",
            "ABC");
    }

    public sealed class CheckFactory : Slice1ApiFactory
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

    private readonly CheckFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly Guid _projectDisabled = Guid.NewGuid();
    private readonly Guid _projectRate = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    private Guid _caseId;
    private Guid _versionId;
    private Guid _legacyCaseId;
    private Guid _legacyVersionId;
    private Guid _manualCaseId;
    private Guid _manualVersionId;
    private Guid _malformedCaseId;
    private Guid _malformedVersionId;
    private Guid _otherCaseId;
    private Guid _bCaseId;
    private Guid _bVersionId;
    private Guid _rateCaseId;
    private Guid _rateVersionId;

    public JiraChangeCheckApiTests(CheckFactory factory) => _factory = factory;

    private static JsonDocument StoredJira(
        string key = "ABC-123",
        string? origin = "jira-import",
        string title = "Guest checkout",
        string? description = "Server is up.\n\nPay now\nReceipt shown",
        string[]? criteria = null,
        string? type = "Story",
        bool withNormalizerVersion = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["storyTitle"] = title,
            ["storyDescription"] = description,
            ["acceptanceCriteria"] = criteria ?? new[] { "Pay now", "Receipt shown" },
            ["focusCriterionIndex"] = 0,
            ["promptVersion"] = "story-to-tests-v1",
            ["source"] = "story-ai",
            ["origin"] = origin,
            ["jiraIssueKey"] = key,
            ["jiraIssueType"] = type,
            ["jiraBaseUrlHost"] = "company.atlassian.net",
            ["jiraFetchedAt"] = "2026-10-06T00:00:00Z",
            ["generationId"] = Guid.NewGuid().ToString(),
            ["proposalId"] = "opaque",
        };
        if (withNormalizerVersion)
            payload["normalizerVersion"] = "jira-story-normalizer-v1";
        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb, pd, pr) = (_projectA, _projectB, _projectDisabled, _projectRate);
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
                var mgr = new User { ExternalIdentityId = "jc-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "jc-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "jc-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "jc-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Check Alpha", Key = "CKA" },
                    new Project { Id = pb, Name = "Check Beta", Key = "CKB" },
                    new Project { Id = pd, Name = "Check Disabled", Key = "CKD" },
                    new Project { Id = pr, Name = "Check Rate", Key = "CKR" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pb, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId },
                    new ProjectMember { ProjectId = pd, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pr, UserId = tester.Id, RoleId = TesterRoleId });

                foreach (var (project, key) in new[] { (pa, "ABC"), (pb, "ABC"), (pr, "ABC") })
                {
                    db.Integrations.Add(new Integration
                    {
                        ProjectId = project, Provider = "jira", IntegrationType = "ticketing",
                        Configuration = JsonDocument.Parse(
                            """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
                        SecretReference = "integration-secret-xyz",
                        Status = IntegrationStatus.Active,
                    });
                }
                db.Integrations.Add(new Integration
                {
                    ProjectId = pd, Provider = "jira", IntegrationType = "ticketing",
                    Configuration = JsonDocument.Parse(
                        """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
                    SecretReference = "integration-secret-xyz",
                    Status = IntegrationStatus.Disabled,
                });

                Guid AddCase(Guid project, string key, JsonDocument? provenance)
                {
                    var testCase = new TestCase
                    {
                        ProjectId = project, TestKey = key, Title = $"Title {key}",
                        Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "ai",
                    };
                    db.TestCases.Add(testCase);
                    var version = new TestCaseVersion
                    {
                        TestCaseId = testCase.Id, VersionNumber = 1,
                        SourceCode = "test('x', async () => {});",
                        ReviewStatus = ReviewStatus.Approved, GenerationRequest = provenance,
                    };
                    db.TestCaseVersions.Add(version);
                    return testCase.Id;
                }

                Guid VersionOf(Guid testCaseId)
                    => db.TestCaseVersions.Local.First(v => v.TestCaseId == testCaseId).Id;

                _caseId = AddCase(pa, "CHECK-001", StoredJira());
                _versionId = VersionOf(_caseId);
                _legacyCaseId = AddCase(pa, "CHECK-LEG", StoredJira(withNormalizerVersion: false));
                _legacyVersionId = VersionOf(_legacyCaseId);
                _manualCaseId = AddCase(pa, "CHECK-MAN", null);
                _manualVersionId = VersionOf(_manualCaseId);
                _malformedCaseId = AddCase(pa, "CHECK-BAD", JsonDocument.Parse("[1,2]"));
                _malformedVersionId = VersionOf(_malformedCaseId);
                _otherCaseId = AddCase(pa, "CHECK-OTH", StoredJira());
                _bCaseId = AddCase(pb, "CHECK-B", StoredJira());
                _bVersionId = VersionOf(_bCaseId);
                _rateCaseId = AddCase(pr, "CHECK-R", StoredJira());
                _rateVersionId = VersionOf(_rateCaseId);
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

    private void ResetFake() => _factory.Jira.GetHandler = null;

    private sealed record CheckPayload(
        string Status, IReadOnlyList<string> ChangedFields, string JiraIssueKey, string CheckedAt);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    private Task<HttpResponseMessage> CheckAsync(HttpClient client, Guid testCaseId, Guid versionId, object? body = null)
        => body is null
            ? client.PostAsync($"/api/v1/test-cases/{testCaseId}/versions/{versionId}/jira-change-check", null)
            : client.PostAsJsonAsync($"/api/v1/test-cases/{testCaseId}/versions/{versionId}/jira-change-check", body);

    // ---------- authorization ----------

    [Fact]
    public async Task Check_TesterManage_Succeeds()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("current", result!.Status);
        Assert.Empty(result.ChangedFields);
        Assert.Equal("ABC-123", result.JiraIssueKey);
    }

    [Fact]
    public async Task Check_Viewer_Returns403()
    {
        await SeedOnceAsync();
        ResetFake();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await CheckAsync(Client("jc-viewer", ["viewer"]), _caseId, _versionId)).StatusCode);
    }

    [Fact]
    public async Task Check_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        ResetFake();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CheckAsync(Client(null), _caseId, _versionId)).StatusCode);
    }

    // ---------- comparison ----------

    [Fact]
    public async Task Check_ChangedTitle_ReturnsChangedTitle()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with { Summary = "Express checkout" });
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("changed", result!.Status);
        Assert.Equal(new[] { "title" }, result.ChangedFields);
        ResetFake();
    }

    [Fact]
    public async Task Check_ChangedDescription_ReturnsChangedDescription()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with
            {
                DescriptionAdfJson = """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Server is down."}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Pay now"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Receipt shown"}]}]}]}]}""",
            });
        var result = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("changed", result!.Status);
        // Only the paragraph changed; the list items (and hence criteria) match.
        Assert.Equal(new[] { "description" }, result.ChangedFields);
        ResetFake();
    }

    [Fact]
    public async Task Check_ChangedCriteria_ReturnsChangedCriteria()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with
            {
                DescriptionAdfJson = """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Server is up."}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Pay now"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Gift wrap offered"}]}]}]}]}""",
            });
        var result = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("changed", result!.Status);
        // List text is embedded in the normalized description, so a list edit
        // deterministically flags both fields.
        Assert.Equal(new[] { "description", "acceptanceCriteria" }, result.ChangedFields);
        ResetFake();
    }

    [Fact]
    public async Task Check_ReorderedCriteria_ReturnsChangedCriteria()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with
            {
                DescriptionAdfJson = """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Server is up."}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Receipt shown"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Pay now"}]}]}]}]}""",
            });
        var result = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("changed", result!.Status);
        Assert.Equal(new[] { "description", "acceptanceCriteria" }, result.ChangedFields);
        ResetFake();
    }

    [Fact]
    public async Task Check_ChangedIssueType_ReturnsChangedIssueType()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with { IssueTypeName = "Task" });
        var result = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("changed", result!.Status);
        Assert.Equal(new[] { "issueType" }, result.ChangedFields);
        ResetFake();
    }

    [Fact]
    public async Task Check_MultipleChanges_ReturnsDeterministicFieldOrder()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with { Summary = "X", IssueTypeName = "Bug" });
        var result = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("changed", result!.Status);
        Assert.Equal(new[] { "title", "issueType" }, result.ChangedFields);
        ResetFake();
    }

    // ---------- baseline / version validation ----------

    [Fact]
    public async Task Check_NonJiraVersion_Returns404_WithoutJiraCall()
    {
        await SeedOnceAsync();
        ResetFake();
        var before = _factory.Jira.GetCalls;
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _manualCaseId, _manualVersionId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("JIRA_PROVENANCE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<ErrorPayload>())?.Error.Code);
        Assert.Equal(before, _factory.Jira.GetCalls);
    }

    [Fact]
    public async Task Check_MalformedProvenance_Returns404_WithoutJiraCall()
    {
        await SeedOnceAsync();
        ResetFake();
        var before = _factory.Jira.GetCalls;
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _malformedCaseId, _malformedVersionId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("JIRA_PROVENANCE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<ErrorPayload>())?.Error.Code);
        Assert.Equal(before, _factory.Jira.GetCalls);
    }

    [Fact]
    public async Task Check_VersionMismatch_Returns404()
    {
        await SeedOnceAsync();
        ResetFake();
        // Version belongs to CHECK-001, requested under CHECK-OTH.
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _otherCaseId, _versionId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Check_CrossProject_Returns403()
    {
        await SeedOnceAsync();
        ResetFake();
        // jc-tester is not a member of project B.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await CheckAsync(Client("jc-tester", ["tester"]), _bCaseId, _bVersionId)).StatusCode);
    }

    [Fact]
    public async Task Check_SameKeyInOtherProject_ResolvesPerProject()
    {
        await SeedOnceAsync();
        ResetFake();
        // jc-manager belongs to both projects: each project's check resolves
        // its own integration and returns only its own result.
        var result = await (await CheckAsync(Client("jc-manager", ["qa-lead"]), _bCaseId, _bVersionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("current", result!.Status);
        Assert.Equal("ABC-123", result.JiraIssueKey);
    }

    [Fact]
    public async Task Check_ForeignJiraProject_ReturnsOpaque404()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with { ProjectKey = "OTHER" });
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("OTHER", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        ResetFake();
    }

    [Fact]
    public async Task Check_ClientCannotChooseJiraTarget()
    {
        await SeedOnceAsync();
        ResetFake();
        var before = _factory.Jira.GetCalls;
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId, new
        {
            baseUrl = "https://evil.test",
            projectKey = "EVIL",
            integrationId = Guid.NewGuid(),
            apiToken = "attacker-token",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 1, _factory.Jira.GetCalls);
        Assert.Equal("https://jira.test", _factory.Jira.LastIssueRequest?.BaseUrl);
        Assert.Equal("ABC-123", _factory.Jira.LastIssueRequest?.IssueKey);
    }

    // ---------- provider / rate limit / errors ----------

    [Fact]
    public async Task Check_RateLimit_IsSharedAndBlocksJiraCall()
    {
        await SeedOnceAsync();
        ResetFake();
        var client = Client("jc-tester", ["tester"]);
        // Dedicated project R keeps this budget proof independent of order:
        // 30 checks consume the shared 30/min Jira-read budget, the 31st is
        // rejected without touching Jira.
        for (var i = 0; i < 30; i++)
        {
            var response = await client.PostAsync(
                $"/api/v1/test-cases/{_rateCaseId}/versions/{_rateVersionId}/jira-change-check", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var before = _factory.Jira.GetCalls;
        var limited = await client.PostAsync(
            $"/api/v1/test-cases/{_rateCaseId}/versions/{_rateVersionId}/jira-change-check", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(before, _factory.Jira.GetCalls);
    }

    [Fact]
    public async Task Check_Jira404_MapsSafely()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw new JiraProviderException(JiraErrorKind.NotFound, "gone");
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Check_Jira429_MapsSafely()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw new JiraProviderException(JiraErrorKind.RateLimited, "slow");
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Check_JiraTimeout_MapsSafely()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw JiraProviderException.Timeout("slow");
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ResetFake();
    }

    [Fact]
    public async Task Check_DisabledIntegration_Returns409()
    {
        await SeedOnceAsync();
        ResetFake();
        Guid caseId = Guid.Empty, versionId = Guid.Empty;
        await _factory.SeedAsync(db =>
        {
            var testCase = new TestCase
            {
                ProjectId = _projectDisabled, TestKey = "CHECK-D", Title = "D",
                Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "ai",
            };
            db.TestCases.Add(testCase);
            var version = new TestCaseVersion
            {
                TestCaseId = testCase.Id, VersionNumber = 1,
                ReviewStatus = ReviewStatus.Approved, GenerationRequest = StoredJira(),
            };
            db.TestCaseVersions.Add(version);
            caseId = testCase.Id;
            versionId = version.Id;
            return Task.CompletedTask;
        });
        var response = await CheckAsync(Client("jc-tester", ["tester"]), caseId, versionId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---------- response safety ----------

    [Fact]
    public async Task Check_Response_ContainsNoRawContentOrSecrets()
    {
        await SeedOnceAsync();
        ResetFake();
        var raw = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadAsStringAsync();
        foreach (var token in new[]
            { "Server is up", "Pay now", "acceptanceCriteria", "SecretReference", "apiToken",
              "Authorization", "Bearer", "generationRequest", "paragraph" })
            Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
    }

    // ---------- audit ----------

    [Fact]
    public async Task Check_AuditsRequestedAndCompleted_Safely()
    {
        await SeedOnceAsync();
        ResetFake();
        await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        await _factory.SeedAsync(async db =>
        {
            var events = await db.AuditEvents
                .Where(e => e.ProjectId == _projectA && e.Action.StartsWith("jira-change-check"))
                .ToListAsync();
            Assert.Contains(events, e => e.Action == "jira-change-check.requested");
            Assert.Contains(events, e => e.Action == "jira-change-check.completed");
            foreach (var meta in events.Select(e => e.MetadataJson))
            {
                Assert.DoesNotContain("Server is up", meta ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("Pay now", meta ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("bulletList", meta ?? string.Empty, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task Check_AuditsFailed_OnExternalFailure()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = _ => throw JiraProviderException.Unavailable("down");
        await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        ResetFake();
        await _factory.SeedAsync(async db =>
        {
            Assert.Contains(await db.AuditEvents
                .Where(e => e.ProjectId == _projectA && e.Action == "jira-change-check.failed")
                .Select(e => e.MetadataJson ?? string.Empty)
                .ToListAsync(), m => m.Contains("Unavailable", StringComparison.Ordinal));
        });
    }

    // ---------- immutability ----------

    [Fact]
    public async Task Check_DoesNotModifyVersionOrHistory()
    {
        await SeedOnceAsync();
        ResetFake();
        string before = string.Empty;
        int countBefore = 0;
        await _factory.SeedAsync(async db =>
        {
            before = (await db.TestCaseVersions.FirstAsync(v => v.Id == _versionId))
                .GenerationRequest?.RootElement.GetRawText() ?? string.Empty;
            countBefore = await db.TestCases.CountAsync(t => t.ProjectId == _projectA);
        });
        await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId);
        await _factory.SeedAsync(async db =>
        {
            var after = (await db.TestCaseVersions.FirstAsync(v => v.Id == _versionId))
                .GenerationRequest?.RootElement.GetRawText() ?? string.Empty;
            Assert.Equal(before, after);
            Assert.Equal(countBefore, await db.TestCases.CountAsync(t => t.ProjectId == _projectA));
        });
    }

    // ---------- result shape ----------

    [Fact]
    public async Task Check_CurrentResult_HasEmptyChangedFields_AndCheckedAt()
    {
        await SeedOnceAsync();
        ResetFake();
        var result = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("current", result!.Status);
        Assert.Empty(result.ChangedFields);
        Assert.True(DateTimeOffset.TryParse(result.CheckedAt, out _));
    }

    [Fact]
    public async Task Check_ChangedResult_OnlyAllowlistedFields()
    {
        await SeedOnceAsync();
        _factory.Jira.GetHandler = req => Task.FromResult(
            FakeJiraIssues.DefaultIssue() with { Summary = "X", IssueTypeName = "Bug" });
        var result = await (await CheckAsync(Client("jc-tester", ["tester"]), _caseId, _versionId))
            .Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("changed", result!.Status);
        Assert.All(result.ChangedFields, f =>
            Assert.Contains(f, new[] { "title", "description", "acceptanceCriteria", "issueType" }));
        ResetFake();
    }

    [Fact]
    public async Task Check_LegacyProvenance_WithoutNormalizerVersion_Works()
    {
        await SeedOnceAsync();
        ResetFake();
        var response = await CheckAsync(Client("jc-tester", ["tester"]), _legacyCaseId, _legacyVersionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("current", result!.Status);
    }
}
