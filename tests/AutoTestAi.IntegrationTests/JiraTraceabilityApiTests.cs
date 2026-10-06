using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 6: read-side Jira traceability — version-level provenance
/// display, ANY-version exact-match filtering, composition, pagination,
/// project isolation, and secret safety. No Jira calls, no AI, no migration.
/// </summary>
public sealed class JiraTraceabilityApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private readonly Dictionary<string, Guid> _caseIds = new();

    public JiraTraceabilityApiTests(Slice1ApiFactory factory) => _factory = factory;

    private static object JiraProvenance(string key, string? type = "Story") => new
    {
        storyTitle = "Guest checkout",
        storyDescription = "Flow.",
        acceptanceCriteria = new[] { "Pay" },
        focusCriterionIndex = 0,
        promptVersion = "story-to-tests-v1",
        source = "story-ai",
        origin = "jira-import",
        jiraIssueKey = key,
        jiraIssueType = type,
        jiraBaseUrlHost = "company.atlassian.net",
        jiraFetchedAt = "2026-10-06T00:00:00Z",
    };

    private static object GenericProvenance() => new
    {
        title = "Login",
        promptVersion = "test-generation-v1",
        source = "ai",
    };

    private static object ValidSteps() => new[]
    {
        new { order = 1, action = "navigate", target = "https://example.test/checkout", value = (string?)null },
    };

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
                var mgr = new User { ExternalIdentityId = "jt-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "jt-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "jt-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "jt-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Trace Alpha", Key = "TCA" },
                    new Project { Id = pb, Name = "Trace Beta", Key = "TCB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pb, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });
                return Task.CompletedTask;
            });

            if (_caseIds.Count > 0) return;
            var client = Client("jt-manager", ["qa-lead"]);

            async Task<Guid> CreateCase(Guid project, string key, object? provenance)
            {
                var response = await client.PostAsJsonAsync(
                    $"/api/v1/projects/{project}/test-cases", new
                    {
                        testKey = key,
                        title = $"Title {key}",
                        framework = "playwright",
                        platform = "web",
                        priority = "High",
                        sourceType = "ai",
                        sourceCode = "test('x', async () => {});",
                        structuredSteps = ValidSteps(),
                        generationProvider = "stub",
                        generationRequest = provenance,
                    });
                response.EnsureSuccessStatusCode();
                var created = await response.Content.ReadFromJsonAsync<CreatedPayload>();
                return created!.Id;
            }

            _caseIds["J1"] = await CreateCase(pa, "TRACE-J1", JiraProvenance("PROJ-123"));
            _caseIds["J2"] = await CreateCase(pa, "TRACE-J2", JiraProvenance("PROJ-123"));
            _caseIds["J3"] = await CreateCase(pa, "TRACE-J3", JiraProvenance("PROJ-999"));
            _caseIds["J4"] = await CreateCase(pa, "TRACE-J4", JiraProvenance("PROJ-123"));
            _caseIds["M"] = await CreateCase(pa, "TRACE-M", GenericProvenance());
            _caseIds["SUB"] = await CreateCase(pa, "TRACE-SUB", JiraProvenance("PROJ-1234"));
            _caseIds["X"] = await CreateCase(pb, "TRACE-X", JiraProvenance("PROJ-123"));
            // Origin-consistency traps: jiraIssueKey WITHOUT origin == "jira-import"
            // must match nothing and project null provenance (see detail test).
            _caseIds["W"] = await CreateCase(pa, "TRACE-W", new
            {
                origin = "other",
                jiraIssueKey = "PROJ-123",
            });
            _caseIds["N"] = await CreateCase(pa, "TRACE-N", new
            {
                jiraIssueKey = "PROJ-123",
            });

            // J1 v2: manual content edit → latest version has no provenance,
            // v1 keeps historical Jira provenance (ANY-version semantics).
            var update = await client.PutAsJsonAsync($"/api/v1/test-cases/{_caseIds["J1"]}", new
            {
                title = "Title TRACE-J1",
                framework = "playwright",
                platform = "web",
                sourceCode = "test('x-updated', async () => {});",
                structuredSteps = ValidSteps(),
                hasSourceCode = true,
                hasStructuredSteps = true,
            });
            update.EnsureSuccessStatusCode();
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

    private sealed record CreatedPayload(Guid Id);
    private sealed record ListItemPayload(Guid Id, string TestKey);
    private sealed record ListPayload(IReadOnlyList<ListItemPayload> Items, int TotalCount, int Page, int PageSize);
    private sealed record JiraProvenancePayload(
        string Origin, string JiraIssueKey, string? JiraIssueType, string? JiraBaseUrlHost, string? JiraFetchedAt);
    private sealed record VersionPayload(Guid Id, int VersionNumber, JiraProvenancePayload? JiraProvenance);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    private async Task<ListPayload> ListAsync(HttpClient client, Guid project, string query = "")
        => (await (await client.GetAsync($"/api/v1/projects/{project}/test-cases{query}"))
            .Content.ReadFromJsonAsync<ListPayload>())!;

    private static List<string> KeysOf(ListPayload page)
        => page.Items.Select(i => i.TestKey).Order().ToList();

    [Fact]
    public async Task List_NoFilter_ReturnsAllProjectCases()
    {
        await SeedOnceAsync();
        var page = await ListAsync(Client("jt-tester", ["tester"]), _projectA);
        Assert.Equal(8, page.TotalCount);
        Assert.Equal(
            new[] { "TRACE-J1", "TRACE-J2", "TRACE-J3", "TRACE-J4", "TRACE-M", "TRACE-N", "TRACE-SUB", "TRACE-W" },
            KeysOf(page));
    }

    [Fact]
    public async Task Filter_MatchesAnyVersion_WithExactKey()
    {
        await SeedOnceAsync();
        var page = await ListAsync(Client("jt-tester", ["tester"]), _projectA, "?jiraIssueKey=PROJ-123");
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { "TRACE-J1", "TRACE-J2", "TRACE-J4" }, KeysOf(page));
    }

    [Fact]
    public async Task Filter_IsExact_NotSubstring()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        var exact = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123");
        Assert.DoesNotContain("TRACE-SUB", KeysOf(exact));
        var longer = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-1234");
        Assert.Equal(new[] { "TRACE-SUB" }, KeysOf(longer));
    }

    [Theory]
    [InlineData(" proj-123 ", new[] { "TRACE-J1", "TRACE-J2", "TRACE-J4" })]
    [InlineData("Proj-123", new[] { "TRACE-J1", "TRACE-J2", "TRACE-J4" })]
    public async Task Filter_NormalizesCaseAndWhitespace(string input, string[] expected)
    {
        await SeedOnceAsync();
        var page = await ListAsync(Client("jt-tester", ["tester"]), _projectA,
            $"?jiraIssueKey={Uri.EscapeDataString(input)}");
        Assert.Equal(expected, KeysOf(page));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("PROJ123")]
    [InlineData("https://jira.test/browse/PROJ-123")]
    public async Task Filter_InvalidKey_Returns400(string input)
    {
        await SeedOnceAsync();
        var response = await Client("jt-tester", ["tester"]).GetAsync(
            $"/api/v1/projects/{_projectA}/test-cases?jiraIssueKey={Uri.EscapeDataString(input)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("VALIDATION_ERROR", body?.Error.Code);
    }

    [Fact]
    public async Task Filter_ComposesWithExistingFilters()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        // J1 was reprioritized to Medium by its v2 content edit; J2/J4 stayed High.
        var high = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123&priority=High");
        Assert.Equal(new[] { "TRACE-J2", "TRACE-J4" }, KeysOf(high));
        var medium = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123&priority=Medium");
        Assert.Equal(new[] { "TRACE-J1" }, KeysOf(medium));
        var search = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123&search=TRACE-J2");
        Assert.Equal(new[] { "TRACE-J2" }, KeysOf(search));
    }

    [Fact]
    public async Task Filter_PaginationRemainsCorrect()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        var first = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123&page=1&pageSize=2");
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(2, first.Items.Count);
        var second = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123&page=2&pageSize=2");
        Assert.Equal(3, second.TotalCount);
        Assert.Single(second.Items);
        Assert.Empty((await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-404")).Items);
    }

    [Fact]
    public async Task Versions_ExposeProvenance_PerVersion()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        var j2 = await (await client.GetAsync($"/api/v1/test-cases/{_caseIds["J2"]}/versions"))
            .Content.ReadFromJsonAsync<List<VersionPayload>>();
        var provenance = Assert.Single(j2!).JiraProvenance;
        Assert.NotNull(provenance);
        Assert.Equal("jira-import", provenance!.Origin);
        Assert.Equal("PROJ-123", provenance.JiraIssueKey);
        Assert.Equal("Story", provenance.JiraIssueType);
        Assert.Equal("company.atlassian.net", provenance.JiraBaseUrlHost);
        Assert.Equal("2026-10-06T00:00:00Z", provenance.JiraFetchedAt);

        var m = await (await client.GetAsync($"/api/v1/test-cases/{_caseIds["M"]}/versions"))
            .Content.ReadFromJsonAsync<List<VersionPayload>>();
        Assert.Null(Assert.Single(m!).JiraProvenance);
    }

    [Fact]
    public async Task HistoricalOnlyVersion_NullButCaseStillMatches()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        var versions = await (await client.GetAsync($"/api/v1/test-cases/{_caseIds["J1"]}/versions"))
            .Content.ReadFromJsonAsync<List<VersionPayload>>();
        Assert.Equal(2, versions!.Count);
        Assert.Null(versions.OrderByDescending(v => v.VersionNumber).First().JiraProvenance);
        Assert.NotNull(versions.OrderByDescending(v => v.VersionNumber).Last().JiraProvenance);

        var page = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123");
        Assert.Contains("TRACE-J1", KeysOf(page));
    }

    [Fact]
    public async Task Filter_RequiresJiraOrigin_NotJustKey()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        var page = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123");
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { "TRACE-J1", "TRACE-J2", "TRACE-J4" }, KeysOf(page));
        Assert.DoesNotContain("TRACE-W", KeysOf(page));
        Assert.DoesNotContain("TRACE-N", KeysOf(page));
    }

    [Fact]
    public async Task DetailAndFilter_AgreeOnNonJiraOrigin()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        foreach (var key in new[] { "W", "N" })
        {
            var versions = await (await client.GetAsync($"/api/v1/test-cases/{_caseIds[key]}/versions"))
                .Content.ReadFromJsonAsync<List<VersionPayload>>();
            Assert.Null(Assert.Single(versions!).JiraProvenance);
        }
    }

    [Fact]
    public async Task ProjectIsolation_SameKeyInTwoProjects()
    {
        await SeedOnceAsync();
        var client = Client("jt-manager", ["qa-lead"]);
        var a = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123");
        Assert.DoesNotContain("TRACE-X", KeysOf(a));
        var b = await ListAsync(client, _projectB, "?jiraIssueKey=PROJ-123");
        Assert.Equal(new[] { "TRACE-X" }, KeysOf(b));
    }

    [Fact]
    public async Task ViewerCanRead_ProvenanceAndFilter()
    {
        await SeedOnceAsync();
        var client = Client("jt-viewer", ["viewer"]);
        var page = await ListAsync(client, _projectA, "?jiraIssueKey=PROJ-123");
        Assert.Equal(3, page.TotalCount);
        var versions = await (await client.GetAsync($"/api/v1/test-cases/{_caseIds["J2"]}/versions"))
            .Content.ReadFromJsonAsync<List<VersionPayload>>();
        Assert.NotNull(Assert.Single(versions!).JiraProvenance);
    }

    [Fact]
    public async Task Unauthorized_ReadsRejected()
    {
        await SeedOnceAsync();
        var outsider = Client("jt-outsider", ["tester"]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/v1/projects/{_projectA}/test-cases?jiraIssueKey=PROJ-123")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/v1/test-cases/{_caseIds["J2"]}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client(null).GetAsync($"/api/v1/projects/{_projectA}/test-cases?jiraIssueKey=PROJ-123")).StatusCode);
    }

    [Fact]
    public async Task Responses_ContainNoRawGenerationRequest()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        var listRaw = await (await client.GetAsync(
            $"/api/v1/projects/{_projectA}/test-cases?jiraIssueKey=PROJ-123")).Content.ReadAsStringAsync();
        var versionsRaw = await (await client.GetAsync(
            $"/api/v1/test-cases/{_caseIds["J2"]}/versions")).Content.ReadAsStringAsync();
        Assert.Contains("jiraProvenance", versionsRaw, StringComparison.Ordinal);
        foreach (var raw in new[] { listRaw, versionsRaw })
        {
            Assert.DoesNotContain("acceptanceCriteria", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("generationRequest", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("storyTitle", raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Responses_ContainNoSecrets()
    {
        await SeedOnceAsync();
        var client = Client("jt-tester", ["tester"]);
        var versionsRaw = await (await client.GetAsync(
            $"/api/v1/test-cases/{_caseIds["J2"]}/versions")).Content.ReadAsStringAsync();
        foreach (var token in new[] { "SecretReference", "secretReference", "apiToken", "Authorization", "Bearer" })
            Assert.DoesNotContain(token, versionsRaw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyResult_ReturnsNormalEmptyShape()
    {
        await SeedOnceAsync();
        var page = await ListAsync(Client("jt-tester", ["tester"]), _projectA, "?jiraIssueKey=PROJ-404");
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(1, page.Page);
    }
}
