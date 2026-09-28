using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 6: defect API — human-owned creation from failed executions,
/// lifecycle, audit, isolation, and secret hygiene (docs/06 §9).
/// </summary>
public sealed class DefectApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    private Guid _failedExecutionA = Guid.Empty;
    private Guid _passedExecutionA = Guid.Empty;

    public DefectApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(async db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }

                var mgr = new User { ExternalIdentityId = "df-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "df-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "df-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "df-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Defect Alpha", Key = "DFA" },
                    new Project { Id = pb, Name = "Defect Beta", Key = "DFB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                var testCase = new TestCase
                {
                    ProjectId = pa, TestKey = "LOGIN-001", Title = "Login works",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(testCase);
                db.TestCaseVersions.Add(new TestCaseVersion
                {
                    TestCaseId = testCase.Id, VersionNumber = 1, SourceCode = "// v1",
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                });

                var failed = new Execution { ProjectId = pa, Status = ExecutionStatus.Failed };
                db.Executions.Add(failed);
                db.ExecutionTests.Add(new ExecutionTest
                {
                    ExecutionId = failed.Id, TestCaseId = testCase.Id,
                    Status = ExecutionTestStatus.Failed,
                    FailureClassification = FailureClassification.ApplicationDefect,
                    ErrorType = "HttpError", ErrorMessage = "HTTP 500 on login.",
                });
                var passed = new Execution { ProjectId = pa, Status = ExecutionStatus.Passed };
                db.Executions.Add(passed);
                db.ExecutionTests.Add(new ExecutionTest
                {
                    ExecutionId = passed.Id, TestCaseId = testCase.Id,
                    Status = ExecutionTestStatus.Passed,
                });

                await db.SaveChangesAsync();
                _failedExecutionA = failed.Id;
                _passedExecutionA = passed.Id;
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(string sub, string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private sealed record DefectPayload(
        Guid Id, Guid ProjectId, string Title, string Severity, string Status,
        string? FailureClassification, Guid? ExecutionId, string? TestKey);
    private sealed record DetailPayload(
        Guid Id, Guid ProjectId, string Title, string Severity, string Status,
        string? FailureClassification, Guid? ExecutionId, Guid? ExecutionTestId,
        string? TestKey, Guid? TestCaseVersionId, Guid? FailureAnalysisId);
    private sealed record PagePayload(IReadOnlyList<DefectPayload> Items, int TotalCount);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    [Fact]
    public async Task Create_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = _failedExecutionA, title = "X" })).StatusCode);
    }

    [Fact]
    public async Task Create_ViewerAndOutsider_Return403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("df-viewer", ["viewer"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = _failedExecutionA, title = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("df-outsider", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = _failedExecutionA, title = "X" })).StatusCode);
    }

    [Fact]
    public async Task Create_Success_DerivesRelationships()
    {
        await SeedOnceAsync();
        var response = await Client("df-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new
            {
                executionId = _failedExecutionA,
                title = "Login returns 500",
                description = "Staging login fails after deploy.",
                severity = "High",
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DetailPayload>();
        Assert.NotNull(created);
        Assert.Equal("High", created!.Severity);
        Assert.Equal("Open", created.Status);
        Assert.Equal("ApplicationDefect", created.FailureClassification);
        Assert.Equal(_failedExecutionA, created.ExecutionId);
        Assert.Equal("LOGIN-001", created.TestKey);

        await _factory.SeedAsync(async db =>
        {
            var row = await db.Defects.FirstAsync(d => d.Id == created.Id);
            Assert.Equal(_projectA, row.ProjectId);
            Assert.NotNull(row.ExecutionTestId);
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "defect.created");
        });
    }

    [Fact]
    public async Task Create_PassedExecution_Returns409()
    {
        await SeedOnceAsync();
        var response = await Client("df-tester", ["tester"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = _passedExecutionA, title = "X" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        await SeedOnceAsync();
        var response = await Client("df-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = _failedExecutionA, title = "", severity = "Bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var fields = doc!.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToHashSet();
        Assert.Contains("title", fields);
        Assert.Contains("severity", fields);
    }

    [Fact]
    public async Task Create_UnknownExecution_Returns403_NotFound()
    {
        // Project-scoped writes never reveal existence across the boundary.
        await SeedOnceAsync();
        var response = await Client("df-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = Guid.NewGuid(), title = "X" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_Update_Status_And_Audit()
    {
        await SeedOnceAsync();
        var client = Client("df-manager", ["qa-lead"]);
        var created = await (await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = _failedExecutionA, title = "Login 500" }))
            .Content.ReadFromJsonAsync<DetailPayload>();
        Assert.NotNull(created);

        var updated = await (await client.PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects/{created!.Id}",
            new { title = "Login 500 (confirmed)", severity = "Critical" }))
            .Content.ReadFromJsonAsync<DetailPayload>();
        Assert.Equal("Critical", updated!.Severity);

        var progressed = await (await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects/{created.Id}/status",
            new { status = "InProgress" })).Content.ReadFromJsonAsync<DetailPayload>();
        Assert.Equal("InProgress", progressed!.Status);

        var invalid = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects/{created.Id}/status",
            new { status = "Bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await _factory.SeedAsync(db =>
        {
            var actions = db.AuditEvents.Select(e => e.Action).ToList();
            Assert.Contains("defect.updated", actions);
            Assert.Contains("defect.severity_changed", actions);
            Assert.Contains("defect.status_changed", actions);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task List_Detail_CrossProject_Isolated()
    {
        await SeedOnceAsync();
        var client = Client("df-manager", ["qa-lead"]);
        var created = await (await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/defects",
            new { executionId = _failedExecutionA, title = "Login 500" }))
            .Content.ReadFromJsonAsync<DetailPayload>();

        var page = await client.GetFromJsonAsync<PagePayload>(
            $"/api/v1/projects/{_projectA}/defects?page=1&pageSize=25");
        Assert.Equal(1, page!.TotalCount);
        Assert.Equal("LOGIN-001", page.Items.Single().TestKey);

        var detail = await client.GetFromJsonAsync<DetailPayload>(
            $"/api/v1/projects/{_projectA}/defects/{created!.Id}");
        Assert.Equal("Login 500", detail!.Title);

        // Same defect id through another project path is denied.
        var outsider = Client("df-outsider", ["tester"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(
            $"/api/v1/projects/{_projectB}/defects/{created.Id}")).StatusCode);
    }
}
