using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 8: project-scoped read-only dashboard and reports — auth matrix,
/// isolation, aggregates, filters, pagination, secret hygiene, no mutation.
/// </summary>
public sealed class DashboardApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly Guid _projectEmpty = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public DashboardApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb, pe) = (_projectA, _projectB, _projectEmpty);
            await _factory.SeedAsync(async db =>
            {
                if (await db.Projects.AnyAsync(p => p.Id == pa)) return;
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var mgr = new User { ExternalIdentityId = "db-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "db-tester", Email = "t@x", DisplayName = "T" };
                var viewer = new User { ExternalIdentityId = "db-viewer", Email = "v@x", DisplayName = "V" };
                var outsider = new User { ExternalIdentityId = "db-outsider", Email = "o@x", DisplayName = "O" };
                var admin = new User { ExternalIdentityId = "db-admin", Email = "a@x", DisplayName = "A" };
                db.Users.AddRange(mgr, tester, viewer, outsider, admin);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Dash Alpha", Key = "DAA" },
                    new Project { Id = pb, Name = "Dash Beta", Key = "DAB" },
                    new Project { Id = pe, Name = "Dash Empty", Key = "DAE" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId },
                    new ProjectMember { ProjectId = pe, UserId = mgr.Id, RoleId = QaLeadRoleId });

                var now = DateTimeOffset.UtcNow;
                // Project A: two cases (one approved, one pending).
                var approved = new TestCase
                {
                    ProjectId = pa, TestKey = "LOGIN-001", Title = "Login",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var pending = new TestCase
                {
                    ProjectId = pa, TestKey = "SEARCH-001", Title = "Search",
                    Priority = Priority.Medium, Status = TestCaseStatus.Draft, SourceType = "manual",
                };
                db.TestCases.AddRange(approved, pending);
                db.TestCaseVersions.AddRange(
                    new TestCaseVersion { TestCaseId = approved.Id, VersionNumber = 1, ReviewStatus = ReviewStatus.Approved },
                    new TestCaseVersion { TestCaseId = pending.Id, VersionNumber = 1, ReviewStatus = ReviewStatus.Pending });

                // Project A executions: 2 passed, 1 failed, 1 running.
                var e1 = new Execution { ProjectId = pa, Status = ExecutionStatus.Passed, CreatedAt = now.AddDays(-2) };
                var e2 = new Execution { ProjectId = pa, Status = ExecutionStatus.Passed, CreatedAt = now.AddDays(-1) };
                var e3 = new Execution { ProjectId = pa, Status = ExecutionStatus.Failed, CreatedAt = now.AddDays(-1) };
                var e4 = new Execution { ProjectId = pa, Status = ExecutionStatus.Running, CreatedAt = now };
                db.Executions.AddRange(e1, e2, e3, e4);
                var t1 = new ExecutionTest { ExecutionId = e1.Id, TestCaseId = approved.Id, Status = ExecutionTestStatus.Passed, FailureClassification = FailureClassification.Unknown };
                var t2 = new ExecutionTest { ExecutionId = e2.Id, TestCaseId = approved.Id, Status = ExecutionTestStatus.Passed, FailureClassification = FailureClassification.Unknown };
                var t3 = new ExecutionTest { ExecutionId = e3.Id, TestCaseId = approved.Id, Status = ExecutionTestStatus.Failed, FailureClassification = FailureClassification.ApplicationDefect, DurationMs = 1200 };
                var t4 = new ExecutionTest { ExecutionId = e4.Id, TestCaseId = pending.Id, Status = ExecutionTestStatus.Running, FailureClassification = FailureClassification.Unknown };
                db.ExecutionTests.AddRange(t1, t2, t3, t4);

                // Project A defects: open/high + resolved/medium.
                var defectA = new Defect { ProjectId = pa, ExecutionTestId = t3.Id, Title = "Login 500", Severity = Severity.High, Status = DefectStatus.Open, RootCauseType = FailureClassification.ApplicationDefect, CreatedAt = now.AddDays(-1) };
                db.Defects.AddRange(
                    defectA,
                    new Defect { ProjectId = pa, ExecutionTestId = t3.Id, Title = "Flaky timing", Severity = Severity.Medium, Status = DefectStatus.Resolved, RootCauseType = FailureClassification.TestFailure, CreatedAt = now.AddDays(-5) });

                // Project A tickets: one synced, one failed.
                db.Tickets.AddRange(
                    new Ticket { ProjectId = pa, DefectId = defectA.Id, Provider = "jira", ExternalTicketId = "10001", ExternalKey = "DAA-1", ExternalUrl = "https://jira.test/browse/DAA-1", Title = "Login 500", SyncStatus = TicketSyncStatus.Synced, CreatedAt = now.AddDays(-1) },
                    new Ticket { ProjectId = pa, DefectId = defectA.Id, Provider = "jira", Title = "retry", SyncStatus = TicketSyncStatus.Failed, LastError = "down", CreatedAt = now });

                // Project A audit trail (safe actions only).
                db.AuditEvents.AddRange(
                    new AuditEvent { Action = "defect.created", EntityType = "defect", EntityId = defectA.Id.ToString(), ProjectId = pa, CreatedAt = now.AddDays(-1) },
                    new AuditEvent { Action = "ticket.created", EntityType = "ticket", EntityId = "t1", ProjectId = pa, CreatedAt = now.AddHours(-2) });

                // Project B: isolated data that must never leak into A.
                var other = new TestCase { ProjectId = pb, TestKey = "OTHER-001", Title = "Other", Priority = Priority.Low, Status = TestCaseStatus.Active, SourceType = "manual" };
                db.TestCases.Add(other);
                var eb = new Execution { ProjectId = pb, Status = ExecutionStatus.Failed, CreatedAt = now.AddDays(-1) };
                db.Executions.Add(eb);
                var tb = new ExecutionTest { ExecutionId = eb.Id, TestCaseId = other.Id, Status = ExecutionTestStatus.Failed, FailureClassification = FailureClassification.EnvironmentFailure };
                db.ExecutionTests.Add(tb);
                db.Defects.Add(new Defect { ProjectId = pb, ExecutionTestId = tb.Id, Title = "Beta bug", Severity = Severity.Critical, Status = DefectStatus.Open });

                await db.SaveChangesAsync();
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

    private static JsonElement Prop(JsonDocument doc, params string[] path)
    {
        var current = doc.RootElement;
        foreach (var segment in path)
            current = current.GetProperty(segment);
        return current;
    }

    [Fact]
    public async Task Summary_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync($"/api/v1/projects/{_projectA}/dashboard/summary")).StatusCode);
    }

    [Fact]
    public async Task Summary_Outsider_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("db-outsider", ["tester"]).GetAsync($"/api/v1/projects/{_projectA}/dashboard/summary")).StatusCode);
    }

    [Fact]
    public async Task Summary_CrossProject_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("db-manager", ["qa-lead"]).GetAsync($"/api/v1/projects/{_projectB}/dashboard/summary")).StatusCode);
    }

    [Fact]
    public async Task Summary_ReturnsRealAggregates()
    {
        await SeedOnceAsync();
        var response = await Client("db-manager", ["qa-lead"])
            .GetAsync($"/api/v1/projects/{_projectA}/dashboard/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);
        Assert.Equal(2, Prop(doc!, "testCases", "total").GetInt32());
        Assert.Equal(1, Prop(doc!, "testCases", "approved").GetInt32());
        Assert.Equal(4, Prop(doc!, "executions", "total").GetInt32());
        Assert.Equal(2, Prop(doc!, "executions", "passed").GetInt32());
        Assert.Equal(1, Prop(doc!, "executions", "failed").GetInt32());
        // Pass rate = 2 passed / 3 terminal (running excluded).
        Assert.Equal(2.0 / 3.0, Prop(doc!, "executions", "passRate").GetDouble(), precision: 5);
        Assert.Equal(2, Prop(doc!, "defects", "total").GetInt32());
        Assert.Equal(1, Prop(doc!, "defects", "open").GetInt32());
        Assert.Equal(1, Prop(doc!, "defects", "highSeverity").GetInt32());
        Assert.Equal(2, Prop(doc!, "tickets", "total").GetInt32());
        Assert.Equal(1, Prop(doc!, "tickets", "synced").GetInt32());
        Assert.Equal(1, Prop(doc!, "tickets", "failed").GetInt32());
        Assert.Equal(4, Prop(doc!, "recentExecutions").GetArrayLength());
        Assert.Equal(2, Prop(doc!, "recentDefects").GetArrayLength());
        Assert.Equal(2, Prop(doc!, "recentActivity").GetArrayLength());
        var body = doc!.RootElement.GetRawText();
        Assert.DoesNotContain("SecretReference", body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Summary_ProjectB_Isolated()
    {
        await SeedOnceAsync();
        var admin = Client("db-admin", ["admin"]);
        using var doc = await (await admin.GetAsync($"/api/v1/projects/{_projectB}/dashboard/summary"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(doc!, "testCases", "total").GetInt32());
        Assert.Equal(1, Prop(doc!, "executions", "failed").GetInt32());
        Assert.Equal(1, Prop(doc!, "defects", "total").GetInt32());
        Assert.Equal(0, Prop(doc!, "tickets", "total").GetInt32());
    }

    [Fact]
    public async Task Summary_EmptyProject_ReturnsEmptyShape()
    {
        await SeedOnceAsync();
        using var doc = await (await Client("db-manager", ["qa-lead"])
                .GetAsync($"/api/v1/projects/{_projectEmpty}/dashboard/summary"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(0, Prop(doc!, "testCases", "total").GetInt32());
        Assert.Equal(0, Prop(doc!, "executions", "total").GetInt32());
        Assert.Equal(JsonValueKind.Null, Prop(doc!, "executions", "passRate").ValueKind);
    }

    [Fact]
    public async Task Summary_InvalidDateRange_Returns400()
    {
        await SeedOnceAsync();
        var client = Client("db-manager", ["qa-lead"]);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/projects/{_projectA}/dashboard/summary?from=2026-09-29&to=2026-09-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/projects/{_projectA}/dashboard/summary?from=2020-01-01&to=2026-09-29")).StatusCode);
    }

    [Fact]
    public async Task Trend_And_Breakdown_AreDatabaseBacked()
    {
        await SeedOnceAsync();
        var client = Client("db-viewer", ["viewer"]);
        using var trend = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/dashboard/execution-trend?from={DateTimeOffset.UtcNow.AddDays(-7):yyyy-MM-dd}"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("day", Prop(trend!, "granularity").GetString());
        var points = Prop(trend!, "points").EnumerateArray().ToList();
        Assert.True(points.Count >= 7);
        Assert.Equal(4, points.Sum(p => p.GetProperty("total").GetInt32()));

        using var breakdown = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/dashboard/failure-breakdown"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        // Deterministic execution_tests classification over terminal tests only:
        // 2 Unknown (passed) + 1 ApplicationDefect (failed); the running test is excluded.
        var items = Prop(breakdown!, "items").EnumerateArray().ToList();
        Assert.Equal(3, Prop(breakdown!, "total").GetInt32());
        Assert.Contains(items, i => i.GetProperty("name").GetString() == "ApplicationDefect"
            && i.GetProperty("count").GetInt32() == 1);
    }

    [Fact]
    public async Task Reports_Paginate_And_Filter()
    {
        await SeedOnceAsync();
        var client = Client("db-tester", ["tester"]);
        using var failed = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/reports/executions?status=Failed&page=1&pageSize=10"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(failed!, "totalCount").GetInt32());
        Assert.All(Prop(failed!, "items").EnumerateArray(),
            i => Assert.Equal("Failed", i.GetProperty("status").GetString()));

        using var defects = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/reports/defects?severity=High"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(defects!, "totalCount").GetInt32());
        Assert.Equal("DAA-1", Prop(defects!, "items").EnumerateArray().First().GetProperty("jiraKey").GetString());

        using var tickets = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/reports/tickets?syncStatus=Synced"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(tickets!, "totalCount").GetInt32());
        var ticketBody = tickets!.RootElement.GetRawText();
        Assert.DoesNotContain("SecretReference", ticketBody);
    }

    [Fact]
    public async Task Reports_WithoutReportsRead_Returns403()
    {
        await SeedOnceAsync();
        // Unknown role: no permissions at all, so the report boundary denies.
        var limited = _factory.CreateClient();
        limited.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("db-limited", ["custom-limited"]));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await limited.GetAsync($"/api/v1/projects/{_projectA}/reports/executions")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_DoesNotMutate()
    {
        await SeedOnceAsync();
        var client = Client("db-manager", ["qa-lead"]);
        await client.GetAsync($"/api/v1/projects/{_projectA}/dashboard/summary");
        await client.GetAsync($"/api/v1/projects/{_projectA}/dashboard/execution-trend");
        await client.GetAsync($"/api/v1/projects/{_projectA}/reports/executions");
        await _factory.SeedAsync(db =>
        {
            Assert.Equal(4, db.Executions.Count(e => e.ProjectId == _projectA));
            Assert.Equal(2, db.Defects.Count(d => d.ProjectId == _projectA));
            Assert.Equal(2, db.Tickets.Count(t => t.ProjectId == _projectA));
            return Task.CompletedTask;
        });
    }
}
