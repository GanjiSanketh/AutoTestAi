using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.Maintenance;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 10: maintenance scan schedules — auth matrix, project
/// isolation, one-per-project CRUD + lifecycle through a fake coordinator,
/// Temporal-unavailable dependency paths (real unconfigured coordinator),
/// fire path creating proposals without approving/applying them, and the
/// hourly cadence floor.
/// </summary>
public sealed class MaintenanceScanScheduleApiTests : IClassFixture<Slice1ApiFactory>, IDisposable
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _schedFactory;
    private readonly FakeCoordinator _coordinator = new();
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public MaintenanceScanScheduleApiTests(Slice1ApiFactory factory)
    {
        _factory = factory;
        _schedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMaintenanceScanScheduleCoordinator>();
            services.AddSingleton<IMaintenanceScanScheduleCoordinator>(_coordinator);
        }));
    }

    public void Dispose()
    {
        _schedFactory.Dispose();
        _seedLock.Dispose();
    }

    private sealed class FakeCoordinator : IMaintenanceScanScheduleCoordinator
    {
        public bool IsConfigured => true;
        public readonly List<(Guid Id, MaintenanceScanScheduleDefinition Definition)> Created = new();
        public readonly List<(Guid Id, MaintenanceScanScheduleDefinition Definition)> Updated = new();
        public readonly List<Guid> Paused = new();
        public readonly List<Guid> Resumed = new();
        public readonly List<Guid> Deleted = new();

        public Task CreateAsync(Guid scheduleId, MaintenanceScanScheduleDefinition definition, CancellationToken ct)
        {
            Created.Add((scheduleId, definition));
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Guid scheduleId, MaintenanceScanScheduleDefinition definition, CancellationToken ct)
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

    private sealed record SchedulePayload(Guid Id, Guid ProjectId, string CronExpression, string TimeZoneId,
        string Status, DateTimeOffset? LastTriggeredAt, int? LastCandidatesDetected, int? LastProposalsCreated,
        DateTimeOffset? NextRunAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

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
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var manager = new User { ExternalIdentityId = "mscan-manager", Email = "m@x", DisplayName = "M" };
                var viewer = new User { ExternalIdentityId = "mscan-viewer", Email = "v@x", DisplayName = "V" };
                var outsider = new User { ExternalIdentityId = "mscan-outsider", Email = "o@x", DisplayName = "O" };
                db.Users.AddRange(manager, viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Scan Sched Alpha", Key = "MSA" },
                    new Project { Id = pb, Name = "Scan Sched Beta", Key = "MSB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = manager.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });
                await db.SaveChangesAsync();

                await SeedHealingCaseAsync(db, pa, "MSS-SCAN", "Scan case");
                await db.SaveChangesAsync();
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static async Task SeedHealingCaseAsync(
        AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext db, Guid projectId, string key, string title)
    {
        var tc = new TestCase
        {
            ProjectId = projectId, TestKey = key, Title = title,
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        db.TestCases.Add(tc);
        db.TestCaseVersions.Add(new TestCaseVersion
        {
            TestCaseId = tc.Id, VersionNumber = 1,
            ReviewStatus = ReviewStatus.Approved,
            StructuredSteps = JsonDocument.Parse(
                """[{"order":1,"action":"navigate","target":"https://example.test","value":null},{"order":2,"action":"click","target":"css=#login","value":null}]"""),
        });
        await db.SaveChangesAsync();
        var version = await db.TestCaseVersions.SingleAsync(v => v.TestCaseId == tc.Id);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var execution = new Execution { ProjectId = projectId, Status = ExecutionStatus.Passed, CreatedAt = now.AddDays(-1).AddHours(i) };
            db.Executions.Add(execution);
            await db.SaveChangesAsync();
            var test = new ExecutionTest
            {
                ExecutionId = execution.Id, TestCaseId = tc.Id, TestCaseVersionId = version.Id,
                Status = ExecutionTestStatus.Passed, FailureClassification = FailureClassification.Unknown,
                CreatedAt = now.AddDays(-1).AddHours(i),
            };
            db.ExecutionTests.Add(test);
            await db.SaveChangesAsync();
            db.SelfHealingAttempts.Add(new SelfHealingAttempt
            {
                ProjectId = projectId, ExecutionId = execution.Id, ExecutionTestId = test.Id,
                TestCaseId = tc.Id, TestCaseVersionId = version.Id,
                StepOrder = 2, StepAction = "click",
                OriginalStrategy = "css", OriginalValue = "#login",
                RecoveredStrategy = "testid", RecoveredValue = "testLoginBtn",
                HealingStrategy = SelfHealingStrategy.TestAttribute,
                Status = SelfHealingStatus.Applied, CandidateCount = 1,
                WasApplied = true, IsAiAssisted = false,
                CreatedAt = now.AddDays(-1).AddHours(i),
            });
        }
        for (var i = 0; i < 2; i++)
        {
            var execution = new Execution { ProjectId = projectId, Status = ExecutionStatus.Failed, CreatedAt = now.AddDays(-2).AddHours(i) };
            db.Executions.Add(execution);
            await db.SaveChangesAsync();
            db.ExecutionTests.Add(new ExecutionTest
            {
                ExecutionId = execution.Id, TestCaseId = tc.Id, TestCaseVersionId = version.Id,
                Status = ExecutionTestStatus.Failed, FailureClassification = FailureClassification.AutomationFailure,
                CreatedAt = now.AddDays(-2).AddHours(i),
            });
        }
        await db.SaveChangesAsync();
    }

    private HttpClient Client(string sub, string role)
    {
        var client = _schedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, [role]));
        return client;
    }

    private HttpClient Manager() => Client("mscan-manager", "qa-lead");
    private HttpClient Viewer() => Client("mscan-viewer", "viewer");

    private static string Url(Guid projectId) => $"/api/v1/projects/{projectId}/maintenance/schedule";

    // ---------- auth ----------

    [Fact]
    public async Task Get_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _schedFactory.CreateClient().GetAsync(Url(_projectA))).StatusCode);
    }

    [Fact]
    public async Task Post_Outsider_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("mscan-outsider", "qa-lead").PostAsJsonAsync(Url(_projectA),
                new { cronExpression = "30 2 * * *", timeZoneId = "UTC" })).StatusCode);
    }

    [Fact]
    public async Task Post_Viewer_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Viewer().PostAsJsonAsync(Url(_projectA),
                new { cronExpression = "30 2 * * *", timeZoneId = "UTC" })).StatusCode);
    }

    [Fact]
    public async Task Get_CrossProject_Returns403()
    {
        await SeedOnceAsync();
        // Manager belongs to A only; B is opaque.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Manager().GetAsync(Url(_projectB))).StatusCode);
    }

    // ---------- CRUD + lifecycle ----------

    [Fact]
    public async Task Crud_Lifecycle_RoundTrip()
    {
        await SeedOnceAsync();
        var client = Manager();

        var getMissing = await client.GetAsync(Url(_projectA));
        Assert.Equal(HttpStatusCode.NotFound, getMissing.StatusCode);

        var create = await client.PostAsJsonAsync(Url(_projectA),
            new { cronExpression = "30 2 * * *", timeZoneId = "UTC" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.NotNull(created);
        Assert.Equal("Active", created!.Status);
        Assert.Null(created.NextRunAt);
        Assert.Contains(_coordinator.Created, c => c.Id == created.Id && !c.Definition.Paused);

        // One schedule per project: the second create conflicts.
        var duplicate = await client.PostAsJsonAsync(Url(_projectA),
            new { cronExpression = "15 3 * * *", timeZoneId = "UTC" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var get = await client.GetAsync(Url(_projectA));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.Equal(created.Id, fetched!.Id);

        // Viewer can read but not mutate.
        var viewerGet = await Viewer().GetAsync(Url(_projectA));
        Assert.Equal(HttpStatusCode.OK, viewerGet.StatusCode);

        var update = await client.PutAsJsonAsync(Url(_projectA),
            new { cronExpression = "15 4 * * *", timeZoneId = "America/New_York" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Contains(_coordinator.Updated, c => c.Id == created.Id);

        var pause = await client.PostAsync(Url(_projectA) + "/pause", null);
        Assert.Equal(HttpStatusCode.NoContent, pause.StatusCode);
        Assert.Contains(_coordinator.Paused, id => id == created.Id);
        var paused = await (await client.GetAsync(Url(_projectA))).Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.Equal("Disabled", paused!.Status);

        var resume = await client.PostAsync(Url(_projectA) + "/resume", null);
        Assert.Equal(HttpStatusCode.NoContent, resume.StatusCode);
        Assert.Contains(_coordinator.Resumed, id => id == created.Id);

        // Explicit next-run retrieval: single describe through the fake.
        var nextRun = await client.GetAsync(Url(_projectA) + "/next-run");
        Assert.Equal(HttpStatusCode.OK, nextRun.StatusCode);

        var delete = await client.DeleteAsync(Url(_projectA));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Contains(_coordinator.Deleted, id => id == created.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(_projectA))).StatusCode);
    }

    [Fact]
    public async Task Create_RejectsSubHourlyCadence()
    {
        await SeedOnceAsync();
        var bad = await Manager().PostAsJsonAsync(Url(_projectA),
            new { cronExpression = "*/15 * * * *", timeZoneId = "UTC" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsUnknownTimezone()
    {
        await SeedOnceAsync();
        var bad = await Manager().PostAsJsonAsync(Url(_projectA),
            new { cronExpression = "30 2 * * *", timeZoneId = "Mars/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Create_TemporalUnavailable_Returns503_WithoutActiveRow()
    {
        await SeedOnceAsync();
        // Real (unconfigured) coordinator: no fake, no Temporal.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create("mscan-manager", ["qa-lead"]));
        var create = await client.PostAsJsonAsync(Url(_projectA),
            new { cronExpression = "30 2 * * *", timeZoneId = "UTC" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(_projectA))).StatusCode);
    }

    // ---------- scheduled fire: proposals yes, approval/apply no ----------

    [Fact]
    public async Task ScheduledFire_CreatesProposal_WithoutApprovingOrApplying()
    {
        await SeedOnceAsync();
        var client = Manager();
        var create = await client.PostAsJsonAsync(Url(_projectA),
            new { cronExpression = "30 2 * * *", timeZoneId = "UTC" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.NotNull(created);

        var versionsBefore = await CountVersionsAsync();

        MaintenanceScanResult result;
        using (var scope = _schedFactory.Services.CreateScope())
        {
            var schedules = scope.ServiceProvider.GetRequiredService<IMaintenanceScanScheduleService>();
            result = await schedules.FireAsync(created!.Id, CancellationToken.None);
        }
        Assert.True(result.ProposalsCreated >= 1);

        // The fire path updated last-run metadata…
        var fetched = await (await client.GetAsync(Url(_projectA))).Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.NotNull(fetched!.LastTriggeredAt);
        Assert.Equal(result.CandidatesDetected, fetched.LastCandidatesDetected);
        Assert.Equal(result.ProposalsCreated, fetched.LastProposalsCreated);

        // …created a Proposed row…
        using var list = await (await client.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals?search=MSS-SCAN&pageSize=10"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        var item = list!.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Proposed", item.GetProperty("status").GetString());

        // …but approved nothing and minted no versions: human review stays mandatory.
        Assert.Equal(versionsBefore, await CountVersionsAsync());
        using var scope2 = _schedFactory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        Assert.False(await db.MaintenanceProposals.AnyAsync(
            p => p.ProjectId == _projectA && p.Status != MaintenanceProposalStatus.Proposed));
    }

    [Fact]
    public async Task ScheduledFire_DisabledSchedule_FailsClosed()
    {
        await SeedOnceAsync();
        var client = Manager();
        var create = await client.PostAsJsonAsync(Url(_projectA),
            new { cronExpression = "30 2 * * *", timeZoneId = "UTC" });
        var created = await create.Content.ReadFromJsonAsync<SchedulePayload>();
        Assert.NotNull(created);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync(Url(_projectA) + "/pause", null)).StatusCode);

        using var scope = _schedFactory.Services.CreateScope();
        var schedules = scope.ServiceProvider.GetRequiredService<IMaintenanceScanScheduleService>();
        await Assert.ThrowsAsync<AutoTestAi.Application.Common.ValidationException>(
            () => schedules.FireAsync(created!.Id, CancellationToken.None));
    }

    private async Task<int> CountVersionsAsync()
    {
        using var scope = _schedFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        return await db.TestCaseVersions.CountAsync(v =>
            db.TestCases.Where(c => c.ProjectId == _projectA).Select(c => c.Id).Contains(v.TestCaseId));
    }
}
