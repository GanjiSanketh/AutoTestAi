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
/// Slice 7: manual Jira creation — auth, idempotency, config safety, audit,
/// defect immutability. Jira HTTP is faked; no live Jira tenant.
/// </summary>
public sealed class TicketApiTests : IClassFixture<TicketApiTests.TicketFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public sealed class FakeJira : IJiraTicketProvider
    {
        public int Calls;
        public Func<JiraCreateRequest, Task<JiraCreateResult>>? Handler;
        public Task<JiraCreateResult> CreateIssueAsync(JiraCreateRequest request, string email, string apiToken, CancellationToken ct)
        {
            Calls++;
            if (Handler is not null) return Handler(request);
            return Task.FromResult(new JiraCreateResult("10001", "ABC-123", "https://jira.test/browse/ABC-123"));
        }
    }

    public sealed class TicketFactory : Slice1ApiFactory
    {
        public FakeJira Jira { get; } = new();

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

    private readonly TicketFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private Guid _defectA = Guid.Empty;
    private Guid _defectB = Guid.Empty;

    public TicketApiTests(TicketFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
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
                var mgr = new User { ExternalIdentityId = "tk-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "tk-tester", Email = "t@x", DisplayName = "T" };
                var viewer = new User { ExternalIdentityId = "tk-viewer", Email = "v@x", DisplayName = "V" };
                var outsider = new User { ExternalIdentityId = "tk-outsider", Email = "o@x", DisplayName = "O" };
                var admin = new User { ExternalIdentityId = "tk-admin", Email = "a@x", DisplayName = "A" };
                db.Users.AddRange(mgr, tester, viewer, outsider, admin);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Ticket Alpha", Key = "TKA" },
                    new Project { Id = pb, Name = "Ticket Beta", Key = "TKB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                var tc = new TestCase
                {
                    ProjectId = pa, TestKey = "LOGIN-001", Title = "Login",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(tc);
                var exec = new Execution { ProjectId = pa, Status = ExecutionStatus.Failed };
                db.Executions.Add(exec);
                var test = new ExecutionTest
                {
                    ExecutionId = exec.Id, TestCaseId = tc.Id, Status = ExecutionTestStatus.Failed,
                    FailureClassification = FailureClassification.ApplicationDefect,
                };
                db.ExecutionTests.Add(test);
                var defectA = new Defect
                {
                    ProjectId = pa, ExecutionTestId = test.Id, Title = "Login 500",
                    Severity = Severity.High, Status = DefectStatus.Open,
                };
                db.Defects.Add(defectA);
                var execB = new Execution { ProjectId = pb, Status = ExecutionStatus.Failed };
                db.Executions.Add(execB);
                var testB = new ExecutionTest
                {
                    ExecutionId = execB.Id, TestCaseId = tc.Id, Status = ExecutionTestStatus.Failed,
                };
                db.ExecutionTests.Add(testB);
                var defectB = new Defect { ProjectId = pb, ExecutionTestId = testB.Id, Title = "Other", Severity = Severity.Low };
                db.Defects.Add(defectB);
                db.Integrations.Add(new Integration
                {
                    ProjectId = pa, Provider = "jira", IntegrationType = "ticketing",
                    Configuration = JsonDocument.Parse(
                        """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
                    SecretReference = "integration-secret-xyz",
                    Status = IntegrationStatus.Active,
                });
                await db.SaveChangesAsync();
                _defectA = defectA.Id;
                _defectB = defectB.Id;
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

    private sealed record TicketPayload(
        Guid Id, Guid ProjectId, Guid? DefectId, string Provider,
        string? ExternalId, string? ExternalKey, string? ExternalUrl,
        string SyncStatus, bool AlreadyExisted);
    private sealed record StatusPayload(
        string Provider, bool Configured, bool Enabled, string? ProjectKey, string? BaseUrl);

    [Fact]
    public async Task Create_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient()
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{_defectA}/ticket", null)).StatusCode);
    }

    [Fact]
    public async Task Create_ViewerAndOutsider_Return403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("tk-viewer", ["viewer"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{_defectA}/ticket", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("tk-outsider", ["tester"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{_defectA}/ticket", null)).StatusCode);
    }

    [Fact]
    public async Task Create_Success_Persists_AndIsIdempotent()
    {
        await SeedOnceAsync();
        _factory.Jira.Handler = null;
        var before = _factory.Jira.Calls;
        var response = await Client("tk-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{_defectA}/ticket", null);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"got {response.StatusCode}");
        var ticket = await response.Content.ReadFromJsonAsync<TicketPayload>();
        Assert.NotNull(ticket);
        Assert.Equal("ABC-123", ticket!.ExternalKey);
        Assert.Equal("Synced", ticket.SyncStatus);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("integration-secret-xyz", body);

        // Second call is idempotent.
        var second = await Client("tk-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{_defectA}/ticket", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var again = await second.Content.ReadFromJsonAsync<TicketPayload>();
        Assert.Equal(ticket.Id, again!.Id);
        Assert.True(again.AlreadyExisted);
        Assert.Equal(before + 1, _factory.Jira.Calls);

        await _factory.SeedAsync(async db =>
        {
            var row = await db.Tickets.FirstAsync(t => t.Id == ticket.Id);
            Assert.Equal(_projectA, row.ProjectId);
            Assert.Equal(_defectA, row.DefectId);
            Assert.NotNull(row.IntegrationId);
            Assert.Equal("ABC-123", row.ExternalKey);
            var defect = await db.Defects.FirstAsync(d => d.Id == _defectA);
            Assert.Equal(DefectStatus.Open, defect.Status);
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "ticket.created");
            foreach (var meta in db.AuditEvents.Select(e => e.MetadataJson))
                Assert.DoesNotContain("integration-secret-xyz", meta ?? string.Empty);
        });
    }

    [Fact]
    public async Task Create_CrossProject_Blocked()
    {
        await SeedOnceAsync();
        var response = await Client("tk-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectB}/defects/{_defectA}/ticket", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_MissingIntegration_Returns409()
    {
        await SeedOnceAsync();
        var response = await Client("tk-admin", ["admin"])
            .PostAsync($"/api/v1/projects/{_projectB}/defects/{_defectB}/ticket", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_JiraFailure_AllowsRetry()
    {
        await SeedOnceAsync();
        // Use a fresh defect to avoid idempotent-hit from other tests.
        Guid fresh = Guid.Empty;
        await _factory.SeedAsync(async db =>
        {
            var any = await db.ExecutionTests.FirstAsync(t => t.ExecutionId == db.Executions.Where(e => e.ProjectId == _projectA).Select(e => e.Id).First());
            var d = new Defect { ProjectId = _projectA, ExecutionTestId = any.Id, Title = "Retry me", Severity = Severity.Medium };
            db.Defects.Add(d);
            await db.SaveChangesAsync();
            fresh = d.Id;
        });
        _factory.Jira.Handler = _ => throw new JiraProviderException(JiraErrorKind.Unavailable, "down");
        var failed = await Client("tk-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{fresh}/ticket", null);
        Assert.True(failed.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable, $"got {failed.StatusCode}");

        _factory.Jira.Handler = null;
        var retry = await Client("tk-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{fresh}/ticket", null);
        Assert.True(retry.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"got {retry.StatusCode}");
    }

    [Fact]
    public async Task JiraStatus_Safe_NoSecret()
    {
        await SeedOnceAsync();
        var status = await Client("tk-viewer", ["viewer"])
            .GetFromJsonAsync<StatusPayload>($"/api/v1/projects/{_projectA}/integrations/jira/status");
        Assert.NotNull(status);
        Assert.True(status!.Configured);
        Assert.Equal("ABC", status.ProjectKey);
    }

    [Fact]
    public async Task UpsertJira_RequiresAdmin_AndRedactsSecret()
    {
        await SeedOnceAsync();
        var tester = Client("tk-tester", ["tester"]);
        var denied = await tester.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/jira",
            new { baseUrl = "https://jira.test", projectKey = "ABC", email = "qa@example.com", apiToken = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var admin = Client("tk-admin", ["admin"]);
        var ok = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/jira",
            new { baseUrl = "https://jira.test", projectKey = "ABC", email = "qa@example.com", apiToken = "new-secret", issueType = "Bug", enabled = true });
        Assert.True(ok.IsSuccessStatusCode, $"got {ok.StatusCode}");
        var body = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("new-secret", body);
    }
}
