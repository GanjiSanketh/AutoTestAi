using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 5: execution API — auth matrix, approval gate, exact-version binding,
/// history/detail/steps/logs/artifacts, cancellation, and secret hygiene
/// (docs/06 §8). The workflow starter is faked; the worker is faked in unit
/// tests. No live Temporal/Playwright here.
/// </summary>
public sealed class TestExecutionApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    private Guid _approvedVersionA = Guid.Empty;
    private Guid _pendingVersionA = Guid.Empty;
    private Guid _approvedVersionB = Guid.Empty;

    public TestExecutionApiTests(Slice1ApiFactory factory) => _factory = factory;

    private sealed class DisabledCoordinator : IExecutionWorkflowCoordinator
    {
        public bool IsConfigured => false;
        public Task<string> StartAsync(Guid e, Guid p, CancellationToken ct)
            => throw new InvalidOperationException("Temporal is not configured.");
        public Task<bool> CancelAsync(string workflowId, CancellationToken ct)
            => throw new InvalidOperationException("Temporal is not configured.");
    }

    private sealed class FakeArtifactStorage : IArtifactStorage
    {
        public bool IsConfigured => true;
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct)
            => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}?exp={expirySeconds}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
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

                var mgr = new User { ExternalIdentityId = "ex-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "ex-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "ex-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "ex-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);

                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Exec Alpha", Key = "EXA" },
                    new Project { Id = pb, Name = "Exec Beta", Key = "EXB" });

                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                var caseA = new TestCase
                {
                    ProjectId = pa, TestKey = "LOGIN-001", Title = "Login works",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var caseB = new TestCase
                {
                    ProjectId = pb, TestKey = "B-001", Title = "Beta case",
                    Priority = Priority.Medium, Status = TestCaseStatus.Draft, SourceType = "manual",
                };
                db.TestCases.AddRange(caseA, caseB);
                var approvedA = new TestCaseVersion
                {
                    TestCaseId = caseA.Id, VersionNumber = 1, SourceCode = "// a-v1",
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                };
                var pendingA = new TestCaseVersion
                {
                    TestCaseId = caseA.Id, VersionNumber = 2, SourceCode = "// a-v2",
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"click","target":"#x"}]"""),
                    ReviewStatus = ReviewStatus.Pending,
                };
                var approvedB = new TestCaseVersion
                {
                    TestCaseId = caseB.Id, VersionNumber = 1, SourceCode = "// b-v1",
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"navigate","target":"https://b.test"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                };
                db.TestCaseVersions.AddRange(approvedA, pendingA, approvedB);
                _approvedVersionA = approvedA.Id;
                _pendingVersionA = pendingA.Id;
                _approvedVersionB = approvedB.Id;
                return Task.CompletedTask;
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Manager()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-manager", ["qa-lead"]));
        return client;
    }

    private HttpClient Tester()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-tester", ["tester"]));
        return client;
    }

    private sealed record StartPayload(
        Guid ExecutionId, Guid ExecutionTestId, Guid ProjectId, Guid TestCaseId,
        Guid TestCaseVersionId, string Status, string? WorkflowId, bool Duplicated);
    private sealed record PagePayload(IReadOnlyList<ItemPayload> Items, int TotalCount);
    private sealed record ItemPayload(
        Guid Id, Guid ProjectId, string Status, string TestKey, int TestCaseVersionNumber);
    private sealed record DetailPayload(
        Guid Id, Guid ProjectId, string Status, string? WorkflowId, TestPayload Test);
    private sealed record TestPayload(
        Guid Id, string TestKey, Guid TestCaseVersionId, int TestCaseVersionNumber,
        string ReviewStatus, string Status, string? FailureClassification,
        IReadOnlyList<StepPayload> Steps);
    private sealed record StepPayload(int Order, string Action, string? Target, string Status);
    private sealed record LogPayload(long Id, string Level, string Message);
    private sealed record ArtifactPayload(Guid Id, string ArtifactType, string? FileName, int? StepOrder);
    private sealed record DownloadPayload(string DownloadUrl, int ExpiresInSeconds);
    private sealed record CancelPayload(Guid ExecutionId, string Status, bool CancellationRequested);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    // ---------- auth matrix ----------

    [Fact]
    public async Task Start_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { testCaseVersionId = _approvedVersionA });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Start_OutsiderAndViewer_Return403()
    {
        await SeedOnceAsync();
        var outsider = _factory.CreateClient();
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-outsider", ["tester"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA })).StatusCode);

        var viewer = _factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-viewer", ["viewer"]));
        var denied = await viewer.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { testCaseVersionId = _approvedVersionA });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await denied.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("FORBIDDEN", body?.Error.Code);
    }

    [Fact]
    public async Task Start_UnknownProject_Returns403()
    {
        await SeedOnceAsync();
        var outsider = _factory.CreateClient();
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-outsider", ["tester"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(
            $"/api/v1/projects/{Guid.NewGuid()}/executions",
            new { testCaseVersionId = _approvedVersionA })).StatusCode);
    }

    // ---------- approval gate ----------

    [Fact]
    public async Task Start_PendingVersion_Returns409_AndCreatesNothing()
    {
        await SeedOnceAsync();
        _factory.WorkflowCoordinator.Started.Clear();
        var response = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { testCaseVersionId = _pendingVersionA });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("CONFLICT", body?.Error.Code);
        Assert.Empty(_factory.WorkflowCoordinator.Started);
        var projectA = _projectA;
        await _factory.SeedAsync(db =>
        {
            Assert.Empty(db.Executions.Where(e => e.ProjectId == projectA));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Start_CrossProjectVersion_Returns403()
    {
        await SeedOnceAsync();
        var response = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { testCaseVersionId = _approvedVersionB });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Start_UnknownVersion_AsAdmin_Returns404()
    {
        await SeedOnceAsync();
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-admin", ["admin"]));
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { testCaseVersionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Start_InvalidBody_Returns400()
    {
        await SeedOnceAsync();
        var response = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { browser = "netscape" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- happy path ----------

    [Fact]
    public async Task Start_Success_BindsExactVersion_Returns202()
    {
        await SeedOnceAsync();
        _factory.WorkflowCoordinator.Started.Clear();
        var response = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, browser = "chromium", idempotencyKey = $"k-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var started = await response.Content.ReadFromJsonAsync<StartPayload>();
        Assert.NotNull(started);
        Assert.False(started!.Duplicated);
        Assert.Equal("Queued", started.Status);
        Assert.Equal(_approvedVersionA, started.TestCaseVersionId); // exact v1, not pending v2
        Assert.StartsWith("wf-test-", started.WorkflowId);

        Assert.Contains(_factory.WorkflowCoordinator.Started,
            s => s.ExecutionId == started.ExecutionId && s.ProjectId == _projectA);

        await _factory.SeedAsync(async db =>
        {
            var execution = await db.Executions.FirstAsync(e => e.Id == started.ExecutionId);
            Assert.Equal(_projectA, execution.ProjectId);
            Assert.Equal("Queued", execution.Status.ToString());
            Assert.NotNull(execution.WorkflowId);
            var test = await db.ExecutionTests.FirstAsync(t => t.ExecutionId == started.ExecutionId);
            Assert.Equal(_approvedVersionA, test.TestCaseVersionId);
            Assert.Equal("chromium", test.Browser);
            Assert.Equal("playwright", test.Framework);
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "execution.requested");
        });
    }

    [Fact]
    public async Task Start_IdempotencyKey_Dedupes_To200()
    {
        await SeedOnceAsync();
        var key = $"k-{Guid.NewGuid():N}";
        var first = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = key });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var second = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = key });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var payload = await second.Content.ReadFromJsonAsync<StartPayload>();
        Assert.True(payload!.Duplicated);
        var firstPayload = await first.Content.ReadFromJsonAsync<StartPayload>();
        Assert.Equal(firstPayload!.ExecutionId, payload.ExecutionId);
    }

    [Fact]
    public async Task Start_TemporalUnconfigured_Returns503_WithoutRecord()
    {
        await SeedOnceAsync();
        using var custom = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IExecutionWorkflowCoordinator>(new DisabledCoordinator());
            }));
        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-manager", ["qa-lead"]));
        var before = 0;
        await _factory.SeedAsync(db =>
        {
            before = db.Executions.Count();
            return Task.CompletedTask;
        });
        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions", new { testCaseVersionId = _approvedVersionA });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await _factory.SeedAsync(db =>
        {
            Assert.Equal(before, db.Executions.Count());
            return Task.CompletedTask;
        });
    }

    // ---------- history & detail ----------

    [Fact]
    public async Task History_ListsProjectExecutions_WithStatusFilter()
    {
        await SeedOnceAsync();
        var client = Tester();
        var started = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = $"k-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var page = await client.GetFromJsonAsync<PagePayload>(
            $"/api/v1/projects/{_projectA}/executions?page=1&pageSize=25");
        Assert.NotNull(page);
        Assert.Equal(1, page!.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("LOGIN-001", item.TestKey);
        Assert.Equal(1, item.TestCaseVersionNumber);

        var filtered = await client.GetFromJsonAsync<PagePayload>(
            $"/api/v1/projects/{_projectA}/executions?status=Passed");
        Assert.Equal(0, filtered!.TotalCount);

        var bad = await client.GetAsync($"/api/v1/projects/{_projectA}/executions?status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Detail_Steps_Logs_Artifacts_RoundTrip()
    {
        await SeedOnceAsync();
        var client = Manager();
        var started = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = $"k-{Guid.NewGuid():N}" });
        var payload = await started.Content.ReadFromJsonAsync<StartPayload>();
        Assert.NotNull(payload);

        Guid testId = Guid.Empty;
        await _factory.SeedAsync(async db =>
        {
            var test = await db.ExecutionTests.FirstAsync(t => t.ExecutionId == payload!.ExecutionId);
            testId = test.Id;
            db.ExecutionStepResults.Add(new ExecutionStepResult
            {
                ExecutionTestId = testId, StepOrder = 1, Action = "navigate",
                Target = "https://example.test", Status = ExecutionTestStatus.Passed,
            });
            db.ExecutionLogs.Add(new ExecutionLog
            {
                ExecutionTestId = testId, Level = "Information", Message = "navigated",
            });
            db.ExecutionArtifacts.Add(new ExecutionArtifact
            {
                ExecutionTestId = testId, ArtifactType = "screenshot",
                StorageKey = "projects/x/shot.png", FileName = "step-001.png",
                StepOrder = 1, ContentType = "image/png", SizeBytes = 10,
            });
            await db.SaveChangesAsync();
        });

        var detail = await client.GetFromJsonAsync<DetailPayload>(
            $"/api/v1/projects/{_projectA}/executions/{payload!.ExecutionId}");
        Assert.NotNull(detail);
        Assert.Equal(_approvedVersionA, detail!.Test.TestCaseVersionId);
        Assert.Equal("Approved", detail.Test.ReviewStatus);
        var step = Assert.Single(detail.Test.Steps);
        Assert.Equal("navigate", step.Action);

        var steps = await client.GetFromJsonAsync<List<StepPayload>>(
            $"/api/v1/projects/{_projectA}/executions/{payload.ExecutionId}/steps");
        Assert.Single(steps!);

        var logs = await client.GetFromJsonAsync<List<LogPayload>>(
            $"/api/v1/projects/{_projectA}/executions/{payload.ExecutionId}/logs");
        var log = Assert.Single(logs!);
        Assert.Equal("navigated", log.Message);

        var artifacts = await client.GetFromJsonAsync<List<ArtifactPayload>>(
            $"/api/v1/projects/{_projectA}/executions/{payload.ExecutionId}/artifacts");
        var artifact = Assert.Single(artifacts!);
        Assert.Equal("step-001.png", artifact.FileName);
    }

    [Fact]
    public async Task Detail_CrossProject_Returns403()
    {
        await SeedOnceAsync();
        var started = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = $"k-{Guid.NewGuid():N}" });
        var payload = await started.Content.ReadFromJsonAsync<StartPayload>();

        // Tester is a member of A only; B-scoped path to A's execution must deny.
        var outsider = _factory.CreateClient();
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-outsider", ["tester"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(
            $"/api/v1/projects/{_projectB}/executions/{payload!.ExecutionId}")).StatusCode);
    }

    [Fact]
    public async Task ArtifactDownload_UsesAuthorizedPresignedUrl()
    {
        await SeedOnceAsync();
        using var custom = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IArtifactStorage>(new FakeArtifactStorage());
            }));
        // Seed through the shared database (same InMemory store).
        Guid executionId = Guid.Empty;
        Guid artifactId = Guid.Empty;
        await _factory.SeedAsync(async db =>
        {
            var execution = new Execution { ProjectId = _projectA, Status = ExecutionStatus.Failed };
            var test = new ExecutionTest
            {
                ExecutionId = execution.Id,
                TestCaseId = db.TestCases.First(t => t.ProjectId == _projectA).Id,
                TestCaseVersionId = _approvedVersionA,
                Status = ExecutionTestStatus.Failed,
            };
            var artifact = new ExecutionArtifact
            {
                ExecutionTestId = test.Id, ArtifactType = "screenshot",
                StorageKey = "projects/x/shot.png", FileName = "shot.png", ContentType = "image/png",
            };
            db.Executions.Add(execution);
            db.ExecutionTests.Add(test);
            db.ExecutionArtifacts.Add(artifact);
            await db.SaveChangesAsync();
            executionId = execution.Id;
            artifactId = artifact.Id;
        });

        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-tester", ["tester"]));
        var response = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/executions/{executionId}/artifacts/{artifactId}/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var download = await response.Content.ReadFromJsonAsync<DownloadPayload>();
        Assert.StartsWith("https://artifacts.example/", download!.DownloadUrl, StringComparison.Ordinal);

        var missing = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/executions/{executionId}/artifacts/{Guid.NewGuid()}/download");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Responses_ContainNoSecrets()
    {
        await SeedOnceAsync();
        var started = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = $"k-{Guid.NewGuid():N}" });
        var raw = await started.Content.ReadAsStringAsync();
        Assert.DoesNotContain("apiToken", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", raw, StringComparison.Ordinal);

        var payload = await started.Content.ReadFromJsonAsync<StartPayload>();
        var detail = await Manager().GetStringAsync(
            $"/api/v1/projects/{_projectA}/executions/{payload!.ExecutionId}");
        Assert.DoesNotContain("apiToken", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretKey", detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- cancellation ----------

    [Fact]
    public async Task Cancel_Queued_TransitionsImmediately()
    {
        await SeedOnceAsync();
        _factory.WorkflowCoordinator.Cancelled.Clear();
        var started = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = $"k-{Guid.NewGuid():N}" });
        var payload = await started.Content.ReadFromJsonAsync<StartPayload>();

        var cancel = await Manager().PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{payload!.ExecutionId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var body = await cancel.Content.ReadFromJsonAsync<CancelPayload>();
        Assert.Equal("Cancelled", body!.Status);
        Assert.Empty(_factory.WorkflowCoordinator.Cancelled); // pre-start: no workflow call

        var again = await Manager().PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{payload.ExecutionId}/cancel", null);
        var againBody = await again.Content.ReadFromJsonAsync<CancelPayload>();
        Assert.Equal("Cancelled", againBody!.Status);
        Assert.False(againBody.CancellationRequested);
    }

    [Fact]
    public async Task Cancel_WithoutPermission_Returns403()
    {
        await SeedOnceAsync();
        var started = await Manager().PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/executions",
            new { testCaseVersionId = _approvedVersionA, idempotencyKey = $"k-{Guid.NewGuid():N}" });
        var payload = await started.Content.ReadFromJsonAsync<StartPayload>();

        var viewer = _factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("ex-viewer", ["viewer"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{payload!.ExecutionId}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Cancel_Running_RequestsWorkflowCancellation()
    {
        await SeedOnceAsync();
        _factory.WorkflowCoordinator.Cancelled.Clear();
        Guid executionId = Guid.Empty;
        await _factory.SeedAsync(async db =>
        {
            var execution = new Execution
            {
                ProjectId = _projectA, Status = ExecutionStatus.Running,
                WorkflowId = "wf-test-running", StartedAt = DateTimeOffset.UtcNow,
            };
            db.ExecutionTests.Add(new ExecutionTest
            {
                ExecutionId = execution.Id,
                TestCaseId = db.TestCases.First(t => t.ProjectId == _projectA).Id,
                TestCaseVersionId = _approvedVersionA,
                Status = ExecutionTestStatus.Running,
            });
            db.Executions.Add(execution);
            await db.SaveChangesAsync();
            executionId = execution.Id;
        });

        var cancel = await Manager().PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{executionId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var body = await cancel.Content.ReadFromJsonAsync<CancelPayload>();
        Assert.Equal("Running", body!.Status);
        Assert.True(body.CancellationRequested);
        Assert.Contains(_factory.WorkflowCoordinator.Cancelled, w => w == "wf-test-running");
    }
}
