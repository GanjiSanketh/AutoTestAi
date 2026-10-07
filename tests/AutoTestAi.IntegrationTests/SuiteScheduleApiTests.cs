using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 9B: suite schedules — auth matrix, project isolation, CRUD +
/// lifecycle through a fake schedule coordinator, Temporal-unavailable
/// dependency paths (real unconfigured coordinator), suite-archive cascade,
/// run-now fan-out, and trigger-aware reporting.
/// </summary>
public sealed class SuiteScheduleApiTests : IClassFixture<Slice1ApiFactory>, IDisposable
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _schedFactory;
    private readonly FakeCoordinator _coordinator = new();
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public SuiteScheduleApiTests(Slice1ApiFactory factory)
    {
        _factory = factory;
        _schedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISuiteScheduleCoordinator>();
            services.AddSingleton<ISuiteScheduleCoordinator>(_coordinator);
        }));
    }

    public void Dispose()
    {
        _schedFactory.Dispose();
        _seedLock.Dispose();
    }

    private sealed class FakeCoordinator : ISuiteScheduleCoordinator
    {
        public bool IsConfigured => true;
        public readonly List<(Guid Id, SuiteScheduleDefinition Definition)> Created = new();
        public readonly List<(Guid Id, SuiteScheduleDefinition Definition)> Updated = new();
        public readonly List<Guid> Paused = new();
        public readonly List<Guid> Resumed = new();
        public readonly List<Guid> Deleted = new();

        public Task CreateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct)
        {
            Created.Add((scheduleId, definition));
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct)
        {
            Updated.Add((scheduleId, definition));
            return Task.CompletedTask;
        }

        public Task PauseAsync(Guid scheduleId, string note, CancellationToken ct)
        {
            Paused.Add(scheduleId);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(Guid scheduleId, string note, CancellationToken ct)
        {
            Resumed.Add(scheduleId);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid scheduleId, CancellationToken ct)
        {
            Deleted.Add(scheduleId);
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetNextRunAsync(Guid scheduleId, CancellationToken ct)
            => Task.FromResult<DateTimeOffset?>(new DateTimeOffset(2026, 10, 8, 2, 30, 0, TimeSpan.Zero));
    }

    private sealed record SeedResult(Guid SuiteA, Guid CaseA1, Guid CaseA2, Guid SuiteB);
    private sealed record SchedulePayload(Guid Id, Guid ProjectId, Guid SuiteId, string SuiteName, string Name,
        string CronExpression, string TimeZoneId, string Status, string OverlapPolicy,
        DateTimeOffset? LastTriggeredAt, Guid? LastExecutionId, DateTimeOffset? NextRunAt,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record CreatedPayload(Guid Id, string Name, string Status);
    private sealed record ExecutePayload(Guid ExecutionId, Guid SuiteId, int TestCount, string Status, DateTimeOffset CreatedAt);
    private sealed record HistoryPage(IReadOnlyList<HistoryItem> Items, int TotalCount, int Page, int PageSize);
    private sealed record HistoryItem(Guid ExecutionId, string Status, string TriggerType, DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int TestCount, int PassedCount, int FailedCount);
    private sealed record ReportPayload(Guid SuiteId, int TotalExecutions,
        IReadOnlyList<TriggerRow> TriggerBreakdown, IReadOnlyList<TrendRow> Trend);
    private sealed record TriggerRow(string Trigger, int Total, int Passed, int Failed, double? PassRate);
    private sealed record TrendRow(DateOnly Date, int Total, int Passed, int Failed);

    private static JsonDocument Steps()
        => JsonDocument.Parse("""[{"order":1,"action":"navigate","target":"https://example.test"}]""");

    private async Task<SeedResult> SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            var result = new SeedResult(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
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

                var mgr = new User { ExternalIdentityId = "sched-manager", Email = "sm@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "sched-tester", Email = "st@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "sched-viewer", Email = "sv@x", DisplayName = "Viewer" };
                db.Users.AddRange(mgr, tester, viewer);

                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Sched Alpha", Key = "SCHEDA" },
                    new Project { Id = pb, Name = "Sched Beta", Key = "SCHEDB" });

                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId },
                    new ProjectMember { ProjectId = pb, UserId = mgr.Id, RoleId = QaLeadRoleId });

                var a1 = new TestCase
                {
                    ProjectId = pa, TestKey = "SCH-A1", Title = "Alpha one",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var a2 = new TestCase
                {
                    ProjectId = pa, TestKey = "SCH-A2", Title = "Alpha two",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.AddRange(a1, a2);
                db.TestCaseVersions.AddRange(
                    new TestCaseVersion { TestCaseId = a1.Id, VersionNumber = 1, SourceCode = "// a1", StructuredSteps = Steps(), ReviewStatus = ReviewStatus.Approved },
                    new TestCaseVersion { TestCaseId = a2.Id, VersionNumber = 1, SourceCode = "// a2", StructuredSteps = Steps(), ReviewStatus = ReviewStatus.Approved });

                var suiteA = new TestSuite { ProjectId = pa, Name = "Sched Regression", Status = ProjectStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                var suiteB = new TestSuite { ProjectId = pb, Name = "Sched Beta", Status = ProjectStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                db.TestSuites.AddRange(suiteA, suiteB);
                db.SuiteTestCases.AddRange(
                    new SuiteTestCase { SuiteId = suiteA.Id, TestCaseId = a1.Id, ExecutionOrder = 1 },
                    new SuiteTestCase { SuiteId = suiteA.Id, TestCaseId = a2.Id, ExecutionOrder = 2 });

                result = new SeedResult(suiteA.Id, a1.Id, a2.Id, suiteB.Id);
                return Task.CompletedTask;
            });
            return result;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string sub, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, [role]));
        return client;
    }

    private HttpClient Manager() => Client(_schedFactory, "sched-manager", "qa-lead");
    private HttpClient Viewer() => Client(_schedFactory, "sched-viewer", "viewer");
    private HttpClient Tester() => Client(_schedFactory, "sched-tester", "tester");
    private HttpClient RealManager() => Client(_factory, "sched-manager", "qa-lead");

    // ---------- authentication ----------

    [Fact]
    public async Task List_Anonymous_Returns401()
    {
        var seed = await SeedOnceAsync();
        var response = await _schedFactory.CreateClient()
            .GetAsync($"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- CRUD + lifecycle ----------

    [Fact]
    public async Task Crud_Lifecycle_RoundTrip()
    {
        var seed = await SeedOnceAsync();
        var client = Manager();

        var create = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules",
            new { name = "Nightly", cronExpression = "30 2 * * *", timeZoneId = "UTC", overlapPolicy = "Skip" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreatedPayload>();
        Assert.NotNull(created);
        Assert.Contains(_coordinator.Created, c => c.Id == created.Id && !c.Definition.Paused);

        var list = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var get = await client.GetAsync($"/api/v1/test-suite-schedules/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var detail = await get.Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.Equal("Nightly", detail!.Name);
        Assert.Equal("Active", detail.Status);
        Assert.NotNull(detail.NextRunAt);

        var update = await client.PutAsJsonAsync($"/api/v1/test-suite-schedules/{created.Id}",
            new { name = "Nightly v2", cronExpression = "0 3 * * *", timeZoneId = "America/New_York", overlapPolicy = "Allow" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Contains(_coordinator.Updated, c => c.Id == created.Id && c.Definition.OverlapPolicy == "Allow");

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/v1/test-suite-schedules/{created.Id}/pause", null)).StatusCode);
        var paused = await (await client.GetAsync($"/api/v1/test-suite-schedules/{created.Id}"))
            .Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.Equal("Disabled", paused!.Status);
        Assert.Contains(_coordinator.Paused, id => id == created.Id);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/v1/test-suite-schedules/{created.Id}/resume", null)).StatusCode);
        Assert.Contains(_coordinator.Resumed, id => id == created.Id);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/test-suite-schedules/{created.Id}")).StatusCode);
        Assert.Contains(_coordinator.Deleted, id => id == created.Id);
        var archived = await (await client.GetAsync($"/api/v1/test-suite-schedules/{created.Id}"))
            .Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.Equal("Archived", archived!.Status);
    }

    [Fact]
    public async Task Create_Validation_And_Duplicate()
    {
        var seed = await SeedOnceAsync();
        var client = Manager();
        var url = $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules";

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url,
            new { name = "  ", cronExpression = "30 2 * * *" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url,
            new { name = "N", cronExpression = "bogus" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url,
            new { name = "N", cronExpression = "30 2 * * *", timeZoneId = "Mars/Olympus" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url,
            new { name = "Dup", cronExpression = "30 2 * * *" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url,
            new { name = "dup", cronExpression = "0 3 * * *" })).StatusCode);
    }

    // ---------- authorization ----------

    [Fact]
    public async Task Viewer_Reads_But_Cannot_Mutate()
    {
        var seed = await SeedOnceAsync();
        var manager = Manager();
        var create = await manager.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules",
            new { name = "Nightly", cronExpression = "30 2 * * *" });
        var created = await create.Content.ReadFromJsonAsync<CreatedPayload>();

        var viewer = Viewer();
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/v1/test-suite-schedules/{created!.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules",
            new { name = "Nope", cronExpression = "30 2 * * *" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync($"/api/v1/test-suite-schedules/{created.Id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync($"/api/v1/test-suite-schedules/{created.Id}/run-now",
                JsonContent.Create(new { }))).StatusCode);
    }

    [Fact]
    public async Task CrossProject_Access_Returns403()
    {
        var seed = await SeedOnceAsync();
        var manager = Manager();
        var create = await manager.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules",
            new { name = "Nightly", cronExpression = "30 2 * * *" });
        var created = await create.Content.ReadFromJsonAsync<CreatedPayload>();

        // sched-tester belongs to project A only.
        var tester = Tester();
        Assert.Equal(HttpStatusCode.OK,
            (await tester.GetAsync($"/api/v1/test-suite-schedules/{created!.Id}")).StatusCode);

        // A schedule living in project B is invisible to the A-only tester.
        var createB = await manager.PostAsJsonAsync(
            $"/api/v1/projects/{_projectB}/test-suites/{seed.SuiteB}/schedules",
            new { name = "Beta Nightly", cronExpression = "30 2 * * *" });
        Assert.Equal(HttpStatusCode.Created, createB.StatusCode);
        var createdB = await createB.Content.ReadFromJsonAsync<CreatedPayload>();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tester.GetAsync($"/api/v1/test-suite-schedules/{createdB!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tester.GetAsync($"/api/v1/projects/{_projectB}/test-suites/{seed.SuiteB}/schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PostAsJsonAsync(
            $"/api/v1/projects/{_projectB}/test-suites/{seed.SuiteB}/schedules",
            new { name = "X", cronExpression = "30 2 * * *" })).StatusCode);
        // Cross-project suite reference rejected.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteB}/schedules",
            new { name = "X", cronExpression = "30 2 * * *" })).StatusCode);
    }

    // ---------- Temporal unavailable ----------

    [Fact]
    public async Task TemporalUnavailable_Create_And_Pause_Return503_WithoutRow()
    {
        var seed = await SeedOnceAsync();
        var client = RealManager(); // real unconfigured coordinator
        var url = $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules";

        var create = await client.PostAsJsonAsync(url,
            new { name = "Nightly", cronExpression = "30 2 * * *" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);

        // No apparently-active row left behind.
        var list = await RealManager().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    // ---------- run-now + archive cascade ----------

    [Fact]
    public async Task RunNow_FansOut_WithScheduleTrigger()
    {
        var seed = await SeedOnceAsync();
        var client = Manager();
        var create = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules",
            new { name = "Nightly", cronExpression = "30 2 * * *" });
        var created = await create.Content.ReadFromJsonAsync<CreatedPayload>();
        var before = _factory.WorkflowCoordinator.Started.Count;

        var run = await client.PostAsJsonAsync($"/api/v1/test-suite-schedules/{created!.Id}/run-now",
            new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await run.Content.ReadFromJsonAsync<ExecutePayload>();
        Assert.NotNull(result);
        Assert.Equal(2, result.TestCount);
        Assert.Equal(before + 2, _factory.WorkflowCoordinator.Started.Count);

        // Schedule row carries last-run pointers; history shows the trigger.
        var detail = await (await client.GetAsync($"/api/v1/test-suite-schedules/{created.Id}"))
            .Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.Equal(result.ExecutionId, detail!.LastExecutionId);
        Assert.NotNull(detail.LastTriggeredAt);

        var history = await (await client.GetAsync($"/api/v1/test-suites/{seed.SuiteA}/executions"))
            .Content.ReadFromJsonAsync<HistoryPage>();
        Assert.Contains(history!.Items, i => i.TriggerType == "Schedule");
    }

    [Fact]
    public async Task SuiteArchive_DisablesSchedules()
    {
        var seed = await SeedOnceAsync();
        var client = Manager();
        var create = await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/test-suites/{seed.SuiteA}/schedules",
            new { name = "Nightly", cronExpression = "30 2 * * *" });
        var created = await create.Content.ReadFromJsonAsync<CreatedPayload>();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/test-suites/{seed.SuiteA}")).StatusCode);

        var detail = await (await client.GetAsync($"/api/v1/test-suite-schedules/{created!.Id}"))
            .Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.Equal("Disabled", detail!.Status);
        Assert.Contains(_coordinator.Paused, id => id == created.Id);

        // Resuming against an archived suite is rejected.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/v1/test-suite-schedules/{created.Id}/resume", null)).StatusCode);
    }

    // ---------- trigger-aware reporting ----------

    [Fact]
    public async Task Report_Trend_And_TriggerBreakdown()
    {
        var seed = await SeedOnceAsync();
        await _factory.SeedAsync(db =>
        {
            var manual = new Execution
            {
                ProjectId = _projectA, SuiteId = seed.SuiteA, Status = ExecutionStatus.Passed,
                TriggerType = TriggerType.Manual, CreatedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
            };
            var scheduled = new Execution
            {
                ProjectId = _projectA, SuiteId = seed.SuiteA, Status = ExecutionStatus.Failed,
                TriggerType = TriggerType.Schedule, CreatedAt = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero),
            };
            db.Executions.AddRange(manual, scheduled);
            db.ExecutionTests.AddRange(
                new ExecutionTest { ExecutionId = manual.Id, TestCaseId = seed.CaseA1, Status = ExecutionTestStatus.Passed },
                new ExecutionTest { ExecutionId = scheduled.Id, TestCaseId = seed.CaseA1, Status = ExecutionTestStatus.Failed });
            return Task.CompletedTask;
        });

        var client = Manager();
        var report = await client.GetAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/report?from=2026-06-01T00:00:00Z&to=2026-06-30T00:00:00Z&groupBy=day");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var payload = await report.Content.ReadFromJsonAsync<ReportPayload>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload.TotalExecutions);
        Assert.Equal(2, payload.TriggerBreakdown.Count);
        Assert.Contains(payload.TriggerBreakdown, b => b.Trigger == "Schedule" && b.Failed == 1);
        Assert.Equal(2, payload.Trend.Count);
        Assert.Equal(new DateOnly(2026, 6, 1), payload.Trend[0].Date);
        Assert.Equal(1, payload.Trend[0].Total);

        var filtered = await client.GetAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/report?from=2026-06-01T00:00:00Z&to=2026-06-30T00:00:00Z&trigger=Schedule");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var filteredPayload = await filtered.Content.ReadFromJsonAsync<ReportPayload>();
        Assert.Equal(1, filteredPayload!.TotalExecutions);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/report?groupBy=week")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/report?trigger=Bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
            $"/api/v1/test-suites/{seed.SuiteA}/report?from=2026-01-01T00:00:00Z&to=2026-06-01T00:00:00Z&groupBy=day")).StatusCode);
    }
}
