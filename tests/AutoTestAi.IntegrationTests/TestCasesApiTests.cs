using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 3: Test repository — auth matrix, IDOR boundary (test case → project),
/// CRUD, immutable versioning, review lifecycle, filters (docs/06 §6).
/// </summary>
public sealed class TestCasesApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    // xUnit creates one test-class instance per test, so every test seeds its
    // own consistent dataset (fresh project/case guids); fixed role rows are
    // inserted idempotently.
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public TestCasesApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task<Guid> SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            Guid caseA = Guid.Empty, caseB = Guid.Empty;
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

                var mgr = new User { ExternalIdentityId = "s3-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "s3-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "s3-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "s3-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);

                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Repo Alpha", Key = "REPOA" },
                    new Project { Id = pb, Name = "Repo Beta", Key = "REPOB" });

                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                var a = new TestCase
                {
                    ProjectId = pa, TestKey = "LOGIN-001", Title = "Login works",
                    Module = "Authentication", Framework = "playwright", Platform = "web",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var b = new TestCase
                {
                    ProjectId = pb, TestKey = "SMOKE-001", Title = "Smoke works (B)",
                    Priority = Priority.Medium, Status = TestCaseStatus.Draft, SourceType = "manual",
                };
                db.TestCases.AddRange(a, b);
                db.TestCaseVersions.AddRange(
                    new TestCaseVersion
                    {
                        TestCaseId = a.Id, VersionNumber = 1, SourceCode = "// a-v1",
                        ReviewStatus = ReviewStatus.Approved,
                    },
                    new TestCaseVersion
                    {
                        TestCaseId = a.Id, VersionNumber = 2, SourceCode = "// a-v2",
                        StructuredSteps = JsonDocument.Parse(
                            """[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
                        ReviewStatus = ReviewStatus.Pending,
                    },
                    new TestCaseVersion
                    {
                        TestCaseId = b.Id, VersionNumber = 1, SourceCode = "// b-v1",
                        ReviewStatus = ReviewStatus.Pending,
                    });
                caseA = a.Id;
                caseB = b.Id;
                return Task.CompletedTask;
            });
            return caseA;
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

    private static string Manager() => TestTokens.Create("s3-manager", ["qa-lead"]);
    private static string Tester() => TestTokens.Create("s3-tester", ["tester"]);
    private static string Viewer() => TestTokens.Create("s3-viewer", ["viewer"]);
    private static string Outsider() => TestTokens.Create("s3-outsider", ["tester"]);
    private static string Admin() => TestTokens.Create("s3-admin", ["admin"]);

    private sealed record PagePayload(IReadOnlyList<CaseItem> Items, int TotalCount, int Page, int PageSize);
    private sealed record CaseItem(
        Guid Id, Guid ProjectId, string TestKey, string Title, string? Module,
        string? Framework, string? Platform, string Priority, string Status,
        int LatestVersionNumber, string LatestReviewStatus, DateTimeOffset UpdatedAt);
    private sealed record CaseDetails(
        Guid Id, Guid ProjectId, string TestKey, string Title, string? Description,
        string? Module, string? Framework, string? Platform, string Priority, string Status,
        string? SourceType, int LatestVersionNumber, string LatestReviewStatus,
        Guid? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record StepPayload(int Order, string Action, string? Target, string? Value);
    private sealed record VersionPayload(
        Guid Id, Guid TestCaseId, int VersionNumber, string? SourceCode,
        IReadOnlyList<StepPayload> StructuredSteps, string? GenerationProvider,
        string? GenerationModel, long? GenerationLatencyMs, string ReviewStatus,
        Guid? CreatedBy, DateTimeOffset CreatedAt);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    private static async Task<Guid> CaseInBAsync(Slice1ApiFactory factory, Guid projectB)
    {
        Guid id = Guid.Empty;
        await factory.SeedAsync(db =>
        {
            var existing = db.TestCases.FirstOrDefault(t => t.ProjectId == projectB);
            if (existing is not null) id = existing.Id;
            return Task.CompletedTask;
        });
        return id;
    }

    // ---------- authentication ----------

    [Fact]
    public async Task List_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        var response = await Client(null).GetAsync($"/api/v1/projects/{_projectA}/test-cases");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Detail_Anonymous_Returns401()
    {
        var caseA = await SeedOnceAsync();
        var response = await Client(null).GetAsync($"/api/v1/test-cases/{caseA}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- IDOR: test case → project ----------

    [Fact]
    public async Task CrossProjectReads_AsNonMember_Return403()
    {
        await SeedOnceAsync();
        var caseB = await CaseInBAsync(_factory, _projectB);
        var client = Client("s3-tester"); // member of A only

        foreach (var url in new[]
                 {
                     $"/api/v1/test-cases/{caseB}",
                     $"/api/v1/test-cases/{caseB}/versions",
                     $"/api/v1/projects/{_projectB}/test-cases",
                 })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            Assert.Equal("FORBIDDEN", body?.Error.Code);
        }
    }

    [Fact]
    public async Task CrossProjectWrites_AsNonMember_Return403()
    {
        await SeedOnceAsync();
        var caseB = await CaseInBAsync(_factory, _projectB);
        var client = Client("s3-tester");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/api/v1/test-cases/{caseB}", new { title = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/v1/test-cases/{caseB}/review",
            new { versionId = Guid.NewGuid(), reviewStatus = "Approved" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"/api/v1/test-cases/{caseB}")).StatusCode);
    }

    [Fact]
    public async Task CrossTestCaseVersion_UnderAuthorizedCase_Returns404()
    {
        var caseA = await SeedOnceAsync();
        var caseB = await CaseInBAsync(_factory, _projectB);
        // Admin passes authorization everywhere: version must still belong to the case.
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Admin());
        Guid foreignVersionId = Guid.Empty;
        await _factory.SeedAsync(db =>
        {
            foreignVersionId = db.TestCaseVersions.First(v => v.TestCaseId == caseB).Id;
            return Task.CompletedTask;
        });
        var response = await admin.GetAsync($"/api/v1/test-cases/{caseA}/versions/{foreignVersionId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- CRUD ----------

    [Fact]
    public async Task Create_Returns201_WithVersion1Pending()
    {
        await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-cases", new
        {
            testKey = "checkout-001",
            title = "Checkout works",
            module = "Shop",
            framework = "playwright",
            platform = "web",
            priority = "High",
            sourceCode = "test('checkout', async () => {});",
            structuredSteps = new[]
            {
                new { order = 1, action = "navigate", target = "https://example.test/shop" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CaseDetails>();
        Assert.NotNull(created);
        Assert.Equal("CHECKOUT-001", created!.TestKey);
        Assert.Equal(1, created.LatestVersionNumber);
        Assert.Equal("Pending", created.LatestReviewStatus);

        var versions = await client.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{created.Id}/versions");
        Assert.NotNull(versions);
        var v1 = Assert.Single(versions!);
        Assert.Equal(1, v1.VersionNumber);
        Assert.Equal("test('checkout', async () => {});", v1.SourceCode);
        var step = Assert.Single(v1.StructuredSteps);
        Assert.Equal("navigate", step.Action);
    }

    [Fact]
    public async Task Create_DuplicateKeySameProject_Returns409_OtherProject_Allowed()
    {
        await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());

        var duplicate = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-cases",
            new { testKey = "LOGIN-001", title = "Dup" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var other = await client.PostAsJsonAsync($"/api/v1/projects/{_projectB}/test-cases",
            new { testKey = "LOGIN-001", title = "Same key, other project" });
        // Manager is not a member of B and not admin → 403 proves scoping, not a bug.
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Admin());
        var allowed = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectB}/test-cases",
            new { testKey = "LOGIN-001", title = "Same key, other project" });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400_WithFieldDetails()
    {
        await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-cases", new
        {
            testKey = "bad key!",
            title = "",
            priority = "Bogus",
            structuredSteps = new[] { new { target = "#x" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);
        var fields = doc!.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToHashSet();
        Assert.Contains("title", fields);
        Assert.Contains("testKey", fields);
        Assert.Contains("priority", fields);
        Assert.Contains("structuredSteps", fields);
    }

    [Fact]
    public async Task Create_AsViewer_Returns403()
    {
        await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create("s3-viewer", ["viewer"]));
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-cases",
            new { testKey = "NOPE-001", title = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Details_ReturnsMetadata_AndListOmitsSourceCode()
    {
        var caseA = await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Tester());

        var details = await client.GetFromJsonAsync<CaseDetails>($"/api/v1/test-cases/{caseA}");
        Assert.NotNull(details);
        Assert.Equal("LOGIN-001", details!.TestKey);
        Assert.Equal(2, details.LatestVersionNumber);
        Assert.Equal("Pending", details.LatestReviewStatus);

        var raw = await client.GetStringAsync(
            $"/api/v1/projects/{_projectA}/test-cases?pageSize=50");
        Assert.DoesNotContain("sourceCode", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOGIN-001", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_MetadataOnly_DoesNotCreateVersion()
    {
        var caseA = await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());

        var response = await client.PutAsJsonAsync($"/api/v1/test-cases/{caseA}",
            new { title = "Login works (renamed)", priority = "Low" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CaseDetails>();
        Assert.Equal("Login works (renamed)", updated!.Title);
        Assert.Equal(2, updated.LatestVersionNumber);

        var versions = await client.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{caseA}/versions");
        Assert.Equal(2, versions!.Count);
    }

    [Fact]
    public async Task Update_ContentChange_CreatesVersion_AndPreservesOld()
    {
        var caseA = await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());

        var response = await client.PutAsJsonAsync($"/api/v1/test-cases/{caseA}", new
        {
            title = "Login works",
            sourceCode = "// a-v3",
            hasSourceCode = true,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CaseDetails>();
        Assert.Equal(3, updated!.LatestVersionNumber);

        var versions = await client.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{caseA}/versions");
        Assert.Equal([3, 2, 1], versions!.Select(v => v.VersionNumber).ToList());
        Assert.Equal("// a-v2", versions.First(v => v.VersionNumber == 2).SourceCode);
    }

    [Fact]
    public async Task Archive_SoftDeletes_PreservingVersions()
    {
        var caseA = await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());

        var delete = await client.DeleteAsync($"/api/v1/test-cases/{caseA}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var details = await client.GetFromJsonAsync<CaseDetails>($"/api/v1/test-cases/{caseA}");
        Assert.Equal("Archived", details!.Status);

        var versions = await client.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{caseA}/versions");
        Assert.Equal(2, versions!.Count);
    }

    // ---------- versions & review ----------

    [Fact]
    public async Task Versions_ListNewestFirst_AndDetailRoundTrips()
    {
        var caseA = await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Tester());

        var versions = await client.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{caseA}/versions");
        Assert.NotNull(versions);
        Assert.Equal([2, 1], versions!.Select(v => v.VersionNumber).ToList());

        var v2 = versions.First(v => v.VersionNumber == 2);
        var detail = await client.GetFromJsonAsync<VersionPayload>(
            $"/api/v1/test-cases/{caseA}/versions/{v2.Id}");
        Assert.Equal("// a-v2", detail!.SourceCode);
        var step = Assert.Single(detail.StructuredSteps);
        Assert.Equal("navigate", step.Action);
        Assert.Equal("Pending", detail.ReviewStatus);
    }

    [Fact]
    public async Task Review_Approve_Then_InvalidTransitionRejected()
    {
        var caseA = await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());

        var versions = await client.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{caseA}/versions");
        var pending = versions!.First(v => v.VersionNumber == 2);

        var approved = await client.PostAsJsonAsync($"/api/v1/test-cases/{caseA}/review",
            new { versionId = pending.Id, reviewStatus = "Approved" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = await approved.Content.ReadFromJsonAsync<VersionPayload>();
        Assert.Equal("Approved", body!.ReviewStatus);

        var invalid = await client.PostAsJsonAsync($"/api/v1/test-cases/{caseA}/review",
            new { versionId = pending.Id, reviewStatus = "Pending" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Review_AsViewer_Returns403_And_UnknownVersion_Returns404()
    {
        var caseA = await SeedOnceAsync();
        var viewer = _factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create("s3-viewer", ["viewer"]));
        var versions = await viewer.GetFromJsonAsync<List<VersionPayload>>(
            $"/api/v1/test-cases/{caseA}/versions");
        var denied = await viewer.PostAsJsonAsync($"/api/v1/test-cases/{caseA}/review",
            new { versionId = versions!.First().Id, reviewStatus = "Approved" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());
        var missing = await manager.PostAsJsonAsync($"/api/v1/test-cases/{caseA}/review",
            new { versionId = Guid.NewGuid(), reviewStatus = "Approved" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---------- filters ----------

    [Fact]
    public async Task List_Filters_BySearchStatusPriorityFrameworkPlatform()
    {
        await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Manager());
        var created = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-cases", new
        {
            testKey = "CART-001",
            title = "Cart checkout",
            module = "Shop",
            framework = "selenium",
            platform = "mobile",
            priority = "Low",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        async Task<int> Count(string query)
        {
            var page = await client.GetFromJsonAsync<PagePayload>(
                $"/api/v1/projects/{_projectA}/test-cases{query}");
            return page!.TotalCount;
        }

        Assert.Equal(1, await Count("?search=cart"));
        Assert.Equal(1, await Count("?search=LOGIN-001"));
        Assert.Equal(1, await Count("?status=Active"));
        Assert.Equal(1, await Count("?priority=Low"));
        Assert.Equal(1, await Count("?framework=selenium"));
        Assert.Equal(1, await Count("?platform=web"));
        Assert.Equal(2, await Count("?reviewStatus=Pending"));
        Assert.Equal(2, await Count("?page=1&pageSize=25"));
        Assert.Equal(0, await Count("?status=Archived"));
    }

    [Fact]
    public async Task List_InvalidFilter_Returns400()
    {
        await SeedOnceAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Tester());
        var response = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/test-cases?status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
