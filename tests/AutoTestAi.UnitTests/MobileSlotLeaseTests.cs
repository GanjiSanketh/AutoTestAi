using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-3: slot lease validation, staging, standalone ops, reaper.
/// ClaimToken and AssignmentToken stay distinct and never reach audit.</summary>
public sealed class MobileSlotLeaseTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Pool = Guid.NewGuid();
    private static readonly Guid Device = Guid.NewGuid();
    private static readonly Guid Worker = Guid.NewGuid();

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<(string Action, string? Metadata)> Events = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Events.Add((action, metadataJson)); return Task.CompletedTask; }
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        private DateTimeOffset _now = now;
        public DateTimeOffset UtcNow => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    // NOTE: intentionally named to mimic the EF Core concurrency exception
    // FullName so the production type-name-based conflict mapping treats it
    // like the real thing. No EntityFrameworkCore reference needed.
    private sealed class DbUpdateConcurrencyException : Exception
    {
        public DbUpdateConcurrencyException() : base("simulated concurrency conflict") { }
    }

    private sealed class FakeSlots : IMobileRegistryStore
    {
        public readonly Dictionary<Guid, MobileDeviceSlot> Slots = new();
        public readonly Dictionary<Guid, MobileDevicePool> Pools = new();
        public bool FailNextSave { get; set; }
        public int SaveCalls { get; private set; }

        public Task<MobileDevicePool?> GetPoolByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Pools.TryGetValue(id, out var p) ? p : null);
        public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Slots.TryGetValue(id, out var s) ? s : null);
        public Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid assignmentId, CancellationToken ct)
            => Task.FromResult(Slots.Values.FirstOrDefault(s => s.AssignmentId == assignmentId));
        public Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(Slots.Values
                .Where(s => s.Status != MobileSlotStatus.Free && s.Status != MobileSlotStatus.Released &&
                            s.ClaimExpiresAt != null && s.ClaimExpiresAt <= now)
                .OrderBy(s => s.ClaimExpiresAt).Take(take).ToList());
        public Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(Slots.Values
                .Where(s => s.Status == MobileSlotStatus.Released)
                .OrderBy(s => s.UpdatedAt).Take(take).ToList());
        public Task<MobileDevicePool?> FindPoolByNameAsync(Guid p, string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDevice?> GetDeviceByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDevice?> FindDeviceByUdidAsync(Guid p, string u, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDevice>> ListDevicesAsync(Guid p, Guid? pool, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountDevicesInPoolAsync(Guid pool, CancellationToken ct) => throw new NotImplementedException();
        public Task AddDeviceAsync(MobileDevice d, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsByDeviceAsync(Guid d, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsForClaimAsync(Guid p, Guid pool, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task AddSlotAsync(MobileDeviceSlot s, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileApp?> GetAppByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAppAsync(MobileApp a, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct)
        {
            SaveCalls++;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new DbUpdateConcurrencyException();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAssignments : IGridAssignmentStore
    {
        public readonly Dictionary<Guid, GridAssignment> Rows = new();
        public Task<GridAssignment?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var a) ? a : null);
        public Task<GridAssignment?> FindActiveByTestAsync(Guid testId, CancellationToken ct) => throw new NotImplementedException();
        public Task<GridAssignment?> FindActiveByRefAsync(string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveByProjectAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountQueuedExecutionsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task AddAsync(GridAssignment a, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fixture
    {
        public readonly FakeSlots Slots = new();
        public readonly FakeAssignments Assignments = new();
        public readonly FakeAudit Audit = new();
        public readonly FixedClock Clock;
        public readonly MobileSlotLeaseService Leases;

        public Fixture()
        {
            Clock = new FixedClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
            Slots.Pools[Pool] = new MobileDevicePool
            {
                Id = Pool, ProjectId = Project, Name = "android-smoke",
                Platform = MobilePlatform.Android, Status = MobilePoolStatus.Active,
            };
            Leases = new MobileSlotLeaseService(Slots, Assignments, Clock,
                Options.Create(new MobileOptions()), Audit, NullLogger<MobileSlotLeaseService>.Instance);
        }

        public MobileDeviceSlot FreeSlot()
        {
            var slot = new MobileDeviceSlot
            {
                ProjectId = Project, PoolId = Pool, DeviceId = Device,
                SlotNumber = 1, Status = MobileSlotStatus.Free,
            };
            Slots.Slots[slot.Id] = slot;
            return slot;
        }
    }

    [Fact]
    public async Task PrepareClaim_StagesWithoutSaving()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        var (staged, token) = await f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None);
        Assert.Equal(MobileSlotStatus.Claimed, staged.Status);
        Assert.Equal(token, staged.ClaimToken);
        Assert.Equal(Worker, staged.WorkerId);
        Assert.NotNull(staged.ClaimExpiresAt);
        Assert.Null(staged.AssignmentId);
        Assert.Equal(0, f.Slots.SaveCalls);
    }

    [Fact]
    public async Task PrepareClaim_OwnedSlot_Conflicts()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        await f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.PrepareClaimAsync(Project, slot.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task PrepareClaim_ReleasedSlot_Conflicts()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        slot.Status = MobileSlotStatus.Released;
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareClaim_ExpiredWithDeadAssignment_Reclaims()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        var dead = new GridAssignment
        {
            ExecutionId = Guid.NewGuid(), ExecutionTestId = Guid.NewGuid(), WorkerId = Worker,
            Status = GridAssignmentStatus.Completed,
        };
        slot.Status = MobileSlotStatus.Active;
        slot.ClaimToken = Guid.NewGuid();
        slot.ClaimExpiresAt = f.Clock.UtcNow.AddMinutes(-1);
        slot.WorkerId = Worker;
        slot.AssignmentId = dead.Id;
        f.Assignments.Rows[dead.Id] = dead;

        var (staged, token) = await f.Leases.PrepareClaimAsync(Project, slot.Id, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(MobileSlotStatus.Claimed, staged.Status);
        Assert.Equal(token, staged.ClaimToken);
        Assert.Null(staged.AssignmentId);
    }

    [Fact]
    public async Task PrepareClaim_ExpiredWithLiveAssignment_Conflicts()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        var live = new GridAssignment
        {
            ExecutionId = Guid.NewGuid(), ExecutionTestId = Guid.NewGuid(), WorkerId = Worker,
            Status = GridAssignmentStatus.Running,
            ExpiresAt = f.Clock.UtcNow.AddMinutes(5),
        };
        slot.Status = MobileSlotStatus.Active;
        slot.ClaimToken = Guid.NewGuid();
        slot.ClaimExpiresAt = f.Clock.UtcNow.AddMinutes(-1);
        slot.AssignmentId = live.Id;
        f.Assignments.Rows[live.Id] = live;

        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.PrepareClaimAsync(Project, slot.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task BindAssignment_RequiresClaimed()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        Assert.Throws<ConflictException>(() => f.Leases.BindAssignment(slot, Guid.NewGuid()));
        var (_, _) = await f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None);
        var assignmentId = Guid.NewGuid();
        f.Leases.BindAssignment(slot, assignmentId);
        Assert.Equal(assignmentId, slot.AssignmentId);
    }

    [Fact]
    public async Task Activate_Renew_Release_TokenChecked()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        var (_, token) = await f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.ActivateAsync(Project, slot.Id, Guid.NewGuid(), CancellationToken.None));
        var assignmentId = Guid.NewGuid();
        f.Leases.BindAssignment(slot, assignmentId);
        await f.Leases.ActivateAsync(Project, slot.Id, token, CancellationToken.None);
        Assert.Equal(MobileSlotStatus.Active, slot.Status);

        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.RenewAsync(Project, slot.Id, Guid.NewGuid(), CancellationToken.None));
        var before = slot.ClaimExpiresAt;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Leases.RenewAsync(Project, slot.Id, token, CancellationToken.None);
        Assert.True(slot.ClaimExpiresAt > before);

        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.ReleaseAsync(Project, slot.Id, Guid.NewGuid(), CancellationToken.None));
        await f.Leases.ReleaseAsync(Project, slot.Id, token, CancellationToken.None);
        Assert.Equal(MobileSlotStatus.Released, slot.Status);
        Assert.Null(slot.ClaimToken);
        Assert.Equal(assignmentId, slot.AssignmentId);
    }

    [Fact]
    public async Task Renew_ExpiredLease_Conflicts()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        var (_, token) = await f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.RenewAsync(Project, slot.Id, token, CancellationToken.None));
    }

    [Fact]
    public async Task SaveConflict_MapsToConflictException()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        var (_, token) = await f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None);
        f.Slots.FailNextSave = true;
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Leases.ActivateAsync(Project, slot.Id, token, CancellationToken.None));
    }

    [Fact]
    public async Task Reaper_FreesExpiredWithDeadAssignment_KeepsLive()
    {
        var f = new Fixture();
        var dead = new GridAssignment
        {
            ExecutionId = Guid.NewGuid(), ExecutionTestId = Guid.NewGuid(), WorkerId = Worker,
            Status = GridAssignmentStatus.Expired,
        };
        var live = new GridAssignment
        {
            ExecutionId = Guid.NewGuid(), ExecutionTestId = Guid.NewGuid(), WorkerId = Worker,
            Status = GridAssignmentStatus.Running,
            ExpiresAt = f.Clock.UtcNow.AddMinutes(5),
        };
        f.Assignments.Rows[dead.Id] = dead;
        f.Assignments.Rows[live.Id] = live;

        var deadSlot = f.FreeSlot();
        deadSlot.Status = MobileSlotStatus.Active;
        deadSlot.ClaimToken = Guid.NewGuid();
        deadSlot.ClaimExpiresAt = f.Clock.UtcNow.AddMinutes(-1);
        deadSlot.WorkerId = Worker;
        deadSlot.AssignmentId = dead.Id;

        var liveSlot = f.FreeSlot();
        liveSlot.Status = MobileSlotStatus.Active;
        liveSlot.ClaimToken = Guid.NewGuid();
        liveSlot.ClaimExpiresAt = f.Clock.UtcNow.AddMinutes(-1);
        liveSlot.WorkerId = Worker;
        liveSlot.AssignmentId = live.Id;

        var reaped = await f.Leases.ReapExpiredSlotsAsync(CancellationToken.None);
        Assert.Equal(1, reaped);
        Assert.Equal(MobileSlotStatus.Free, deadSlot.Status);
        Assert.Null(deadSlot.AssignmentId);
        Assert.Equal(MobileSlotStatus.Active, liveSlot.Status);
        Assert.Contains(f.Audit.Events, e => e.Action == "mobile.slot_expired");
    }

    [Fact]
    public async Task Reaper_UnlinkedClaim_UsesGracePeriod()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        slot.Status = MobileSlotStatus.Claimed;
        slot.ClaimToken = Guid.NewGuid();
        slot.ClaimExpiresAt = f.Clock.UtcNow.AddMinutes(-1);
        slot.UpdatedAt = f.Clock.UtcNow;

        Assert.Equal(0, await f.Leases.ReapExpiredSlotsAsync(CancellationToken.None));
        Assert.Equal(MobileSlotStatus.Claimed, slot.Status);

        f.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await f.Leases.ReapExpiredSlotsAsync(CancellationToken.None));
        Assert.Equal(MobileSlotStatus.Free, slot.Status);
        Assert.Contains(f.Audit.Events, e => e.Action == "mobile.slot_recovered");
    }

    [Fact]
    public async Task AuditNeverCarriesTokens()
    {
        var f = new Fixture();
        var slot = f.FreeSlot();
        var (_, token) = await f.Leases.PrepareClaimAsync(Project, slot.Id, Worker, CancellationToken.None);
        f.Leases.BindAssignment(slot, Guid.NewGuid());
        await f.Leases.ActivateAsync(Project, slot.Id, token, CancellationToken.None);
        await f.Leases.RenewAsync(Project, slot.Id, token, CancellationToken.None);
        await f.Leases.ReleaseAsync(Project, slot.Id, token, CancellationToken.None);
        await f.Leases.ReapExpiredSlotsAsync(CancellationToken.None);
        Assert.NotEmpty(f.Audit.Events);
        foreach (var (_, meta) in f.Audit.Events)
            Assert.DoesNotContain(token.ToString("D"), meta ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MobileOptions_ClampBounds()
    {
        var options = new MobileOptions
        {
            LeaseDurationSeconds = 5,
            ReapBatchSize = 0,
            MaxClaimAttempts = 1000,
            ClaimGraceSeconds = 99999,
        };
        Assert.Equal(TimeSpan.FromSeconds(60), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(3600), options.ClaimGrace);
        Assert.Equal(300, new MobileOptions().LeaseDurationSeconds);
        Assert.Equal(100, new MobileOptions().ReapBatchSize);
        Assert.Equal(8, new MobileOptions().MaxClaimAttempts);
    }
}
