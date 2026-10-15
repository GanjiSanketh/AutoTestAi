using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Maintenance;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>In-memory IMaintenanceScanScheduleStore fake.</summary>
internal sealed class FakeMaintenanceScanScheduleStore : IMaintenanceScanScheduleStore
{
    public readonly List<MaintenanceScanSchedule> Schedules = new();
    public Func<Exception?>? SaveFailure { get; set; }

    public Task<MaintenanceScanSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct)
        => Task.FromResult(Schedules.FirstOrDefault(s => s.Id == scheduleId));

    public Task<MaintenanceScanSchedule?> GetByProjectIdAsync(Guid projectId, CancellationToken ct)
        => Task.FromResult(Schedules.FirstOrDefault(s => s.ProjectId == projectId));

    public Task AddAsync(MaintenanceScanSchedule schedule, CancellationToken ct)
    {
        Schedules.Add(schedule);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(MaintenanceScanSchedule schedule, CancellationToken ct)
    {
        Schedules.Remove(schedule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        var failure = SaveFailure?.Invoke();
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }
}

/// <summary>Recording IMaintenanceScanScheduleCoordinator fake: no Temporal required.</summary>
internal sealed class FakeMaintenanceScanCoordinator : IMaintenanceScanScheduleCoordinator
{
    public bool IsConfigured { get; set; } = true;
    public readonly List<(Guid Id, MaintenanceScanScheduleDefinition Definition)> Created = new();
    public readonly List<(Guid Id, MaintenanceScanScheduleDefinition Definition)> Updated = new();
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

    public Task CreateAsync(Guid scheduleId, MaintenanceScanScheduleDefinition definition, CancellationToken ct)
    {
        MaybeFail(scheduleId, "create");
        Created.Add((scheduleId, definition));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Guid scheduleId, MaintenanceScanScheduleDefinition definition, CancellationToken ct)
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

/// <summary>Stub IMaintenanceService: records whether the auth-free scan core was used.</summary>
internal sealed class StubScanCoreMaintenanceService : IMaintenanceService
{
    public int ManualScans;
    public int SystemScans;
    public Func<Guid, MaintenanceScanResult>? ResultFor { get; set; }

    public Task<MaintenanceScanResult> ScanAsync(Guid projectId, CancellationToken ct)
    {
        ManualScans++;
        throw new NotImplementedException("Manual scans are not exercised through the schedule service.");
    }

    public Task<MaintenanceScanResult> ScanAsSystemAsync(Guid projectId, CancellationToken ct)
    {
        SystemScans++;
        return Task.FromResult(ResultFor?.Invoke(projectId)
            ?? new MaintenanceScanResult(0, 0, 0, 0, DateTimeOffset.UtcNow));
    }

    public Task<PagedResult<MaintenanceProposalDto>> ListAsync(
        Guid projectId, MaintenanceProposalFilters filters, int page, int pageSize, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<MaintenanceProposalDetailDto> GetAsync(Guid projectId, Guid proposalId, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<MaintenanceApproveResult> ApproveAsync(Guid projectId, Guid proposalId, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<MaintenanceProposalDto> RejectAsync(Guid projectId, Guid proposalId, string? reason, CancellationToken ct)
        => throw new NotImplementedException();
}

/// <summary>
/// Phase 4 Slice 10: maintenance scan schedules — cadence validation, the
/// one-per-project boundary, lifecycle/consistency, fire-time revalidation,
/// overlap=Skip, and human-gate preservation (scheduled scans create
/// proposals but never approve/apply them).
/// </summary>
public sealed class MaintenanceScanScheduleServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private sealed class Harness
    {
        public readonly FakeMaintenanceScanScheduleStore Schedules = new();
        public readonly FakeMaintenanceScanCoordinator Coordinator = new();
        public readonly StubScanCoreMaintenanceService Maintenance = new();
        public readonly FakeProjectStore Projects = new();
        public readonly MaintenanceScanScheduleService Service;

        public Harness(StubCurrentUser user, bool member = true)
        {
            var memberships = new StubMembershipStore();
            if (member && user.ExternalIdentityId is not null)
            {
                memberships.Add(user.ExternalIdentityId, ProjectA);
                memberships.Add(user.ExternalIdentityId, ProjectB);
            }
            Projects.Projects.Add(new Project { Id = ProjectA, Name = "Alpha", Key = "A" });
            Projects.Projects.Add(new Project { Id = ProjectB, Name = "Beta", Key = "B" });
            var directory = new FakeUserDirectory();
            var authorization = new AuthorizationService(user, memberships);
            // Audits flow through the shared FakeProjectStore so action names
            // stay assertable without a second store.
            Service = new MaintenanceScanScheduleService(
                Schedules, Maintenance, Projects, Coordinator, authorization,
                user, directory, new SystemDateTimeProvider(),
                new AuditService(Projects, user, directory, NullLogger<AuditService>.Instance));
        }
    }

    private static StubCurrentUser Manager(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["qa-lead"],
        Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static StubCurrentUser Viewer(string sub = "viewer-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["viewer"],
        Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private static MaintenanceScanSchedule SeedSchedule(
        FakeMaintenanceScanScheduleStore store, Guid projectId,
        ScheduleStatus status = ScheduleStatus.Active)
    {
        var schedule = new MaintenanceScanSchedule
        {
            ProjectId = projectId,
            CronExpression = "30 2 * * *",
            TimeZoneId = "UTC",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        store.Schedules.Add(schedule);
        return schedule;
    }

    // ---------- cron floor ----------

    [Theory]
    [InlineData("30 2 * * *")]
    [InlineData("0 * * * *")]
    [InlineData("5 0 1 * *")]
    [InlineData("0 0 * * 1")]
    [InlineData("5 30 2 * * *")]
    public void HourlyFloor_AllowsHourlyOrSlower(string cron)
        => Assert.False(MaintenanceCronPolicy.IsMoreFrequentThanHourly(cron));

    [Theory]
    [InlineData("*/15 * * * *")]
    [InlineData("* * * * *")]
    [InlineData("5,10 * * * *")]
    [InlineData("5-55 * * * *")]
    [InlineData("*/5 * * * * *")]
    [InlineData("* 30 2 * * *")]
    [InlineData("not-a-cron")]
    [InlineData("")]
    [InlineData(null)]
    public void HourlyFloor_RejectsSubHourlyOrImplausible(string? cron)
        => Assert.True(MaintenanceCronPolicy.IsMoreFrequentThanHourly(cron));

    [Fact]
    public void ScheduleIds_MaintenanceNamespace()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"maintenance-scan-{id:N}", MaintenanceScanScheduleIds.ForSchedule(id));
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_PersistsActive_And_CreatesRemote_WithSkip()
    {
        var h = new Harness(Manager());
        var dto = await h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "UTC"), CancellationToken.None);

        Assert.Equal("Active", dto.Status);
        Assert.Equal("30 2 * * *", dto.CronExpression);
        Assert.Null(dto.NextRunAt); // ordinary reads never pay a Temporal RPC
        var created = Assert.Single(h.Coordinator.Created);
        Assert.Equal(dto.Id, created.Id);
        Assert.False(created.Definition.Paused);
        Assert.Contains(h.Projects.Audits, a => a.Action == "maintenance.scan_scheduled");
    }

    [Fact]
    public async Task Create_RejectsSubHourly_And_UnknownTimezone()
    {
        var h = new Harness(Manager());
        await Assert.ThrowsAsync<ValidationException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("*/15 * * * *", "UTC"), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "Mars/Olympus"), CancellationToken.None));
        Assert.Empty(h.Schedules.Schedules);
        Assert.Empty(h.Coordinator.Created);
    }

    [Fact]
    public async Task Create_Duplicate_Conflicts()
    {
        var h = new Harness(Manager());
        SeedSchedule(h.Schedules, ProjectA);
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 3 * * *", "UTC"), CancellationToken.None));
        Assert.Empty(h.Coordinator.Created);
    }

    [Fact]
    public async Task Create_ArchivedProject_Conflicts()
    {
        var h = new Harness(Manager());
        h.Projects.Projects.First(p => p.Id == ProjectA).Status = ProjectStatus.Archived;
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "UTC"), CancellationToken.None));
    }

    [Fact]
    public async Task Create_Viewer_Forbidden()
    {
        var h = new Harness(Viewer());
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "UTC"), CancellationToken.None));
    }

    [Fact]
    public async Task Create_RemoteFailure_CompensatesRow_And_Audits()
    {
        var h = new Harness(Manager());
        h.Coordinator.FailureFor = _ => new InvalidOperationException("Temporal is not configured (create).");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "UTC"), CancellationToken.None));
        // Never leave a falsely Active database record.
        Assert.Empty(h.Schedules.Schedules);
        Assert.Contains(h.Projects.Audits, a => a.Action == "maintenance.scan_failed");
    }

    [Fact]
    public async Task Create_AfterDelete_ReplacesArchivedRow()
    {
        var h = new Harness(Manager());
        SeedSchedule(h.Schedules, ProjectA, ScheduleStatus.Archived);
        var dto = await h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "UTC"), CancellationToken.None);
        Assert.Equal("Active", dto.Status);
        Assert.Single(h.Schedules.Schedules); // UNIQUE(ProjectId) never violated
    }

    [Fact]
    public async Task Create_ConcurrentDuplicate_TranslatesToConflict()
    {
        var h = new Harness(Manager());
        // Postgres unique-violation shape for the one-per-project index.
        h.Schedules.SaveFailure = () => new InvalidOperationException(
            "duplicate key value violates unique constraint IX_maintenance_scan_schedules_ProjectId (SQLSTATE 23505)");
        var ex = await Assert.ThrowsAsync<ConflictException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "UTC"), CancellationToken.None));
        Assert.Contains("already has a maintenance scan schedule", ex.Message);
    }

    [Fact]
    public async Task Create_NonUniqueFailure_PropagatesUntranslated()
    {
        var h = new Harness(Manager());
        h.Schedules.SaveFailure = () => new InvalidOperationException(
            "insert on table maintenance_scan_schedules violates foreign key constraint FK_maintenance_scan_schedules_projects_ProjectId");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.CreateAsync(ProjectA,
            new CreateMaintenanceScanScheduleCommand("30 2 * * *", "UTC"), CancellationToken.None));
    }

    // ---------- read / update ----------

    [Fact]
    public async Task GetByProject_ReturnsDto_WithoutRemoteCall()
    {
        var h = new Harness(Viewer());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        var dto = await h.Service.GetByProjectAsync(ProjectA, CancellationToken.None);
        Assert.NotNull(dto);
        Assert.Equal(seeded.Id, dto!.Id);
        Assert.Null(dto.NextRunAt);
        Assert.Empty(h.Coordinator.Created);
        Assert.Empty(h.Coordinator.Updated);
    }

    [Fact]
    public async Task GetByProject_Missing_ReturnsNull()
    {
        var h = new Harness(Viewer());
        Assert.Null(await h.Service.GetByProjectAsync(ProjectA, CancellationToken.None));
    }

    [Fact]
    public async Task Update_ReconcilesDbAndRemote()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        var dto = await h.Service.UpdateAsync(ProjectA,
            new UpdateMaintenanceScanScheduleCommand("15 4 * * *", "America/New_York"), CancellationToken.None);
        Assert.NotNull(dto);
        Assert.Equal("15 4 * * *", dto!.CronExpression);
        Assert.Equal("America/New_York", dto.TimeZoneId);
        var updated = Assert.Single(h.Coordinator.Updated);
        Assert.Equal(seeded.Id, updated.Id);
        Assert.Equal("15 4 * * *", updated.Definition.CronExpression);
    }

    [Fact]
    public async Task Update_RemoteFailure_RollsBackDbValues()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        h.Coordinator.FailureFor = _ => new InvalidOperationException("Temporal is not configured (update).");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.UpdateAsync(ProjectA,
            new UpdateMaintenanceScanScheduleCommand("15 4 * * *", "UTC"), CancellationToken.None));
        Assert.Equal("30 2 * * *", seeded.CronExpression);
    }

    [Fact]
    public async Task Update_RejectsSubHourly()
    {
        var h = new Harness(Manager());
        SeedSchedule(h.Schedules, ProjectA);
        await Assert.ThrowsAsync<ValidationException>(() => h.Service.UpdateAsync(ProjectA,
            new UpdateMaintenanceScanScheduleCommand("* * * * *", "UTC"), CancellationToken.None));
    }

    // ---------- pause / resume / delete ----------

    [Fact]
    public async Task Pause_RemoteFirst_ThenDisables()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        await h.Service.PauseAsync(ProjectA, CancellationToken.None);
        Assert.Contains(seeded.Id, h.Coordinator.Paused);
        Assert.Equal(ScheduleStatus.Disabled, seeded.Status);
    }

    [Fact]
    public async Task Pause_RemoteFailure_LeavesRowActive()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        h.Coordinator.FailureFor = _ => new InvalidOperationException("Temporal is not configured (pause).");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.PauseAsync(ProjectA, CancellationToken.None));
        // Remote-first: the row never claims a pause Temporal did not confirm.
        Assert.Equal(ScheduleStatus.Active, seeded.Status);
        Assert.DoesNotContain(h.Projects.Audits, a => a.Action == "maintenance.scan_scheduled");
    }

    [Fact]
    public async Task Resume_RequiresScannableProject()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA, ScheduleStatus.Disabled);
        await h.Service.ResumeAsync(ProjectA, CancellationToken.None);
        Assert.Contains(seeded.Id, h.Coordinator.Resumed);
        Assert.Equal(ScheduleStatus.Active, seeded.Status);

        h.Projects.Projects.First(p => p.Id == ProjectB).Status = ProjectStatus.Archived;
        SeedSchedule(h.Schedules, ProjectB, ScheduleStatus.Disabled);
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.ResumeAsync(ProjectB, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Archives_And_RemovesRemote()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        await h.Service.DeleteAsync(ProjectA, CancellationToken.None);
        Assert.Contains(seeded.Id, h.Coordinator.Deleted);
        Assert.Equal(ScheduleStatus.Archived, seeded.Status);
        // Archived schedules read as missing.
        Assert.Null(await h.Service.GetByProjectAsync(ProjectA, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Missing_IsIdempotent()
    {
        var h = new Harness(Manager());
        await h.Service.DeleteAsync(ProjectA, CancellationToken.None);
        Assert.Empty(h.Coordinator.Deleted);
    }

    // ---------- next run ----------

    [Fact]
    public async Task GetNextRun_Explicit_UsesSingleDescribe()
    {
        var h = new Harness(Viewer());
        SeedSchedule(h.Schedules, ProjectA);
        var next = await h.Service.GetNextRunAsync(ProjectA, CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task GetNextRun_Missing_ReturnsNull()
    {
        var h = new Harness(Viewer());
        Assert.Null(await h.Service.GetNextRunAsync(ProjectA, CancellationToken.None));
    }

    // ---------- fire ----------

    [Fact]
    public async Task Fire_UsesSharedScanCore_And_RecordsLastRun()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        h.Maintenance.ResultFor = _ => new MaintenanceScanResult(2, 1, 1, 0, DateTimeOffset.UtcNow);

        var result = await h.Service.FireAsync(seeded.Id, CancellationToken.None);

        Assert.Equal(1, h.Maintenance.SystemScans);
        Assert.Equal(0, h.Maintenance.ManualScans);
        Assert.Equal(2, result.CandidatesDetected);
        Assert.NotNull(seeded.LastTriggeredAt);
        Assert.Equal(2, seeded.LastCandidatesDetected);
        Assert.Equal(1, seeded.LastProposalsCreated);
        Assert.Contains(h.Projects.Audits, a => a.Action == "maintenance.scan_triggered");
    }

    [Fact]
    public async Task Fire_DisabledSchedule_FailsClosed_WithoutScanning()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA, ScheduleStatus.Disabled);
        await Assert.ThrowsAsync<ValidationException>(
            () => h.Service.FireAsync(seeded.Id, CancellationToken.None));
        Assert.Equal(0, h.Maintenance.SystemScans);
        Assert.Contains(h.Projects.Audits, a => a.Action == "maintenance.scan_failed");
    }

    [Fact]
    public async Task Fire_ArchivedProject_FailsClosed_WithoutScanning()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        h.Projects.Projects.First(p => p.Id == ProjectA).Status = ProjectStatus.Archived;
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.FireAsync(seeded.Id, CancellationToken.None));
        Assert.Equal(0, h.Maintenance.SystemScans);
    }

    [Fact]
    public async Task Fire_MissingSchedule_FailsClosed()
    {
        var h = new Harness(Manager());
        await Assert.ThrowsAsync<NotFoundException>(
            () => h.Service.FireAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, h.Maintenance.SystemScans);
    }

    [Fact]
    public async Task Fire_NeverApprovesOrApplies_HumanGatePreserved()
    {
        var h = new Harness(Manager());
        var seeded = SeedSchedule(h.Schedules, ProjectA);
        await h.Service.FireAsync(seeded.Id, CancellationToken.None);

        // The fire path only reaches the scan core: no approval, rejection,
        // or version-mutating audit events exist, and the stub maintenance
        // surface used here exposes no apply path at all.
        Assert.DoesNotContain(h.Projects.Audits, a => a.Action == "maintenance.approved");
        Assert.DoesNotContain(h.Projects.Audits, a => a.Action == "maintenance.applied");
        Assert.DoesNotContain(h.Projects.Audits, a => a.Action == "maintenance.rejected");
    }
}
