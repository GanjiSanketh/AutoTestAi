using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 3C-3: mobile ownership transaction, renewal/release/recovery, and
/// scheduling guards through the real scheduler + EF InMemory. No Appium,
/// devices, or worker execution involved. InMemory does not enforce
/// concurrency tokens — race atomicity is proven by unit version-emulation;
/// these tests prove state-machine and integration correctness.
/// </summary>
public sealed class MobileSchedulingTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid AdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    public MobileSchedulingTests(Slice1ApiFactory factory) => _factory = factory;

    private static readonly IReadOnlySet<Guid> NoExclusions = new HashSet<Guid>();

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                    db.Roles.Add(new Role { Id = AdminRoleId, Name = "admin" });
                var admin = new User { ExternalIdentityId = "ex-mob-sched-admin", Email = "admin@x", DisplayName = "Admin" };
                db.Users.Add(admin);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Sched Alpha", Key = "SCA" },
                    new Project { Id = pb, Name = "Sched Beta", Key = "SCB" });
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
        public Guid SlotId;
        public Guid WorkerId;
        public Guid ExecutionId;
        public Guid TestId;
        public Guid AppId;
    }

    private async Task<Scenario> SeedScenarioAsync(bool withApp = false, Guid? projectOverride = null)
    {
        var project = projectOverride ?? _projectA;
        var s = new Scenario();
        await _factory.SeedAsync(db =>
        {
            s.PoolId = Guid.NewGuid();
            s.DeviceId = Guid.NewGuid();
            s.SlotId = Guid.NewGuid();
            s.WorkerId = Guid.NewGuid();
            s.ExecutionId = Guid.NewGuid();
            s.TestId = Guid.NewGuid();
            db.MobileDevicePools.Add(new MobileDevicePool
            {
                Id = s.PoolId, ProjectId = project, Name = "pool-" + s.PoolId.ToString("N")[..8],
                Platform = MobilePlatform.Android, Status = MobilePoolStatus.Active,
            });
            db.MobileDevices.Add(new MobileDevice
            {
                Id = s.DeviceId, ProjectId = project, PoolId = s.PoolId,
                Platform = MobilePlatform.Android, AutomationName = MobileAutomationNames.UiAutomator2,
                Status = MobileDeviceStatus.Available,
            });
            db.MobileDeviceSlots.Add(new MobileDeviceSlot
            {
                Id = s.SlotId, ProjectId = project, PoolId = s.PoolId, DeviceId = s.DeviceId,
                SlotNumber = 1, Status = MobileSlotStatus.Free,
            });
            db.GridWorkers.Add(new GridWorker
            {
                Id = s.WorkerId, WorkerKey = "appium-" + s.WorkerId.ToString("N")[..8],
                WorkerType = "appium", Framework = "appium", Browsers = new List<string>(),
                Status = GridWorkerStatus.Available, Capacity = 4,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
            });
            if (withApp)
            {
                s.AppId = Guid.NewGuid();
                db.MobileApps.Add(new MobileApp
                {
                    Id = s.AppId, ProjectId = project, Platform = MobilePlatform.Android,
                    Name = "Shop", PackageId = "com.example.shop",
                });
            }
            db.Executions.Add(new Execution
            {
                Id = s.ExecutionId, ProjectId = project, Status = ExecutionStatus.Queued,
                MobileDevicePoolId = s.PoolId, MobileAppId = withApp ? s.AppId : null,
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
    public async Task SuccessfulOwnership_BindsEverything()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync(withApp: true);

        var claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None));

        Assert.NotNull(claim);
        // Workers are global resources shared across scenarios in this
        // suite: the winner may legitimately be another idle worker.
        // Slots are pool-scoped, so the slot must always be our own.
        Assert.Equal(s.SlotId, claim!.Slot.Id);
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var allForTest = await db.GridAssignments.Where(a => a.ExecutionTestId == s.TestId).ToListAsync();
            Assert.Single(allForTest);
            var assignment = allForTest[0];
            Assert.Equal(GridAssignmentStatus.Claimed, assignment.Status);
            var slot = await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId);
            Assert.Equal(MobileSlotStatus.Claimed, slot.Status);
            Assert.Equal(assignment.Id, slot.AssignmentId);
            Assert.Equal(claim!.Worker.Id, slot.WorkerId);
            Assert.NotNull(slot.ClaimToken);
            Assert.NotNull(slot.ClaimExpiresAt);
            Assert.NotEqual(assignment.AssignmentToken, slot.ClaimToken);
            var worker = await db.GridWorkers.FirstAsync(w => w.Id == claim!.Worker.Id);
            Assert.Equal(1, worker.ActiveAssignmentCount);
            var test = await db.ExecutionTests.FirstAsync(t => t.Id == s.TestId);
            Assert.Equal(assignment.Id, test.AssignmentId);
            Assert.Equal(assignment.AssignmentToken, test.AssignmentToken);
            return true;
        });
    }

    [Fact]
    public async Task ValidationFailure_LeavesNothingBehind()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var pool = await db.MobileDevicePools.FirstAsync(p => p.Id == s.PoolId);
            pool.Status = MobilePoolStatus.Disabled;
            await db.SaveChangesAsync();
            return true;
        });

        await Assert.ThrowsAsync<AutoTestAi.Application.Common.ConflictException>(() =>
            InScopeAsync(sp => sp.GetRequiredService<IGridScheduler>()
                .TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None)));

        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            Assert.Empty(await db.GridAssignments.Where(a => a.ExecutionTestId == s.TestId).ToListAsync());
            var slot = await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId);
            Assert.Equal(MobileSlotStatus.Free, slot.Status);
            Assert.Null(slot.ClaimToken);
            Assert.Null(slot.AssignmentId);
            Assert.Equal(0, (await db.GridWorkers.FirstAsync(w => w.Id == s.WorkerId)).ActiveAssignmentCount);
            return true;
        });
    }

    [Fact]
    public async Task Renew_Release_Reap_Flow()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();
        var claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.NotNull(claim);

        var before = await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            return (await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId)).ClaimExpiresAt;
        });
        await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().RenewLeaseAsync(claim!.Assignment.Id, CancellationToken.None));
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            Assert.True((await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId)).ClaimExpiresAt >= before);
            return true;
        });

        var test = await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            return await db.ExecutionTests.FirstAsync(t => t.Id == s.TestId);
        });
        await InScopeAsync(sp =>
            sp.GetRequiredService<IGridLeaseManager>().ReleaseAssignmentAsync(
                test.Id, claim!.Assignment.Id, claim.Assignment.AssignmentToken, "Completed", CancellationToken.None));
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var slot = await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId);
            Assert.Equal(MobileSlotStatus.Released, slot.Status);
            Assert.Null(slot.ClaimToken);
            Assert.Equal(claim!.Assignment.Id, slot.AssignmentId);
            return true;
        });

        var reaped = await InScopeAsync(sp =>
            sp.GetRequiredService<IMobileSlotLeaseService>().ReapExpiredSlotsAsync(CancellationToken.None));
        Assert.Equal(1, reaped);
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var slot = await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId);
            Assert.Equal(MobileSlotStatus.Free, slot.Status);
            Assert.Null(slot.AssignmentId);
            return true;
        });
    }

    [Fact]
    public async Task ActiveAssignment_BlocksSlotReclaim()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();
        var claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.NotNull(claim);

        var reaped = await InScopeAsync(sp =>
            sp.GetRequiredService<IMobileSlotLeaseService>().ReapExpiredSlotsAsync(CancellationToken.None));
        Assert.Equal(0, reaped);
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            Assert.Equal(MobileSlotStatus.Claimed,
                (await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId)).Status);
            return true;
        });
    }

    [Fact]
    public async Task CrossProjectPool_IsRejected()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync(projectOverride: _projectB);
        // Execution lives in A but references B's pool: rewire to prove isolation.
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var execution = await db.Executions.FirstAsync(e => e.Id == s.ExecutionId);
            execution.ProjectId = _projectA;
            await db.SaveChangesAsync();
            return true;
        });
        await Assert.ThrowsAsync<AutoTestAi.Application.Common.ConflictException>(() =>
            InScopeAsync(sp => sp.GetRequiredService<IGridScheduler>()
                .TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None)));
    }

    [Fact]
    public async Task Guards_RejectIneligibleCandidates()
    {
        await SeedOnceAsync();

        // Disabled device: pool-local, so no candidate slot exists at all.
        var s1 = await SeedScenarioAsync();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            (await db.MobileDevices.FirstAsync(d => d.Id == s1.DeviceId)).Status = MobileDeviceStatus.Disabled;
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Null(await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s1.ExecutionId, NoExclusions, CancellationToken.None)));

        // Drained worker: the scheduler must skip it. A claim via a different
        // healthy worker is correct behavior (workers are global resources),
        // so assert the drained worker itself is never selected.
        var s2 = await SeedScenarioAsync();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            (await db.GridWorkers.FirstAsync(w => w.Id == s2.WorkerId)).Status = GridWorkerStatus.Draining;
            await db.SaveChangesAsync();
            return true;
        });
        var s2Claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s2.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.True(s2Claim is null || s2Claim.Worker.Id != s2.WorkerId);

        // Saturated worker: same skip semantics as drained.
        var s3 = await SeedScenarioAsync();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var worker = await db.GridWorkers.FirstAsync(w => w.Id == s3.WorkerId);
            worker.Capacity = 1;
            worker.ActiveAssignmentCount = 1;
            await db.SaveChangesAsync();
            return true;
        });
        var s3Claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s3.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.True(s3Claim is null || s3Claim.Worker.Id != s3.WorkerId);

        // App platform mismatch.
        var s4 = await SeedScenarioAsync();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var appId = Guid.NewGuid();
            db.MobileApps.Add(new MobileApp
            {
                Id = appId, ProjectId = _projectA, Platform = MobilePlatform.Ios,
                Name = "IOS", BundleId = "com.example.ios",
            });
            (await db.Executions.FirstAsync(e => e.Id == s4.ExecutionId)).MobileAppId = appId;
            await db.SaveChangesAsync();
            return true;
        });
        await Assert.ThrowsAsync<AutoTestAi.Application.Common.ConflictException>(() =>
            InScopeAsync(sp => sp.GetRequiredService<IGridScheduler>()
                .TryClaimMobileAsync(s4.ExecutionId, NoExclusions, CancellationToken.None)));

        // Non-mobile framework.
        var s5 = await SeedScenarioAsync();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            (await db.ExecutionTests.FirstAsync(t => t.Id == s5.TestId)).Framework = "playwright";
            await db.SaveChangesAsync();
            return true;
        });
        await Assert.ThrowsAsync<AutoTestAi.Application.Common.ConflictException>(() =>
            InScopeAsync(sp => sp.GetRequiredService<IGridScheduler>()
                .TryClaimMobileAsync(s5.ExecutionId, NoExclusions, CancellationToken.None)));
    }

    [Fact]
    public async Task StaleClaimToken_CannotMutateAfterReclaim()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();
        Guid staleToken = Guid.Empty;
        await InScopeAsync(async sp =>
        {
            var leases = sp.GetRequiredService<IMobileSlotLeaseService>();
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var slot = await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId);
            (_, staleToken) = await leases.PrepareClaimAsync(_projectA, slot.Id, s.WorkerId, CancellationToken.None);
            return true;
        });
        // Simulate a newer owner rebinding the row directly.
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var slot = await db.MobileDeviceSlots.FirstAsync(x => x.Id == s.SlotId);
            slot.ClaimToken = Guid.NewGuid();
            slot.Status = MobileSlotStatus.Active;
            await db.SaveChangesAsync();
            return true;
        });
        await Assert.ThrowsAsync<AutoTestAi.Application.Common.ConflictException>(() =>
            InScopeAsync(sp => sp.GetRequiredService<IMobileSlotLeaseService>()
                .ReleaseAsync(_projectA, s.SlotId, staleToken, CancellationToken.None)));
    }

    [Fact]
    public async Task AuditCarriesIdentifiersOnly()
    {
        await SeedOnceAsync();
        var s = await SeedScenarioAsync();
        var claim = await InScopeAsync(sp =>
            sp.GetRequiredService<IGridScheduler>().TryClaimMobileAsync(s.ExecutionId, NoExclusions, CancellationToken.None));
        Assert.NotNull(claim);
        await InScopeAsync(sp =>
            sp.GetRequiredService<IMobileSlotLeaseService>().ReapExpiredSlotsAsync(CancellationToken.None));
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AutoTestAiDbContext>();
            var events = await db.AuditEvents
                .Where(e => e.Action.StartsWith("mobile.slot", StringComparison.Ordinal))
                .ToListAsync();
            Assert.NotEmpty(events);
            foreach (var audit in events)
            {
                Assert.DoesNotContain(claim!.ClaimToken.ToString("D"), audit.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(claim.Assignment.AssignmentToken.ToString("D"), audit.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        });
    }
}
