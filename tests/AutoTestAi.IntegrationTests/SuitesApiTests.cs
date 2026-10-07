using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 9A: test suites — auth matrix, project isolation (IDOR),
/// CRUD, membership + ordering, manual Run Now fan-out, history and report.
/// </summary>
public sealed class SuitesApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public SuitesApiTests(Slice1ApiFactory factory) => _factory = factory;

    private sealed record SeedResult(Guid SuiteA, Guid CaseA1, Guid CaseA2, Guid SuiteB, Guid CaseB, Guid EmptySuite);
    private sealed record SuitePage(IReadOnlyList<SuiteItem> Items, int TotalCount, int Page, int PageSize);
    private sealed record SuiteItem(Guid Id, Guid ProjectId, string Name, string? Description, string Status, int TestCount, DateTimeOffset UpdatedAt);
    private sealed record SuiteDetail(Guid Id, Guid ProjectId, string Name, string? Description, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<SuiteMember> Members);
    private sealed record SuiteMember(Guid TestCaseId, string TestKey, string Title, int ExecutionOrder, string? JiraIssueKey, string? FreshnessState);
    private sealed record CreatedSuite(Guid Id, Guid ProjectId, string Name, string TestKey);
    private sealed record MemberResult(Guid SuiteId, Guid TestCaseId, int ExecutionOrder);
    private sealed record ExecuteResult(Guid ExecutionId, Guid SuiteId, int TestCount, string Status, DateTimeOffset CreatedAt);
    private sealed record HistoryPage(IReadOnlyList<HistoryItem> Items, int TotalCount, int Page, int PageSize);
    private sealed record HistoryItem(Guid ExecutionId, string Status, string TriggerType, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int TestCount, int PassedCount, int FailedCount);
    private sealed record SuiteReport(Guid SuiteId, string SuiteName, int TotalExecutions, int PassedCount, int FailedCount, int CancelledCount, int TimedOutCount, int ErrorCount, double? PassRate, long TotalDurationMs, long AverageDurationMs, DateTimeOffset? LatestExecutionAt);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    private static JsonDocument Steps()
        => JsonDocument.Parse("""[{"order":1,"action":"navigate","target":"https://example.test"}]""");

    private async Task<SeedResult> SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            var result = new SeedResult(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
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

                var mgr = new User { ExternalIdentityId = "suite-manager", Email = "sm@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "suite-tester", Email = "st@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "suite-viewer", Email = "sv@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "suite-outsider", Email = "so@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);

                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Suite Alpha", Key = "SUITEA" },
                    new Project { Id = pb, Name = "Suite Beta", Key = "SUITEB" });

                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId },
                    new ProjectMember { ProjectId = pb, UserId = mgr.Id, RoleId = QaLeadRoleId });

                var a1 = new TestCase
                {
                    ProjectId = pa, TestKey = "SUITE-A1", Title = "Alpha one",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var a2 = new TestCase
                {
                    ProjectId = pa, TestKey = "SUITE-A2", Title = "Alpha two",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var pending = new TestCase
                {
                    ProjectId = pa, TestKey = "SUITE-AP", Title = "Alpha pending",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var b = new TestCase
                {
                    ProjectId = pb, TestKey = "SUITE-B1", Title = "Beta one",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.AddRange(a1, a2, pending, b);
                db.TestCaseVersions.AddRange(
                    new TestCaseVersion { TestCaseId = a1.Id, VersionNumber = 1, SourceCode = "// a1", StructuredSteps = Steps(), ReviewStatus = ReviewStatus.Approved },
                    new TestCaseVersion { TestCaseId = a2.Id, VersionNumber = 1, SourceCode = "// a2", StructuredSteps = Steps(), ReviewStatus = ReviewStatus.Approved },
                    new TestCaseVersion { TestCaseId = pending.Id, VersionNumber = 1, SourceCode = "// ap", StructuredSteps = Steps(), ReviewStatus = ReviewStatus.Pending },
                    new TestCaseVersion { TestCaseId = b.Id, VersionNumber = 1, SourceCode = "// b", StructuredSteps = Steps(), ReviewStatus = ReviewStatus.Approved });

                var suiteA = new TestSuite { ProjectId = pa, Name = "Regression", Description = "Main suite", Status = ProjectStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                var pendingSuite = new TestSuite { ProjectId = pa, Name = "Pending only", Status = ProjectStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                var emptySuite = new TestSuite { ProjectId = pa, Name = "Empty", Status = ProjectStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                var suiteB = new TestSuite { ProjectId = pb, Name = "Beta suite", Status = ProjectStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                db.TestSuites.AddRange(suiteA, pendingSuite, emptySuite, suiteB);
                db.SuiteTestCases.AddRange(
                    new SuiteTestCase { SuiteId = suiteA.Id, TestCaseId = a1.Id, ExecutionOrder = 1 },
                    new SuiteTestCase { SuiteId = suiteA.Id, TestCaseId = a2.Id, ExecutionOrder = 2 },
                    new SuiteTestCase { SuiteId = pendingSuite.Id, TestCaseId = pending.Id, ExecutionOrder = 1 },
                    new SuiteTestCase { SuiteId = suiteB.Id, TestCaseId = b.Id, ExecutionOrder = 1 });

                result = new SeedResult(suiteA.Id, a1.Id, a2.Id, suiteB.Id, b.Id, emptySuite.Id);
                return Task.CompletedTask;
            });
            return result;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient ManagerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create("suite-manager", ["qa-lead"]));
        return client;
    }

    private HttpClient RoleClient(string sub, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, [role]));
        return client;
    }

    // ---------- authentication ----------

    [Fact]
    public async Task List_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        var response = await _factory.CreateClient().GetAsync($"/api/v1/projects/{_projectA}/test-suites");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Detail_Anonymous_Returns401()
    {
        var seed = await SeedOnceAsync();
        var response = await _factory.CreateClient().GetAsync($"/api/v1/test-suites/{seed.SuiteA}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- CRUD ----------

    [Fact]
    public async Task Crud_RoundTrip()
    {
        await SeedOnceAsync();
        var client = ManagerClient();

        var create = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-suites",
            new { name = "Smoke", description = "Quick set" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreatedSuite>();
        Assert.NotNull(created);

        var list = await client.GetAsync($"/api/v1/projects/{_projectA}/test-suites?search=smoke");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<SuitePage>();
        Assert.Contains(page!.Items, s => s.Id == created.Id);

        var get = await client.GetAsync($"/api/v1/test-suites/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var detail = await get.Content.ReadFromJsonAsync<SuiteDetail>();
        Assert.Equal("Smoke", detail!.Name);
        Assert.Empty(detail.Members);

        var update = await client.PutAsJsonAsync($"/api/v1/test-suites/{created.Id}",
            new { name = "Smoke v2", description = "Updated" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var archive = await client.DeleteAsync($"/api/v1/test-suites/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        var archived = await (await client.GetAsync($"/api/v1/test-suites/{created.Id}"))
            .Content.ReadFromJsonAsync<SuiteDetail>();
        Assert.Equal("Archived", archived!.Status);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409()
    {
        await SeedOnceAsync();
        var client = ManagerClient();

        var response = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-suites",
            new { name = "regression" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        await SeedOnceAsync();
        var client = ManagerClient();

        var blank = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-suites",
            new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var badStatus = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/test-suites",
            new { name = "Fresh", status = "Bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    // ---------- authorization matrix ----------

    [Fact]
    public async Task Viewer_CanRead_CannotMutate()
    {
        var seed = await SeedOnceAsync();
        var viewer = RoleClient("suite-viewer", "viewer");

        Assert.Equal(HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/v1/projects/{_projectA}/test-suites")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/v1/test-suites/{seed.SuiteA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/v1/test-suites/{seed.SuiteA}/executions")).StatusCode);
        // No terminal executions yet: 404 proves the authorized read path
        // reached the store (not a 403).
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/v1/test-suites/{seed.SuiteA}/report")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(
            $"/api/v1/test-suites/{seed.SuiteA}", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.DeleteAsync($"/api/v1/test-suites/{seed.SuiteA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/test-cases",
            new { testCaseId = seed.CaseA1, executionOrder = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/execute", new { projectId = _projectA })).StatusCode);
    }

    [Fact]
    public async Task CrossProject_Access_Returns403()
    {
        var seed = await SeedOnceAsync();
        // suite-tester is a member of project A only.
        var tester = RoleClient("suite-tester", "tester");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await tester.GetAsync($"/api/v1/test-suites/{seed.SuiteB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tester.GetAsync($"/api/v1/projects/{_projectB}/test-suites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tester.GetAsync($"/api/v1/test-suites/{seed.SuiteB}/executions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tester.GetAsync($"/api/v1/test-suites/{seed.SuiteB}/report")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PostAsJsonAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/test-cases",
            new { testCaseId = seed.CaseB, executionOrder = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PostAsJsonAsync(
            $"/api/v1/test-suites/{seed.SuiteB}/execute", new { projectId = _projectB })).StatusCode);
    }

    [Fact]
    public async Task Outsider_AllReads_Return403()
    {
        var seed = await SeedOnceAsync();
        var outsider = RoleClient("suite-outsider", "tester");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/v1/projects/{_projectA}/test-suites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/v1/test-suites/{seed.SuiteA}")).StatusCode);
    }

    // ---------- membership ----------

    [Fact]
    public async Task Membership_AddRemove_Reorder()
    {
        var seed = await SeedOnceAsync();
        var client = ManagerClient();

        // Duplicate add -> 409.
        var dup = await client.PostAsJsonAsync($"/api/v1/test-suites/{seed.SuiteA}/test-cases",
            new { testCaseId = seed.CaseA1, executionOrder = 3 });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // Remove unknown member -> 404.
        var missing = await client.DeleteAsync($"/api/v1/test-suites/{seed.SuiteA}/test-cases/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Remove one member, re-add it, then swap the order.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/test-suites/{seed.SuiteA}/test-cases/{seed.CaseA2}")).StatusCode);
        var readd = await client.PostAsJsonAsync($"/api/v1/test-suites/{seed.SuiteA}/test-cases",
            new { testCaseId = seed.CaseA2, executionOrder = 2 });
        Assert.Equal(HttpStatusCode.OK, readd.StatusCode);
        var added = await readd.Content.ReadFromJsonAsync<MemberResult>();
        Assert.Equal(2, added!.ExecutionOrder);

        // Subset reorder -> 400.
        var subset = await client.PutAsJsonAsync($"/api/v1/test-suites/{seed.SuiteA}/test-cases/order",
            new { members = new[] { new { testCaseId = seed.CaseA1, executionOrder = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, subset.StatusCode);

        // Exact swap -> 204 + deterministic order.
        var swap = await client.PutAsJsonAsync($"/api/v1/test-suites/{seed.SuiteA}/test-cases/order",
            new
            {
                members = new[]
                {
                    new { testCaseId = seed.CaseA1, executionOrder = 2 },
                    new { testCaseId = seed.CaseA2, executionOrder = 1 },
                },
            });
        Assert.Equal(HttpStatusCode.NoContent, swap.StatusCode);

        var detail = await (await client.GetAsync($"/api/v1/test-suites/{seed.SuiteA}"))
            .Content.ReadFromJsonAsync<SuiteDetail>();
        Assert.Equal(2, detail!.Members.Count);
        Assert.Equal(seed.CaseA2, detail.Members[0].TestCaseId);
        Assert.Equal(seed.CaseA1, detail.Members[1].TestCaseId);
        Assert.Equal("SUITE-A2", detail.Members[0].TestKey);
    }

    // ---------- manual execution ----------

    [Fact]
    public async Task Execute_EmptySuite_Returns409()
    {
        var seed = await SeedOnceAsync();
        var client = ManagerClient();

        var response = await client.PostAsJsonAsync($"/api/v1/test-suites/{seed.EmptySuite}/execute",
            new { projectId = _projectA });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Execute_NoApprovedVersion_Returns400()
    {
        var seed = await SeedOnceAsync();
        var client = ManagerClient();

        Guid pendingSuite = Guid.Empty;
        await _factory.SeedAsync(db =>
        {
            pendingSuite = db.TestSuites.First(s => s.Name == "Pending only" && s.ProjectId == _projectA).Id;
            return Task.CompletedTask;
        });
        _ = seed;

        var response = await client.PostAsJsonAsync($"/api/v1/test-suites/{pendingSuite}/execute",
            new { projectId = _projectA });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Execute_ArchivedSuite_Returns409()
    {
        var seed = await SeedOnceAsync();
        var client = ManagerClient();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/test-suites/{seed.SuiteA}")).StatusCode);

        var response = await client.PostAsJsonAsync($"/api/v1/test-suites/{seed.SuiteA}/execute",
            new { projectId = _projectA });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Execute_Success_FansOut_And_HistoryShows()
    {
        var seed = await SeedOnceAsync();
        var client = ManagerClient();
        var before = _factory.WorkflowCoordinator.Started.Count;

        var response = await client.PostAsJsonAsync($"/api/v1/test-suites/{seed.SuiteA}/execute",
            new { projectId = _projectA });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ExecuteResult>();
        Assert.NotNull(result);
        Assert.Equal(2, result.TestCount);
        Assert.Equal(seed.SuiteA, result.SuiteId);

        // One workflow per suite member through the existing seam.
        Assert.Equal(before + 2, _factory.WorkflowCoordinator.Started.Count);

        var history = await (await client.GetAsync($"/api/v1/test-suites/{seed.SuiteA}/executions"))
            .Content.ReadFromJsonAsync<HistoryPage>();
        Assert.True(history!.TotalCount >= 2);
        Assert.All(history.Items, i => Assert.Equal("Manual", i.TriggerType));
    }

    // ---------- history / report ----------

    [Fact]
    public async Task Report_WithTerminalExecution_Aggregates()
    {
        var seed = await SeedOnceAsync();
        await _factory.SeedAsync(db =>
        {
            var execution = new Execution
            {
                ProjectId = _projectA, SuiteId = seed.SuiteA, Status = ExecutionStatus.Passed,
                TriggerType = TriggerType.Manual, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Executions.Add(execution);
            db.ExecutionTests.AddRange(
                new ExecutionTest { ExecutionId = execution.Id, TestCaseId = seed.CaseA1, Status = ExecutionTestStatus.Passed, DurationMs = 1200 },
                new ExecutionTest { ExecutionId = execution.Id, TestCaseId = seed.CaseA2, Status = ExecutionTestStatus.Failed, DurationMs = 800 });
            return Task.CompletedTask;
        });

        var client = ManagerClient();
        var response = await client.GetAsync($"/api/v1/test-suites/{seed.SuiteA}/report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<SuiteReport>();
        Assert.NotNull(report);
        Assert.Equal(1, report.TotalExecutions);
        Assert.Equal(1, report.PassedCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(50, report.PassRate);
        Assert.Equal(2000, report.TotalDurationMs);

        // Viewers hold reports.read: the same report is readable read-only.
        var viewerReport = await RoleClient("suite-viewer", "viewer")
            .GetAsync($"/api/v1/test-suites/{seed.SuiteA}/report");
        Assert.Equal(HttpStatusCode.OK, viewerReport.StatusCode);
    }

    [Fact]
    public async Task Report_WithoutExecutions_Returns404()
    {
        var seed = await SeedOnceAsync();
        var client = ManagerClient();

        var response = await client.GetAsync($"/api/v1/test-suites/{seed.EmptySuite}/report");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownSuite_AsAdmin_Returns404()
    {
        await SeedOnceAsync();
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("suite-admin", ["admin"]));

        var response = await admin.GetAsync($"/api/v1/test-suites/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
