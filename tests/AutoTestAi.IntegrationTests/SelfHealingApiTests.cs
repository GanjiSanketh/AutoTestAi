using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 2 Slice 11: self-healing policy administration (auth, isolation),
/// execution healing reads, and the worker machine plane (lease-token auth,
/// policy gating). AI is the deterministic stub; no live providers.
/// </summary>
public sealed class SelfHealingApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private Guid _executionA = Guid.Empty;
    private Guid _executionB = Guid.Empty;
    private Guid _testA = Guid.Empty;
    private string _workerRefA = string.Empty;
    private Guid _leaseTokenA = Guid.NewGuid();

    public SelfHealingApiTests(Slice1ApiFactory factory) => _factory = factory;

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
                var manager = new User { ExternalIdentityId = "sh-manager", Email = "m@x", DisplayName = "M" };
                var viewer = new User { ExternalIdentityId = "sh-viewer", Email = "v@x", DisplayName = "V" };
                var admin = new User { ExternalIdentityId = "sh-admin", Email = "a@x", DisplayName = "A" };
                var outsider = new User { ExternalIdentityId = "sh-outsider", Email = "o@x", DisplayName = "O" };
                db.Users.AddRange(manager, viewer, admin, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Heal Alpha", Key = "SHA" },
                    new Project { Id = pb, Name = "Heal Beta", Key = "SHB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = manager.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                var tc = new TestCase
                {
                    ProjectId = pa, TestKey = "HEAL-001", Title = "Healing",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(tc);
                var version = new TestCaseVersion
                {
                    TestCaseId = tc.Id, VersionNumber = 1,
                    ReviewStatus = ReviewStatus.Approved,
                    StructuredSteps = System.Text.Json.JsonDocument.Parse(
                        """[{"order": 1, "action": "click", "target": "css=#old"}]"""),
                };
                db.TestCaseVersions.Add(version);
                var execA = new Execution { ProjectId = pa, Status = ExecutionStatus.Failed };
                var execB = new Execution { ProjectId = pb, Status = ExecutionStatus.Failed };
                db.Executions.AddRange(execA, execB);
                var testA = new ExecutionTest
                {
                    ExecutionId = execA.Id, TestCaseId = tc.Id, TestCaseVersionId = version.Id,
                    Status = ExecutionTestStatus.Failed,
                    FailureClassification = FailureClassification.AutomationFailure,
                };
                db.ExecutionTests.Add(testA);
                await db.SaveChangesAsync();

                var worker = new GridWorker
                {
                    WorkerKey = $"sh-worker-{pa:N}", DisplayName = "SH",
                    Status = GridWorkerStatus.Available, Capacity = 2,
                    BaseUrl = "http://localhost:9",
                };
                db.GridWorkers.Add(worker);
                await db.SaveChangesAsync();
                var lease = new GridAssignment
                {
                    ExecutionId = execA.Id, ExecutionTestId = testA.Id, WorkerId = worker.Id,
                    Status = GridAssignmentStatus.Running,
                    WorkerAssignmentRef = testA.Id.ToString("N"),
                    AssignmentToken = _leaseTokenA,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                };
                db.GridAssignments.Add(lease);
                await db.SaveChangesAsync();
                testA.StartedAssignmentId = lease.Id;
                testA.AssignmentId = lease.Id;
                testA.AssignmentToken = lease.AssignmentToken;
                _executionA = execA.Id;
                _executionB = execB.Id;
                _testA = testA.Id;
                _workerRefA = lease.WorkerAssignmentRef;
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

    private sealed record PolicyPayload(
        Guid ProjectId, bool Enabled, bool AiFallbackEnabled, int MaxAttemptsPerStep);
    private sealed record AttemptPayload(
        Guid Id, Guid ExecutionId, int StepOrder, string StepAction,
        string? OriginalValue, string? RecoveredValue, string Status, bool WasApplied, bool IsAiAssisted);
    private sealed record SuggestPayload(IReadOnlyList<CandidatePayload> Candidates);
    private sealed record CandidatePayload(string Strategy, string Value, string? Reason, decimal? Confidence);

    [Fact]
    public async Task Policy_Anonymous_401_MemberRead_AdminWrite()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient()
            .GetAsync($"/api/v1/projects/{_projectA}/self-healing-policy")).StatusCode);

        // qa-lead holds executions.read but not settings.manage.
        var denied = await Client("sh-manager", ["qa-lead"]).PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/self-healing-policy", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var saved = await Client("sh-admin", ["admin"]).PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/self-healing-policy",
            new { enabled = true, aiFallbackEnabled = false });
        Assert.True(saved.IsSuccessStatusCode);
        var policy = await saved.Content.ReadFromJsonAsync<PolicyPayload>();
        Assert.True(policy!.Enabled);
        Assert.Equal(1, policy.MaxAttemptsPerStep);

        var read = await Client("sh-viewer", ["viewer"])
            .GetAsync($"/api/v1/projects/{_projectA}/self-healing-policy");
        Assert.True(read.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Policy_Validation_RejectsBadInput()
    {
        await SeedOnceAsync();
        var admin = Client("sh-admin", ["admin"]);
        var aiWithoutEnabled = await admin.PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/self-healing-policy",
            new { enabled = false, aiFallbackEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, aiWithoutEnabled.StatusCode);

        var badScore = await admin.PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/self-healing-policy",
            new { enabled = true, minDeterministicScore = 500 });
        Assert.Equal(HttpStatusCode.BadRequest, badScore.StatusCode);

        var badStrategy = await admin.PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/self-healing-policy",
            new { enabled = true, allowedStrategies = new[] { "javascript" } });
        Assert.Equal(HttpStatusCode.BadRequest, badStrategy.StatusCode);
    }

    [Fact]
    public async Task Policy_CrossProject_Isolated()
    {
        await SeedOnceAsync();
        // Outsider is no member of either project: existence is never revealed.
        var outsider = await Client("sh-outsider", ["viewer"])
            .GetAsync($"/api/v1/projects/{_projectA}/self-healing-policy");
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);

        // Member of A learns nothing about B.
        var cross = await Client("sh-manager", ["qa-lead"])
            .GetAsync($"/api/v1/projects/{_projectB}/self-healing-policy");
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
    }

    [Fact]
    public async Task HealingReads_Unknown_And_CrossProject_404()
    {
        await SeedOnceAsync();
        var viewer = Client("sh-viewer", ["viewer"]);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer
            .GetAsync($"/api/v1/projects/{_projectA}/executions/{Guid.NewGuid()}/healing")).StatusCode);
        // Execution B belongs to project B: requested under A → 404, not a leak.
        Assert.Equal(HttpStatusCode.NotFound, (await viewer
            .GetAsync($"/api/v1/projects/{_projectA}/executions/{_executionB}/healing")).StatusCode);
    }

    [Fact]
    public async Task HealingReads_ListsSeededAttempts()
    {
        await SeedOnceAsync();
        await _factory.SeedAsync(db =>
        {
            if (db.SelfHealingAttempts.Any(a => a.ExecutionId == _executionA)) return Task.CompletedTask;
            db.SelfHealingAttempts.Add(new SelfHealingAttempt
            {
                ProjectId = _projectA, ExecutionId = _executionA, ExecutionTestId = _testA,
                TestCaseId = Guid.NewGuid(), StepOrder = 1, StepAction = "click",
                OriginalStrategy = "css", OriginalValue = "css=#old",
                RecoveredStrategy = "testid", RecoveredValue = "new-btn",
                HealingStrategy = SelfHealingStrategy.TestAttribute,
                Status = SelfHealingStatus.Applied, CandidateCount = 1,
                WasApplied = true, IsAiAssisted = false,
            });
            return Task.CompletedTask;
        });
        var rows = await Client("sh-viewer", ["viewer"]).GetFromJsonAsync<AttemptPayload[]>(
            $"/api/v1/projects/{_projectA}/executions/{_executionA}/healing");
        Assert.NotNull(rows);
        Assert.Contains(rows!, r => r.WasApplied && r.RecoveredValue == "new-btn");
    }

    [Fact]
    public async Task Suggest_RequiresLeaseToken()
    {
        await SeedOnceAsync();
        var url = $"/api/v1/execution-grid/assignments/{_workerRefA}/healing/suggest";
        var body = new { action = "click", originalTarget = "css=#old", domFragment = "x" };
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateClient().PostAsJsonAsync(url, body)).StatusCode);
        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsJsonAsync(url, body)).StatusCode);
    }

    [Fact]
    public async Task Suggest_PolicyGated_Then_ReturnsStubCandidates()
    {
        await SeedOnceAsync();
        var url = $"/api/v1/execution-grid/assignments/{_workerRefA}/healing/suggest";
        HttpClient WorkerClient()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _leaseTokenA.ToString());
            return client;
        }
        var evidence = new
        {
            action = "click",
            originalTarget = "css=#old",
            domFragment = "role=button|Submit",
            attributes = new[] { "role=button|Submit" },
            nearbyText = new[] { "Submit" },
        };
        // Healing disabled by default → policy conflict, provider never involved.
        var disabled = await WorkerClient().PostAsJsonAsync(url, evidence);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);

        var admin = Client("sh-admin", ["admin"]);
        var saved = await admin.PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/self-healing-policy",
            new { enabled = true, aiFallbackEnabled = true });
        Assert.True(saved.IsSuccessStatusCode);

        var ok = await WorkerClient().PostAsJsonAsync(url, evidence);
        Assert.True(ok.IsSuccessStatusCode);
        var payload = await ok.Content.ReadFromJsonAsync<SuggestPayload>();
        Assert.NotNull(payload);
        Assert.Contains(payload!.Candidates, c => c.Strategy == "text" && c.Value == "Submit");
    }
}
