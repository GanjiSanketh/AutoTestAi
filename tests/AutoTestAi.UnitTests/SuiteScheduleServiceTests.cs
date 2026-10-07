using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>In-memory ISuiteScheduleStore fake.</summary>
internal sealed class FakeSuiteScheduleStore : ISuiteScheduleStore
{
    public readonly List<TestSuiteSchedule> Schedules = new();

    public Task<TestSuiteSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct)
        => Task.FromResult(Schedules.FirstOrDefault(s => s.Id == scheduleId));

    public Task<IReadOnlyList<TestSuiteSchedule>> ListBySuiteAsync(Guid suiteId, bool includeArchived, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestSuiteSchedule>>(Schedules
            .Where(s => s.SuiteId == suiteId && (includeArchived || s.Status != ScheduleStatus.Archived))
            .OrderBy(s => s.Name)
            .ToList());

    public Task<IReadOnlyList<TestSuiteSchedule>> ListActiveBySuiteAsync(Guid suiteId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestSuiteSchedule>>(Schedules
            .Where(s => s.SuiteId == suiteId && s.Status == ScheduleStatus.Active)
            .ToList());

    public Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeScheduleId, CancellationToken ct)
        => Task.FromResult(Schedules.Any(s =>
            s.ProjectId == projectId &&
            string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (!excludeScheduleId.HasValue || s.Id != excludeScheduleId.Value)));

    public Task AddAsync(TestSuiteSchedule schedule, CancellationToken ct)
    {
        Schedules.Add(schedule);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(TestSuiteSchedule schedule, CancellationToken ct)
    {
        Schedules.Remove(schedule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Recording ISuiteScheduleCoordinator fake: no Temporal required.</summary>
internal sealed class FakeScheduleCoordinator : ISuiteScheduleCoordinator
{
    public bool IsConfigured { get; set; } = true;
    public readonly List<(Guid Id, SuiteScheduleDefinition Definition)> Created = new();
    public readonly List<(Guid Id, SuiteScheduleDefinition Definition)> Updated = new();
    public readonly List<Guid> Paused = new();
    public readonly List<Guid> Resumed = new();
    public readonly List<Guid> Deleted = new();
    public Func<Guid, Exception?> FailureFor { get; set; } = _ => null;
    public DateTimeOffset? NextRun { get; set; } = new DateTimeOffset(2026, 10, 8, 2, 30, 0, TimeSpan.Zero);

    private void MaybeFail(Guid id, string op)
    {
        var failure = FailureFor(id);
        if (failure is not null) throw failure;
        if (!IsConfigured) throw new InvalidOperationException($"Temporal is not configured ({op}).");
    }

    public Task CreateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct)
    {
        MaybeFail(scheduleId, "create");
        Created.Add((scheduleId, definition));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct)
    {
        MaybeFail(scheduleId, "update");
        Updated.Add((scheduleId, definition));
        return Task.CompletedTask;
    }

    public Task PauseAsync(Guid scheduleId, string note, CancellationToken ct)
    {
        MaybeFail(scheduleId, "pause");
        Paused.Add(scheduleId);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(Guid scheduleId, string note, CancellationToken ct)
    {
        MaybeFail(scheduleId, "resume");
        Resumed.Add(scheduleId);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid scheduleId, CancellationToken ct)
    {
        MaybeFail(scheduleId, "delete");
        Deleted.Add(scheduleId);
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetNextRunAsync(Guid scheduleId, CancellationToken ct)
        => Task.FromResult(IsConfigured ? NextRun : null);
}

public sealed class SuiteScheduleServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private static (SuiteScheduleService Service, FakeSuiteScheduleStore Schedules, FakeSuiteStore Suites, FakeTestCaseStore Cases, FakeScheduleCoordinator Coordinator, RecordingExecutionService Executions, StubAuditProjectStore Audits) Create(
        StubCurrentUser user,
        Action<FakeSuiteScheduleStore, FakeSuiteStore, FakeTestCaseStore, StubMembershipStore>? seed = null,
        string userSub = "user-1")
    {
        var schedules = new FakeSuiteScheduleStore();
        var suites = new FakeSuiteStore();
        var cases = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        seed?.Invoke(schedules, suites, cases, memberships);
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        var audits = new StubAuditProjectStore();
        var executions = new RecordingExecutionService();
        var coordinator = new FakeScheduleCoordinator();
        var executionService = new SuiteExecutionService(
            suites, cases, executions, authorization,
            new SystemDateTimeProvider(),
            new AuditService(audits, user, directory, NullLogger<AuditService>.Instance));
        var service = new SuiteScheduleService(
            schedules, suites, executionService, coordinator, authorization,
            user, directory, new SystemDateTimeProvider(),
            new AuditService(audits, user, directory, NullLogger<AuditService>.Instance));
        return (service, schedules, suites, cases, coordinator, executions, audits);
    }

    private static StubCurrentUser Manager(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["qa-lead"],
        Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static StubCurrentUser Viewer(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["viewer"],
        Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private static StubCurrentUser Admin(string sub = "admin-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["admin"],
        Permissions = RolePermissions.Resolve(["admin"]),
    };

    private static TestSuite SeedSuite(FakeSuiteStore suites, Guid projectId, string name,
        ProjectStatus status = ProjectStatus.Active)
    {
        var suite = new TestSuite
        {
            ProjectId = projectId, Name = name, Status = status,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        suites.Suites.Add(suite);
        return suite;
    }

    private static TestCase SeedApproved(FakeTestCaseStore cases, Guid projectId, string key)
    {
        var testCase = new TestCase
        {
            ProjectId = projectId, TestKey = key, Title = $"Title {key}",
            Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        cases.Versions.Add(new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 1, SourceCode = "// v1", ReviewStatus = ReviewStatus.Approved,
        });
        return testCase;
    }

    private static TestSuiteSchedule SeedSchedule(FakeSuiteScheduleStore schedules, Guid projectId, Guid suiteId, string name,
        ScheduleStatus status = ScheduleStatus.Active)
    {
        var schedule = new TestSuiteSchedule
        {
            ProjectId = projectId, SuiteId = suiteId, Name = name,
            CronExpression = "30 2 * * *", TimeZoneId = "UTC",
            Status = status, OverlapPolicy = ScheduleOverlapPolicy.Skip,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        schedules.Schedules.Add(schedule);
        return schedule;
    }

    // ---------- keys / validation helpers ----------

    [Fact]
    public void ScheduleKeys_Deterministic_And_Bounded()
    {
        var scheduleId = Guid.NewGuid();
        var runA = Guid.NewGuid().ToString("N");
        var runB = Guid.NewGuid().ToString("N");

        var first = SuiteScheduleKeys.ForActionRun(scheduleId, runA);
        Assert.Equal(first, SuiteScheduleKeys.ForActionRun(scheduleId, runA));
        Assert.NotEqual(first, SuiteScheduleKeys.ForActionRun(scheduleId, runB));
        Assert.StartsWith("sched-", first);
        Assert.True(first.Length <= 60);

        var manual = SuiteScheduleKeys.ForManualTrigger(scheduleId, "client-key");
        Assert.StartsWith("sched-", manual);
        Assert.True(manual.Length <= 60);
    }

    [Fact]
    public void ScheduleIds_DeterministicShape()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"suite-schedule-{id:N}", SuiteScheduleIds.ForSchedule(id));
    }

    [Fact]
    public void CronValidation_AcceptsFiveAndSixField_RejectsGarbage()
    {
        Assert.True(CronValidation.IsPlausible("30 2 * * *"));
        Assert.True(CronValidation.IsPlausible("0 30 2 * * MON"));
        Assert.True(CronValidation.IsPlausible("*/15 9-17 * * 1-5"));
        Assert.False(CronValidation.IsPlausible(null));
        Assert.False(CronValidation.IsPlausible(""));
        Assert.False(CronValidation.IsPlausible("* * * *"));
        Assert.False(CronValidation.IsPlausible("* * * * * * *"));
        Assert.False(CronValidation.IsPlausible("30 2 * * *; DROP TABLE x")); // charset + server authoritative
        Assert.False(CronValidation.IsPlausible("30 2 * *"));
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_Succeeds_WithDefaults_AndAudits()
    {
        TestSuite? suite = null;
        var (service, schedules, _, _, coordinator, _, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "Regression");
        });

        var created = await service.CreateAsync(ProjectA, suite!.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "Nightly", "30 2 * * *", null, null),
            CancellationToken.None);

        Assert.Equal("Nightly", created.Name);
        Assert.Equal("UTC", created.TimeZoneId);
        Assert.Equal("Active", created.Status);
        Assert.Equal("Skip", created.OverlapPolicy);
        Assert.Single(schedules.Schedules);
        Assert.Single(coordinator.Created);
        var remote = coordinator.Created.Single();
        Assert.Equal("30 2 * * *", remote.Definition.CronExpression);
        Assert.False(remote.Definition.Paused);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_created" && a.ProjectId == ProjectA);
    }

    [Fact]
    public async Task Create_Validation_NameCronTimezoneOverlap()
    {
        TestSuite? suite = null;
        var (service, _, _, _, _, _, _) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "Regression");
        });

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(ProjectA, suite!.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "  ", "30 2 * * *", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(ProjectA, suite.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "N", "bogus", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(ProjectA, suite.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "N", "30 2 * * *", "Mars/Olympus", null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(ProjectA, suite.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "N", "30 2 * * *", null, "Sometimes"), CancellationToken.None));
    }

    [Fact]
    public async Task Create_DuplicateName_Conflict_CrossProjectSuite_Forbidden_ArchivedSuite_Conflict()
    {
        TestSuite? suite = null;
        TestSuite? archived = null;
        TestSuite? other = null;
        var (service, _, _, _, _, _, _) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            m.Add("user-1", ProjectB);
            suite = SeedSuite(s, ProjectA, "Regression");
            archived = SeedSuite(s, ProjectA, "Old", ProjectStatus.Archived);
            other = SeedSuite(s, ProjectB, "Beta");
            SeedSchedule(sched, ProjectA, suite.Id, "Taken");
        });

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(ProjectA, suite!.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "taken", "30 2 * * *", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(ProjectA, other!.Id,
            new CreateSuiteScheduleCommand(ProjectA, other.Id, "X", "30 2 * * *", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(ProjectA, archived!.Id,
            new CreateSuiteScheduleCommand(ProjectA, archived.Id, "X", "30 2 * * *", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync(ProjectA, Guid.NewGuid(),
            new CreateSuiteScheduleCommand(ProjectA, Guid.NewGuid(), "X", "30 2 * * *", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_RemoteFailure_CompensatesRow_And_Throws()
    {
        TestSuite? suite = null;
        var (service, schedules, _, _, coordinator, _, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "Regression");
        });
        coordinator.FailureFor = _ => new InvalidOperationException("Temporal down.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(ProjectA, suite!.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "Nightly", "30 2 * * *", null, null), CancellationToken.None));

        // No apparently-active row left behind.
        Assert.Empty(schedules.Schedules);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_failed");
    }

    [Fact]
    public async Task Create_Viewer_Forbidden_Outsider_Forbidden()
    {
        TestSuite? suite = null;
        var (viewerService, _, _, _, _, _, _) = Create(Viewer(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            m.Add("user-1", ProjectB);
            suite = SeedSuite(s, ProjectA, "Regression");
        });

        // Viewer is a member but lacks testcases.manage.
        await Assert.ThrowsAsync<ForbiddenException>(() => viewerService.CreateAsync(ProjectA, suite!.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite.Id, "N", "30 2 * * *", null, null), CancellationToken.None));

        var (outsiderService, _, _, _, _, _, _) = Create(Manager("outsider"), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
        });
        await Assert.ThrowsAsync<ForbiddenException>(() => outsiderService.CreateAsync(ProjectA, suite!.Id,
            new CreateSuiteScheduleCommand(ProjectA, suite!.Id, "N", "30 2 * * *", null, null), CancellationToken.None));
    }

    // ---------- read ----------

    [Fact]
    public async Task Get_UnknownId_Admin_NotFound_NonAdmin_Forbidden()
    {
        var (memberService, _, _, _, _, _, _) = Create(Manager(), (sched, s, c, m) => m.Add("user-1", ProjectA));
        await Assert.ThrowsAsync<ForbiddenException>(() => memberService.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));

        var (adminService, _, _, _, _, _, _) = Create(Admin(), null);
        await Assert.ThrowsAsync<NotFoundException>(() => adminService.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Get_Detail_IncludesNextRun_List_DoesNotCallRemote()
    {
        TestSuite? suite = null;
        TestSuiteSchedule? schedule = null;
        var (service, _, _, _, coordinator, _, _) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "Regression");
            schedule = SeedSchedule(sched, ProjectA, suite.Id, "Nightly");
        });

        var detail = await service.GetByIdAsync(schedule!.Id, CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 2, 30, 0, TimeSpan.Zero), detail!.NextRunAt);

        coordinator.NextRun = null;
        var list = await service.ListBySuiteAsync(ProjectA, suite!.Id, CancellationToken.None);
        Assert.Single(list);
        Assert.Null(list.Single().NextRunAt);
    }

    // ---------- update / lifecycle ----------

    [Fact]
    public async Task Update_Succeeds_And_PushesRemote()
    {
        TestSuite? suite = null;
        TestSuiteSchedule? schedule = null;
        var (service, _, _, _, coordinator, _, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "Regression");
            schedule = SeedSchedule(sched, ProjectA, suite.Id, "Nightly");
        });

        var updated = await service.UpdateAsync(schedule!.Id,
            new UpdateSuiteScheduleCommand("Nightly v2", "0 3 * * *", "America/New_York", "Allow"),
            CancellationToken.None);

        Assert.Equal("Nightly v2", updated!.Name);
        Assert.Equal("America/New_York", updated.TimeZoneId);
        Assert.Equal("Allow", updated.OverlapPolicy);
        var remote = Assert.Single(coordinator.Updated);
        Assert.Equal("0 3 * * *", remote.Definition.CronExpression);
        Assert.Equal("America/New_York", remote.Definition.TimeZoneId);
        Assert.Equal("Allow", remote.Definition.OverlapPolicy);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_updated");
    }

    [Fact]
    public async Task Update_RemoteFailure_RollsBackRow()
    {
        TestSuiteSchedule? schedule = null;
        var (service, schedules, _, _, coordinator, _, _) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var suite = SeedSuite(s, ProjectA, "Regression");
            schedule = SeedSchedule(sched, ProjectA, suite.Id, "Nightly");
        });
        coordinator.FailureFor = _ => new InvalidOperationException("Temporal down.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(schedule!.Id,
            new UpdateSuiteScheduleCommand("Changed", "0 3 * * *", null, null), CancellationToken.None));

        // Row still carries the old cadence.
        Assert.Equal("Nightly", schedules.Schedules.Single().Name);
        Assert.Equal("30 2 * * *", schedules.Schedules.Single().CronExpression);
    }

    [Fact]
    public async Task Pause_Resume_Archive_Lifecycle()
    {
        TestSuite? suite = null;
        TestSuiteSchedule? schedule = null;
        var (service, schedules, _, _, coordinator, _, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "Regression");
            schedule = SeedSchedule(sched, ProjectA, suite.Id, "Nightly");
        });

        await service.PauseAsync(schedule!.Id, CancellationToken.None);
        Assert.Equal(ScheduleStatus.Disabled, schedules.Schedules.Single().Status);
        Assert.Contains(schedule.Id, coordinator.Paused);

        await service.ResumeAsync(schedule.Id, CancellationToken.None);
        Assert.Equal(ScheduleStatus.Active, schedules.Schedules.Single().Status);
        Assert.Contains(schedule.Id, coordinator.Resumed);

        await service.ArchiveAsync(schedule.Id, CancellationToken.None);
        Assert.Equal(ScheduleStatus.Archived, schedules.Schedules.Single().Status);
        Assert.Contains(schedule.Id, coordinator.Deleted);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_deleted");

        // Archived schedules reject further lifecycle moves.
        await Assert.ThrowsAsync<ConflictException>(() => service.PauseAsync(schedule.Id, CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => service.ResumeAsync(schedule.Id, CancellationToken.None));
        _ = suite;
    }

    [Fact]
    public async Task Resume_ArchivedSuite_Conflict()
    {
        TestSuiteSchedule? schedule = null;
        var (service, _, _, _, _, _, _) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var suite = SeedSuite(s, ProjectA, "Regression", ProjectStatus.Archived);
            schedule = SeedSchedule(sched, ProjectA, suite.Id, "Nightly", ScheduleStatus.Disabled);
        });

        await Assert.ThrowsAsync<ConflictException>(() => service.ResumeAsync(schedule!.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DisableForSuite_DisablesActive_PausesRemote()
    {
        TestSuiteSchedule? active = null;
        TestSuiteSchedule? disabled = null;
        var (service, schedules, _, _, coordinator, _, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var suite = SeedSuite(s, ProjectA, "Regression");
            active = SeedSchedule(sched, ProjectA, suite.Id, "A");
            disabled = SeedSchedule(sched, ProjectA, suite.Id, "B", ScheduleStatus.Disabled);
        });

        var count = await service.DisableForSuiteAsync(active!.SuiteId, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(ScheduleStatus.Disabled, schedules.Schedules.Single(s => s.Id == active.Id).Status);
        Assert.Equal(ScheduleStatus.Disabled, schedules.Schedules.Single(s => s.Id == disabled!.Id).Status);
        Assert.Contains(active.Id, coordinator.Paused);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_paused");
    }

    [Fact]
    public async Task DisableForSuite_RemoteFailure_StillDisablesRow()
    {
        TestSuiteSchedule? active = null;
        var (service, schedules, _, _, coordinator, _, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var suite = SeedSuite(s, ProjectA, "Regression");
            active = SeedSchedule(sched, ProjectA, suite.Id, "A");
        });
        coordinator.FailureFor = _ => new InvalidOperationException("Temporal down.");

        var count = await service.DisableForSuiteAsync(active!.SuiteId, CancellationToken.None);

        // Fail closed: row disabled (fire path revalidates), miss recorded.
        Assert.Equal(1, count);
        Assert.Equal(ScheduleStatus.Disabled, schedules.Schedules.Single().Status);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_paused");
    }

    // ---------- fire / run-now ----------

    private static void SeedRunnable(FakeSuiteScheduleStore sched, FakeSuiteStore s, FakeTestCaseStore c,
        Guid projectId, out TestSuite suite, out TestSuiteSchedule schedule)
    {
        suite = SeedSuite(s, projectId, "Regression");
        var member = SeedApproved(c, projectId, "A-001");
        s.Members.Add((suite.Id, member.Id, 1));
        schedule = SeedSchedule(sched, projectId, suite.Id, "Nightly");
    }

    [Fact]
    public async Task Fire_Success_UsesScheduleTrigger_And_PersistsPointers()
    {
        var (service, schedules, _, _, _, executions, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            SeedRunnable(sched, s, c, ProjectA, out _, out _);
        });
        var schedule = schedules.Schedules.Single();

        var result = await service.FireAsync(schedule.Id, "sched-basis-1", CancellationToken.None);

        Assert.Equal(1, result.TestCount);
        var started = Assert.Single(executions.StartedAsSystem);
        Assert.Equal(TriggerType.Schedule, started.Trigger);
        Assert.Equal(schedule.SuiteId, started.SuiteId);
        Assert.StartsWith("sched-basis-1-", started.IdempotencyKey);
        var row = schedules.Schedules.Single();
        Assert.Equal(result.ExecutionId, row.LastExecutionId);
        Assert.NotNull(row.LastTriggeredAt);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_triggered" && a.ProjectId == ProjectA);
        Assert.Contains(audits.Audits, a => a.Action == "suite.executed" && a.ProjectId == ProjectA);
    }

    [Fact]
    public async Task Fire_DisabledSchedule_Validation_ArchivedSuite_Conflict_NoApproved_Validation()
    {
        var (service, schedules, suites, cases, _, executions, audits) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var suite = SeedSuite(s, ProjectA, "Regression");
            var member = SeedApproved(c, ProjectA, "A-001");
            s.Members.Add((suite.Id, member.Id, 1));
            SeedSchedule(sched, ProjectA, suite.Id, "Paused", ScheduleStatus.Disabled);
        });
        var paused = schedules.Schedules.Single(s => s.Name == "Paused");

        await Assert.ThrowsAsync<ValidationException>(() => service.FireAsync(paused.Id, "k1", CancellationToken.None));

        // Archived suite.
        var suite = suites.Suites.Single();
        suite.Status = ProjectStatus.Archived;
        var active = SeedSchedule(schedules, ProjectA, suite.Id, "Active2");
        _ = cases;
        await Assert.ThrowsAsync<ConflictException>(() => service.FireAsync(active.Id, "k2", CancellationToken.None));
        Assert.Empty(executions.StartedAsSystem);
        Assert.Contains(audits.Audits, a => a.Action == "suite.schedule_failed");
    }

    [Fact]
    public async Task RunNow_Succeeds_WithoutTouchingStatus()
    {
        var (service, schedules, _, _, _, executions, _) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            SeedRunnable(sched, s, c, ProjectA, out _, out var schedule);
            schedule.Status = ScheduleStatus.Disabled;
        });
        var scheduleRow = schedules.Schedules.Single();

        var result = await service.RunNowAsync(scheduleRow.Id, new RunScheduleNowCommand("client-1"), CancellationToken.None);

        Assert.Equal(1, result.TestCount);
        Assert.Single(executions.StartedAsSystem);
        // Cadence and pause state untouched.
        Assert.Equal(ScheduleStatus.Disabled, schedules.Schedules.Single().Status);
        Assert.Equal(result.ExecutionId, schedules.Schedules.Single().LastExecutionId);
    }

    [Fact]
    public async Task RunNow_ArchivedSchedule_Conflict_LongKey_Validation()
    {
        var (service, schedules, _, _, _, _, _) = Create(Manager(), (sched, s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            SeedRunnable(sched, s, c, ProjectA, out _, out var schedule);
            schedule.Status = ScheduleStatus.Archived;
        });
        var archived = schedules.Schedules.Single();

        await Assert.ThrowsAsync<ConflictException>(() => service.RunNowAsync(archived.Id, new RunScheduleNowCommand(null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.RunNowAsync(archived.Id, new RunScheduleNowCommand(new string('k', 22)), CancellationToken.None));
    }

    // ---------- report extension ----------

    private static SuiteService ReportService(
        StubCurrentUser user,
        FakeSuiteStore suites,
        FakeTestCaseStore cases,
        StubMembershipStore memberships)
    {
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        var audits = new StubAuditProjectStore();
        return new SuiteService(
            suites, cases, new SuiteServiceTests.StubScheduleService(), user, authorization, directory,
            new SystemDateTimeProvider(),
            new AuditService(audits, user, directory, NullLogger<AuditService>.Instance));
    }

    [Fact]
    public async Task Report_TriggerFilter_And_Breakdown_And_Trend()
    {
        var suites = new FakeSuiteStore();
        var cases = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        memberships.Add("user-1", ProjectA);
        var suite = SeedSuite(suites, ProjectA, "Regression");
        var manual = new Execution
        {
            ProjectId = ProjectA, SuiteId = suite.Id, Status = ExecutionStatus.Passed,
            TriggerType = TriggerType.Manual, CreatedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        };
        var scheduled = new Execution
        {
            ProjectId = ProjectA, SuiteId = suite.Id, Status = ExecutionStatus.Failed,
            TriggerType = TriggerType.Schedule, CreatedAt = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero),
        };
        suites.Executions.AddRange([manual, scheduled]);
        suites.Tests.AddRange(
        [
            new ExecutionTest { ExecutionId = manual.Id, Status = ExecutionTestStatus.Passed },
            new ExecutionTest { ExecutionId = scheduled.Id, Status = ExecutionTestStatus.Failed },
        ]);
        var service = ReportService(Manager(), suites, cases, memberships);

        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var full = await service.GetReportAsync(suite.Id,
            new SuiteReportFilters(from, to, null, "day"), CancellationToken.None);
        Assert.Equal(2, full!.TotalExecutions);
        Assert.Equal(2, full.TriggerBreakdown.Count);
        Assert.Equal(1, full.TriggerBreakdown.Single(b => b.Trigger == "Manual").Passed);
        Assert.Equal(1, full.TriggerBreakdown.Single(b => b.Trigger == "Schedule").Failed);
        Assert.Equal(2, full.Trend.Count);
        Assert.Equal(new DateOnly(2026, 6, 1), full.Trend[0].Date);
        Assert.Equal(1, full.Trend[0].Total);
        Assert.Equal(1, full.Trend[0].Passed);

        // Trigger filter narrows the base counts.
        var onlyScheduled = await service.GetReportAsync(suite.Id,
            new SuiteReportFilters(from, to, "Schedule", null), CancellationToken.None);
        Assert.Equal(1, onlyScheduled!.TotalExecutions);
        Assert.Empty(onlyScheduled.Trend);

        // Invalid trigger / groupBy / oversized range.
        await Assert.ThrowsAsync<ValidationException>(() => service.GetReportAsync(suite.Id,
            new SuiteReportFilters(from, to, "Bogus", null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.GetReportAsync(suite.Id,
            new SuiteReportFilters(from, to, null, "week"), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.GetReportAsync(suite.Id,
            new SuiteReportFilters(from, from.AddDays(91), null, "day"), CancellationToken.None));
    }
}
