using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-4B-1: MobileDeviceSession ownership, fencing, and lifecycle.</summary>
public sealed class MobileSessionLifecycleTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private sealed class FakeRegistry : IMobileRegistryStore
    {
        public readonly Dictionary<Guid, MobileDeviceSlot> Slots = new();
        public readonly Dictionary<Guid, MobileDeviceSession> Sessions = new();
        public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Slots.TryGetValue(id, out var s) ? s : null);
        public Task<MobileDeviceSession?> GetSessionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Sessions.TryGetValue(id, out var s) ? s : null);
        public Task<MobileDeviceSession?> FindSessionByAssignmentAsync(Guid assignmentId, CancellationToken ct)
            => Task.FromResult(Sessions.Values.FirstOrDefault(s => s.AssignmentId == assignmentId));
        public Task<IReadOnlyList<MobileDeviceSession>> ListStaleSessionsAsync(DateTimeOffset staleBefore, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSession>>(Sessions.Values
                .Where(s => s.Status != MobileSessionStatus.Closed && s.Status != MobileSessionStatus.Orphaned &&
                            s.LastHeartbeatAt != null && s.LastHeartbeatAt <= staleBefore)
                .OrderBy(s => s.LastHeartbeatAt).Take(take).ToList());
        public Task AddSessionAsync(MobileDeviceSession session, CancellationToken ct) { Sessions[session.Id] = session; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MobileDevicePool?> GetPoolByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
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
        public Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Slots.Values.FirstOrDefault(s => s.AssignmentId == id));
        public Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct) => throw new NotImplementedException();
        public Task AddSlotAsync(MobileDeviceSlot s, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileApp?> GetAppByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAppAsync(MobileApp a, CancellationToken ct) => throw new NotImplementedException();
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

    private sealed record Harness(FakeRegistry Registry, FakeAssignments Assignments, MobileSessionService Service);

    private static Harness Create()
    {
        var registry = new FakeRegistry();
        var assignments = new FakeAssignments();
        var service = new MobileSessionService(
            registry, assignments, new SystemDateTimeProvider(), NullLogger<MobileSessionService>.Instance);
        return new Harness(registry, assignments, service);
    }

    private static (Guid slotId, Guid deviceId, Guid executionId, Guid assignmentId, Guid token) SeedClaim(
        Harness h, Guid project = default)
    {
        project = project == default ? ProjectA : project;
        var slotId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        var token = Guid.NewGuid();
        h.Registry.Slots[slotId] = new MobileDeviceSlot
        {
            Id = slotId, ProjectId = project, PoolId = Guid.NewGuid(), DeviceId = deviceId,
            SlotNumber = 1, Status = MobileSlotStatus.Claimed, AssignmentId = assignmentId,
        };
        h.Assignments.Rows[assignmentId] = new GridAssignment
        {
            Id = assignmentId, ExecutionId = executionId, ExecutionTestId = Guid.NewGuid(),
            WorkerId = Guid.NewGuid(), Status = GridAssignmentStatus.Claimed,
            AssignmentToken = token,
        };
        return (slotId, deviceId, executionId, assignmentId, token);
    }

    [Fact]
    public async Task Create_Then_Activate_Then_Close()
    {
        var h = Create();
        var (slotId, deviceId, executionId, assignmentId, token) = SeedClaim(h);
        var session = await h.Service.CreateAsync(ProjectA, deviceId, slotId, executionId, assignmentId, CancellationToken.None);
        Assert.Equal(MobileSessionStatus.Creating, session.Status);

        var active = await h.Service.ActivateAsync(ProjectA, session.Id, assignmentId, token, "appium-session-1", CancellationToken.None);
        Assert.Equal(MobileSessionStatus.Active, active.Status);
        Assert.Equal("appium-session-1", active.AppiumSessionId);
        Assert.NotNull(active.StartedAt);
        Assert.NotNull(active.LastHeartbeatAt);

        await h.Service.CloseAsync(ProjectA, session.Id, assignmentId, token, CancellationToken.None);
        Assert.Equal(MobileSessionStatus.Closed, h.Registry.Sessions[session.Id].Status);
        Assert.NotNull(h.Registry.Sessions[session.Id].ClosedAt);

        // Idempotent close.
        await h.Service.CloseAsync(ProjectA, session.Id, assignmentId, token, CancellationToken.None);
        Assert.Equal(MobileSessionStatus.Closed, h.Registry.Sessions[session.Id].Status);
    }

    [Fact]
    public async Task StaleToken_CannotActivate()
    {
        var h = Create();
        var (slotId, deviceId, executionId, assignmentId, _) = SeedClaim(h);
        var session = await h.Service.CreateAsync(ProjectA, deviceId, slotId, executionId, assignmentId, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() =>
            h.Service.ActivateAsync(ProjectA, session.Id, assignmentId, Guid.NewGuid(), "s-1", CancellationToken.None));
        Assert.Equal(MobileSessionStatus.Creating, h.Registry.Sessions[session.Id].Status);
    }

    [Fact]
    public async Task StaleAssignment_CannotCloseNewerSession()
    {
        var h = Create();
        var (slotId, deviceId, executionId, assignmentId, token) = SeedClaim(h);
        var session = await h.Service.CreateAsync(ProjectA, deviceId, slotId, executionId, assignmentId, CancellationToken.None);
        await h.Service.ActivateAsync(ProjectA, session.Id, assignmentId, token, "s-1", CancellationToken.None);

        // Slot reassigned to a newer assignment: old token/assignment is stale.
        var newerAssignment = Guid.NewGuid();
        h.Registry.Slots[slotId].AssignmentId = newerAssignment;
        await Assert.ThrowsAsync<ConflictException>(() =>
            h.Service.CloseAsync(ProjectA, session.Id, assignmentId, token, CancellationToken.None));
        Assert.NotEqual(MobileSessionStatus.Closed, h.Registry.Sessions[session.Id].Status);
    }

    [Fact]
    public async Task CrossProject_Rejected()
    {
        var h = Create();
        var (slotId, deviceId, executionId, assignmentId, token) = SeedClaim(h);
        var session = await h.Service.CreateAsync(ProjectA, deviceId, slotId, executionId, assignmentId, CancellationToken.None);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Service.HeartbeatAsync(ProjectB, session.Id, assignmentId, token, CancellationToken.None));
    }

    [Fact]
    public async Task Heartbeat_UpdatesTimestamp_And_RejectsClosed()
    {
        var h = Create();
        var (slotId, deviceId, executionId, assignmentId, token) = SeedClaim(h);
        var session = await h.Service.CreateAsync(ProjectA, deviceId, slotId, executionId, assignmentId, CancellationToken.None);
        var before = h.Registry.Sessions[session.Id].LastHeartbeatAt;
        await Task.Delay(5);
        await h.Service.HeartbeatAsync(ProjectA, session.Id, assignmentId, token, CancellationToken.None);
        Assert.True(h.Registry.Sessions[session.Id].LastHeartbeatAt >= before);
        await h.Service.CloseAsync(ProjectA, session.Id, assignmentId, token, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() =>
            h.Service.HeartbeatAsync(ProjectA, session.Id, assignmentId, token, CancellationToken.None));
    }

    [Fact]
    public async Task Reap_OrphansOnlyUnownedStale()
    {
        var h = Create();
        var (slotId, deviceId, executionId, assignmentId, token) = SeedClaim(h);
        var owned = await h.Service.CreateAsync(ProjectA, deviceId, slotId, executionId, assignmentId, CancellationToken.None);
        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(5);

        // Still owned: reaper leaves it alone.
        Assert.Equal(0, await h.Service.ReapStaleAsync(staleBefore, 10, CancellationToken.None));
        Assert.Equal(MobileSessionStatus.Creating, h.Registry.Sessions[owned.Id].Status);

        // Ownership lost: reaper orphans.
        h.Registry.Slots[slotId].AssignmentId = Guid.NewGuid();
        Assert.Equal(1, await h.Service.ReapStaleAsync(staleBefore, 10, CancellationToken.None));
        Assert.Equal(MobileSessionStatus.Orphaned, h.Registry.Sessions[owned.Id].Status);
    }

    [Fact]
    public async Task Session_BelongsToCorrectExecution_And_Slot()
    {
        var h = Create();
        var (slotId, deviceId, executionId, assignmentId, _) = SeedClaim(h);
        var session = await h.Service.CreateAsync(ProjectA, deviceId, slotId, executionId, assignmentId, CancellationToken.None);
        Assert.Equal(executionId, session.ExecutionId);
        Assert.Equal(slotId, session.DeviceSlotId);
        Assert.Equal(assignmentId, session.AssignmentId);
    }
}
