using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 3C-4B-1: session ownership/lifecycle through the real scheduler +
/// session service + EF InMemory. No Appium server or device required:
/// session ids are synthetic, fencing is real.
/// </summary>
public sealed class MobileSessionLifecycleTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid AdminRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    public MobileSessionLifecycleTests(Slice1ApiFactory factory) => _factory = factory;

    private static readonly IReadOnlySet<Guid> NoExclusions = new HashSet<Guid>();

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            var pa = _projectA;
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any(r => r.Id == AdminRoleId))
                    db.Roles.Add(new Role { Id = AdminRoleId, Name = "admin" });
                var admin = new User { ExternalIdentityId = "ex-mob-session-admin", Email = "sess-admin@x", DisplayName = "Admin" };
                db.Users.Add(admin);
                db.Projects.Add(new Project { Id = pa, Name = "Session Alpha", Key = "SSA" });
                db.ProjectMembers.Add(new ProjectMember { ProjectId = pa, UserId = admin.Id, RoleId = AdminRoleId });
                return Task.CompletedTask;
            });
            _seeded = true;
        }
        finally { _seedLock.Release(); }
    }

    private sealed class Scenario
    {
        public Guid PoolId;
        public Guid DeviceId;
        public Guid ExecutionId;
        public Guid TestId;
        public Guid AppId;
    }

    private async Task<Scenario> SeedScenarioAsync()
    {
        var s = new Scenario();
        await _factory.SeedAsync(db =>
        {
            s.PoolId = Guid.NewGuid();
            s.DeviceId = Guid.NewGuid();
            s.ExecutionId = Guid.NewGuid();
            s.TestId = Guid.NewGuid();
            s.AppId = Guid.NewGuid();
            db.MobileDevicePools.Add(new MobileDevicePool
            {
                Id = s.PoolId, ProjectId = _projectA, Name = "pool-" + s.PoolId.ToString("N")[..8],
                Platform = MobilePlatform.Android, Status = MobilePoolStatus.Active,
            });
            db.MobileDevices.Add(new MobileDevice
            {
                Id = s.DeviceId, ProjectId = _projectA, PoolId = s.PoolId,
                Platform = MobilePlatform.Android, AutomationName = MobileAutomationNames.UiAutomator2,
                Status = MobileDeviceStatus.Available,
            });
            db.MobileDeviceSlots.Add(new MobileDeviceSlot
            {
                Id = Guid.NewGuid(), ProjectId = _projectA, PoolId = s.PoolId, DeviceId = s.DeviceId,
                SlotNumber = 1, Status = MobileSlotStatus.Free,
            });
            db.GridWorkers.Add(new GridWorker
            {
                Id = Guid.NewGuid(), WorkerKey = "appium-" + Guid.NewGuid().ToString("N")[..8],
                WorkerType = "appium", Framework = "appium", Browsers = new List<string>(),
                Status = GridWorkerStatus.Available, Capacity = 4,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
            });
            db.MobileApps.Add(new MobileApp
            {
                Id = s.AppId, ProjectId = _projectA, Platform = MobilePlatform.Android,
                Name = "Shop", PackageId = "com.example.shop",
            });
            db.Executions.Add(new Execution
            {
                Id = s.ExecutionId, ProjectId = _projectA, Status = ExecutionStatus.Queued,
                MobileDevicePoolId = s.PoolId, MobileAppId = s.AppId,
            });
            db.ExecutionTests.Add(new ExecutionTest
            {
                Id = s.TestId, ExecutionId = s.ExecutionId, TestCaseId = Guid.NewGuid(),
                Status = ExecutionTestStatus.Queued, Framework = "appium", Attempt = 1,
            });
            return Task.CompletedTask;
        });
        return s;
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    [Fact]
    public async Task FullLifecycle_Creating_Active_Closed_WithFencing()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();

        var claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.NotNull(claim);

        var sessionId = await InScopeAsync(async sp =>
        {
            var sessions = sp.GetRequiredService<IMobileSessionService>();
            var slot = (await sp.GetRequiredService<IMobileRegistryStore>()
                .FindSlotByAssignmentAsync(claim!.Assignment.Id, CancellationToken.None))!;
            var created = await sessions.CreateAsync(
                _projectA, slot.DeviceId, slot.Id, s.ExecutionId, claim.Assignment.Id, CancellationToken.None);
            Assert.Equal(MobileSessionStatus.Creating, created.Status);
            var active = await sessions.ActivateAsync(
                _projectA, created.Id, claim.Assignment.Id, claim.AssignmentToken, "appium-sess-1", CancellationToken.None);
            Assert.Equal(MobileSessionStatus.Active, active.Status);
            Assert.Equal(s.ExecutionId, active.ExecutionId);
            Assert.Equal(slot.Id, active.DeviceSlotId);
            await sessions.HeartbeatAsync(_projectA, created.Id, claim.Assignment.Id, claim.AssignmentToken, CancellationToken.None);
            await sessions.CloseAsync(_projectA, created.Id, claim.Assignment.Id, claim.AssignmentToken, CancellationToken.None);
            // Idempotent second close.
            await sessions.CloseAsync(_projectA, created.Id, claim.Assignment.Id, claim.AssignmentToken, CancellationToken.None);
            return created.Id;
        });

        await InScopeAsync(async sp =>
        {
            var store = sp.GetRequiredService<IMobileRegistryStore>();
            var row = (await store.GetSessionByIdAsync(sessionId, CancellationToken.None))!;
            Assert.Equal(MobileSessionStatus.Closed, row.Status);
            Assert.NotNull(row.ClosedAt);
            Assert.Equal("appium-sess-1", row.AppiumSessionId);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StaleToken_CannotActivate_And_CrossProject_Rejected()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();

        var claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.NotNull(claim);

        await InScopeAsync(async sp =>
        {
            var sessions = sp.GetRequiredService<IMobileSessionService>();
            var store = sp.GetRequiredService<IMobileRegistryStore>();
            var slot = (await store.FindSlotByAssignmentAsync(claim!.Assignment.Id, CancellationToken.None))!;
            var created = await sessions.CreateAsync(
                _projectA, slot.DeviceId, slot.Id, s.ExecutionId, claim.Assignment.Id, CancellationToken.None);
            await Assert.ThrowsAsync<ConflictException>(() => sessions.ActivateAsync(
                _projectA, created.Id, claim.Assignment.Id, Guid.NewGuid(), "s-1", CancellationToken.None));
            await Assert.ThrowsAsync<NotFoundException>(() => sessions.HeartbeatAsync(
                Guid.NewGuid(), created.Id, claim.Assignment.Id, claim.AssignmentToken, CancellationToken.None));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Renewal_TouchesSessionHeartbeat()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();

        var claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.NotNull(claim);

        DateTimeOffset? before = await InScopeAsync(async sp =>
        {
            var sessions = sp.GetRequiredService<IMobileSessionService>();
            var store = sp.GetRequiredService<IMobileRegistryStore>();
            var slot = (await store.FindSlotByAssignmentAsync(claim!.Assignment.Id, CancellationToken.None))!;
            var created = await sessions.CreateAsync(
                _projectA, slot.DeviceId, slot.Id, s.ExecutionId, claim.Assignment.Id, CancellationToken.None);
            await sessions.ActivateAsync(
                _projectA, created.Id, claim.Assignment.Id, claim.AssignmentToken, "s-renew-1", CancellationToken.None);
            return ((await store.GetSessionByIdAsync(created.Id, CancellationToken.None))!).LastHeartbeatAt;
        });

        await Task.Delay(10);
        await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().RenewLeaseAsync(claim!.Assignment.Id, CancellationToken.None));

        var after = await InScopeAsync(async sp =>
        {
            var store = sp.GetRequiredService<IMobileRegistryStore>();
            var session = await store.FindSessionByAssignmentAsync(claim!.Assignment.Id, CancellationToken.None);
            return session!.LastHeartbeatAt;
        });
        Assert.True(after >= before);
    }
}
