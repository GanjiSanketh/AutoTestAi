using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-3: deterministic mobile scheduling, atomic ownership,
/// scheduler races, renewal/release/reap piggybacks, registration allowlist.</summary>
public sealed class MobileSchedulerTests
{
    private static readonly Guid Project = Guid.NewGuid();

    // Exception doubles whose FullNames satisfy the production type-name
    // conflict mappings without an EF reference.
    private sealed class FakeDbUpdateConcurrencyException : Exception
    {
        public FakeDbUpdateConcurrencyException() : base("simulated concurrency conflict") { }
    }

    private sealed class FakeDbUpdateException : Exception
    {
        public FakeDbUpdateException() : base("duplicate key violates unique constraint") { }
    }

    private static T Clone<T>(T row) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(row))!;

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<(string Action, string? Metadata)> Events = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Events.Add((action, metadataJson)); return Task.CompletedTask; }
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>Version-emulating shared fake database. All fake stores read
    /// and write through these dictionaries, and ANY store SaveChanges call
    /// commits every tracked change at once — mirroring the shared scoped
    /// EF DbContext that makes the scheduler ownership transaction atomic.
    /// Each store translates failures exactly like its production EF
    /// counterpart (assignment/worker stores map to ConflictException;
    /// mobile/execution stores pass through raw).</summary>
    private sealed class FakeDb
    {
        public readonly Dictionary<Guid, GridWorker> Workers = new();
        public readonly Dictionary<Guid, uint> WorkerVersions = new();
        public readonly Dictionary<Guid, GridAssignment> Assignments = new();
        public readonly Dictionary<Guid, MobileDeviceSlot> Slots = new();
        public readonly Dictionary<Guid, byte[]?> SlotVersions = new();
        public readonly Dictionary<Guid, Execution> Executions = new();
        public readonly Dictionary<Guid, ExecutionTest> Tests = new();
        public readonly Dictionary<Guid, MobileDevicePool> Pools = new();
        public readonly Dictionary<Guid, MobileDevice> Devices = new();
        public readonly Dictionary<Guid, MobileApp> Apps = new();
        public int SaveCalls;
    }

    /// <summary>One reader's tracked changes. ANY store SaveChanges commits
    /// the whole unit at once — mirroring the shared scoped EF DbContext
    /// that makes the scheduler ownership transaction atomic.</summary>
    private sealed class FakeUnitOfWork
    {
        private readonly FakeDb _db;
        public readonly Dictionary<Guid, GridWorker> Workers = new();
        public readonly Dictionary<Guid, uint> OriginalWorkerVersions = new();
        public readonly Dictionary<Guid, MobileDeviceSlot> Slots = new();
        public readonly Dictionary<Guid, byte[]?> OriginalSlotVersions = new();
        public readonly Dictionary<Guid, Execution> Executions = new();
        public readonly Dictionary<Guid, ExecutionTest> Tests = new();
        public readonly List<GridAssignment> PendingAssignments = new();
        public readonly Dictionary<Guid, GridAssignment> Assignments = new();

        public FakeUnitOfWork(FakeDb db) => _db = db;

        public void Commit()
        {
            // Validate original versions before committing anything.
            foreach (var (id, clone) in Workers)
            {
                var original = OriginalWorkerVersions.TryGetValue(id, out var v) ? v : 0;
                if (!_db.WorkerVersions.TryGetValue(id, out var committed) || committed != original)
                    throw new FakeDbUpdateConcurrencyException();
            }
            foreach (var (id, clone) in Slots)
            {
                var original = OriginalSlotVersions.TryGetValue(id, out var v) ? v : null;
                var committed = _db.SlotVersions.TryGetValue(id, out var c) ? c : null;
                if (!NullableEquals(committed, original))
                    throw new FakeDbUpdateConcurrencyException();
            }
            foreach (var assignment in PendingAssignments)
            {
                if (_db.Assignments.Values.Any(a =>
                        a.ExecutionTestId == assignment.ExecutionTestId &&
                        (a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running)) ||
                    PendingAssignments.Count(a =>
                        a.ExecutionTestId == assignment.ExecutionTestId &&
                        (a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running)) > 1)
                    throw new FakeDbUpdateException();
            }
            foreach (var (id, clone) in Workers)
            {
                _db.Workers[id] = Clone(clone);
                _db.WorkerVersions[id] = clone.RowVersion + 1;
                clone.RowVersion++;
            }
            foreach (var (id, clone) in Slots)
            {
                _db.Slots[id] = Clone(clone);
                var next = clone.RowVersion is null ? new byte[] { 1 } : Increment(clone.RowVersion);
                _db.SlotVersions[id] = next;
                clone.RowVersion = (byte[])next.Clone();
            }
            foreach (var assignment in PendingAssignments)
                _db.Assignments[assignment.Id] = Clone(assignment);
            PendingAssignments.Clear();
            foreach (var (id, a) in Assignments) _db.Assignments[id] = Clone(a);
            Assignments.Clear();
            foreach (var (id, e) in Executions) _db.Executions[id] = Clone(e);
            foreach (var (id, t) in Tests) _db.Tests[id] = Clone(t);
            Workers.Clear();
            OriginalWorkerVersions.Clear();
            Slots.Clear();
            OriginalSlotVersions.Clear();
            Executions.Clear();
            Tests.Clear();
            _db.SaveCalls++;
        }

        private static bool NullableEquals(byte[]? a, byte[]? b)
            => (a is null && b is null) || (a is not null && b is not null && a.SequenceEqual(b));

        private static byte[] Increment(byte[] v)
        {
            var next = (byte[])v.Clone();
            next[^1]++;
            return next;
        }
    }

    private sealed class FakeWorkerStore : IGridWorkerStore
    {
        private readonly FakeDb _db;
        private readonly FakeUnitOfWork _uow;
        public FakeWorkerStore(FakeDb db, FakeUnitOfWork uow) { _db = db; _uow = uow; }
        public Task<GridWorker?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            if (!_db.Workers.TryGetValue(id, out var row)) return Task.FromResult<GridWorker?>(null);
            var clone = Clone(row);
            _uow.Workers[id] = clone;
            _uow.OriginalWorkerVersions[id] = _db.WorkerVersions.TryGetValue(id, out var v) ? v : 0;
            return Task.FromResult<GridWorker?>(clone);
        }
        public Task<GridWorker?> FindByKeyAsync(string key, CancellationToken ct)
            => Task.FromResult(_db.Workers.Values.FirstOrDefault(w => w.WorkerKey == key));
        public Task<IReadOnlyList<GridWorker>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GridWorker>>(_db.Workers.Values.Select(Clone).ToList());
        public Task AddAsync(GridWorker worker, CancellationToken ct)
        {
            _db.Workers[worker.Id] = Clone(worker);
            _db.WorkerVersions[worker.Id] = worker.RowVersion;
            return Task.CompletedTask;
        }
        public Task SaveChangesAsync(CancellationToken ct)
        {
            try
            {
                _uow.Commit();
            }
            catch (FakeDbUpdateConcurrencyException ex)
            {
                throw new ConflictException("A grid worker changed concurrently; retry the operation.");
            }
            catch (FakeDbUpdateException ex)
            {
                throw new ConflictException("A grid worker changed concurrently; retry the operation.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAssignmentStore : IGridAssignmentStore
    {
        private readonly FakeDb _db;
        private readonly FakeUnitOfWork _uow;
        public FakeAssignmentStore(FakeDb db, FakeUnitOfWork uow) { _db = db; _uow = uow; }
        public Task<GridAssignment?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            if (!_db.Assignments.TryGetValue(id, out var row)) return Task.FromResult<GridAssignment?>(null);
            var clone = Clone(row);
            _uow.Assignments[id] = clone;
            return Task.FromResult<GridAssignment?>(clone);
        }
        public Task<GridAssignment?> FindActiveByTestAsync(Guid testId, CancellationToken ct)
            => Task.FromResult(_db.Assignments.Values
                .Where(a => a.ExecutionTestId == testId &&
                            (a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running))
                .Select(Clone).FirstOrDefault());
        public Task<GridAssignment?> FindActiveByRefAsync(string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct)
        {
            var clones = _db.Assignments.Values
                .Where(a => (a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running) && a.ExpiresAt < now)
                .OrderBy(a => a.ExpiresAt).Take(take).Select(Clone).ToList();
            foreach (var clone in clones) _uow.Assignments[clone.Id] = clone;
            return Task.FromResult<IReadOnlyList<GridAssignment>>(clones);
        }
        public Task<int> CountActiveAsync(CancellationToken ct)
            => Task.FromResult(_db.Assignments.Values.Count(a =>
                a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running));
        public Task<int> CountActiveByProjectAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult(_db.Assignments.Values.Count(a =>
                (a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running) &&
                _db.Executions.TryGetValue(a.ExecutionId, out var e) && e.ProjectId == projectId));
        public Task<int> CountQueuedExecutionsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task AddAsync(GridAssignment assignment, CancellationToken ct)
        {
            _uow.PendingAssignments.Add(assignment);
            return Task.CompletedTask;
        }
        public Task SaveChangesAsync(CancellationToken ct)
        {
            try
            {
                _uow.Commit();
            }
            catch (FakeDbUpdateConcurrencyException ex)
            {
                throw new ConflictException("Scheduling conflict; retry the operation.");
            }
            catch (FakeDbUpdateException ex)
            {
                throw new ConflictException("Scheduling conflict; retry the operation.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExecutionStore : IExecutionStore
    {
        private readonly FakeDb _db;
        private readonly FakeUnitOfWork _uow;
        public FakeExecutionStore(FakeDb db, FakeUnitOfWork uow) { _db = db; _uow = uow; }
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
        {
            if (!_db.Executions.TryGetValue(id, out var e)) return Task.FromResult<Execution?>(null);
            var clone = Clone(e);
            _uow.Executions[id] = clone;
            return Task.FromResult<Execution?>(clone);
        }
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
        {
            var clones = _db.Tests.Values
                .Where(t => t.ExecutionId == id).OrderBy(t => t.CreatedAt).Select(Clone).ToList();
            foreach (var clone in clones) _uow.Tests[clone.Id] = clone;
            return Task.FromResult<IReadOnlyList<ExecutionTest>>(clones);
        }
        public Task SaveChangesAsync(CancellationToken ct)
        {
            _uow.Commit();
            return Task.CompletedTask;
        }
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct)
        {
            if (!_db.Tests.TryGetValue(id, out var t)) return Task.FromResult<ExecutionTest?>(null);
            var clone = Clone(t);
            _uow.Tests[clone.Id] = clone;
            return Task.FromResult<ExecutionTest?>(clone);
        }
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionAsync(Execution e, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid t, CancellationToken ct) => throw new NotImplementedException();
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> r, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteStepResultsAsync(Guid t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid t, long? a, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> l, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteLogsAsync(Guid t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid t, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteArtifactsAsync(Guid t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Domain.Entities.FailureAnalysis>> ListAnalysesAsync(Guid t, CancellationToken ct) => throw new NotImplementedException();
        public Task<Domain.Entities.FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAnalysisAsync(Domain.Entities.FailureAnalysis a, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeMobileStore : IMobileRegistryStore
    {
        private readonly FakeDb _db;
        private readonly FakeUnitOfWork _uow;
        public FakeMobileStore(FakeDb db, FakeUnitOfWork uow) { _db = db; _uow = uow; }
        public Task<MobileDevicePool?> GetPoolByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_db.Pools.TryGetValue(id, out var p) ? Clone(p) : null);
        public Task<MobileDevice?> GetDeviceByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_db.Devices.TryGetValue(id, out var d) ? Clone(d) : null);
        public Task<MobileApp?> GetAppByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_db.Apps.TryGetValue(id, out var a) ? Clone(a) : null);
        public Task<IReadOnlyList<MobileDevice>> ListDevicesAsync(Guid projectId, Guid? poolId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDevice>>(_db.Devices.Values
                .Where(d => d.ProjectId == projectId && (poolId == null || d.PoolId == poolId))
                .Select(Clone).ToList());
        public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid id, CancellationToken ct)
        {
            if (!_db.Slots.TryGetValue(id, out var row)) return Task.FromResult<MobileDeviceSlot?>(null);
            var clone = Clone(row);
            _uow.Slots[id] = clone;
            _uow.OriginalSlotVersions[id] = _db.SlotVersions.TryGetValue(id, out var v) ? v?.ToArray() : null;
            return Task.FromResult<MobileDeviceSlot?>(clone);
        }
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsForClaimAsync(Guid projectId, Guid poolId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(_db.Slots.Values
                .Where(s => s.ProjectId == projectId && s.PoolId == poolId)
                .OrderBy(s => s.SlotNumber).ThenBy(s => s.Id).Take(take).Select(Clone).ToList());
        public Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid assignmentId, CancellationToken ct)
        {
            var row = _db.Slots.Values.FirstOrDefault(s => s.AssignmentId == assignmentId);
            if (row is null) return Task.FromResult<MobileDeviceSlot?>(null);
            var clone = Clone(row);
            _uow.Slots[clone.Id] = clone;
            _uow.OriginalSlotVersions[clone.Id] = _db.SlotVersions.TryGetValue(clone.Id, out var v) ? v?.ToArray() : null;
            return Task.FromResult<MobileDeviceSlot?>(clone);
        }
        public Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(_db.Slots.Values
                .Where(s => s.Status != MobileSlotStatus.Free && s.Status != MobileSlotStatus.Released &&
                            s.ClaimExpiresAt != null && s.ClaimExpiresAt <= now)
                .Select(Clone).ToList());
        public Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(_db.Slots.Values
                .Where(s => s.Status == MobileSlotStatus.Released).Take(take).Select(Clone).ToList());
        public Task<MobileDevicePool?> FindPoolByNameAsync(Guid p, string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDevice?> FindDeviceByUdidAsync(Guid p, string u, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountDevicesInPoolAsync(Guid pool, CancellationToken ct) => throw new NotImplementedException();
        public Task AddDeviceAsync(MobileDevice d, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsByDeviceAsync(Guid d, CancellationToken ct) => throw new NotImplementedException();
        public Task AddSlotAsync(MobileDeviceSlot s, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAppAsync(MobileApp a, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct)
        {
            // Plain passthrough like EfMobileRegistryStore: commits the whole
            // shared unit (same DbContext in production).
            _uow.Commit();
            return Task.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public readonly FakeDb Db;
        public readonly FakeAudit Audit = new();
        public readonly FixedClock Clock;
        public readonly GridScheduler Scheduler;
        public readonly FakeExecutionStore Executions;
        public GridLeaseManager Leases = null!;

        public Harness() : this(new FakeDb()) { }

        public Harness(FakeDb db)
        {
            Db = db;
            Clock = new FixedClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
            var uow = new FakeUnitOfWork(db);
            var workers = new FakeWorkerStore(Db, uow);
            var assignments = new FakeAssignmentStore(Db, uow);
            Executions = new FakeExecutionStore(Db, uow);
            var mobile = new FakeMobileStore(Db, uow);
            var leases = new MobileSlotLeaseService(mobile, assignments, Clock,
                Options.Create(new MobileOptions()), Audit, NullLogger<MobileSlotLeaseService>.Instance);
            Scheduler = new GridScheduler(workers, assignments, Executions, leases, mobile, Clock,
                Audit, Options.Create(new GridOptions()), Options.Create(new MobileOptions()),
                NullLogger<GridScheduler>.Instance);
            Leases = new GridLeaseManager(assignments, workers, Executions, leases,
                NullLogger<GridLeaseManager>.Instance);
        }
    }

    private static Guid SeedScenario(Harness h, string framework = "appium", bool withApp = false)
        => SeedScenario(h.Db, h.Clock.UtcNow, framework, withApp);

    private static Guid SeedScenario(FakeDb db, DateTimeOffset now, string framework = "appium", bool withApp = false)
    {
        var poolId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var testId = Guid.NewGuid();
        db.Pools[poolId] = new MobileDevicePool
        {
            Id = poolId, ProjectId = Project, Name = "android-smoke",
            Platform = MobilePlatform.Android, Status = MobilePoolStatus.Active,
        };
        db.Devices[deviceId] = new MobileDevice
        {
            Id = deviceId, ProjectId = Project, PoolId = poolId,
            Platform = MobilePlatform.Android, AutomationName = MobileAutomationNames.UiAutomator2,
            Status = MobileDeviceStatus.Available,
        };
        db.Slots[slotId] = new MobileDeviceSlot
        {
            Id = slotId, ProjectId = Project, PoolId = poolId, DeviceId = deviceId,
            SlotNumber = 1, Status = MobileSlotStatus.Free,
        };
        db.Workers[workerId] = new GridWorker
        {
            Id = workerId, WorkerKey = "appium-1", WorkerType = "appium", Framework = "appium",
            Browsers = new List<string>(), Status = GridWorkerStatus.Available,
            Capacity = 4, LastHeartbeatAt = now,
        };
        db.WorkerVersions[workerId] = 0;
        var appId = Guid.Empty;
        if (withApp)
        {
            appId = Guid.NewGuid();
            db.Apps[appId] = new MobileApp
            {
                Id = appId, ProjectId = Project, Platform = MobilePlatform.Android,
                Name = "Shop", PackageId = "com.example.shop",
            };
        }
        db.Executions[executionId] = new Execution
        {
            Id = executionId, ProjectId = Project, Status = ExecutionStatus.Queued,
            MobileDevicePoolId = poolId, MobileAppId = withApp ? appId : null,
        };
        db.Tests[testId] = new ExecutionTest
        {
            Id = testId, ExecutionId = executionId, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Queued, Framework = framework,
        };
        return executionId;
    }

    private static ExecutionTest? FirstTest(Harness h, Guid executionId)
        => h.Db.Tests.Values.FirstOrDefault(t => t.ExecutionId == executionId);

    private static readonly IReadOnlySet<Guid> NoExclusions =
        new HashSet<Guid>();

    [Fact]
    public async Task NonMobileFramework_Conflicts()
    {
        var h = new Harness();
        var executionId = SeedScenario(h, framework: "playwright");
        await Assert.ThrowsAsync<ConflictException>(() =>
            h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None));
    }

    [Fact]
    public async Task MissingPool_Conflicts()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        h.Db.Executions[executionId].MobileDevicePoolId = Guid.NewGuid();
        await Assert.ThrowsAsync<ConflictException>(() =>
            h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None));
        Assert.Empty(h.Db.Assignments);
    }

    [Fact]
    public async Task NoMobileWorkers_StaysQueued()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        h.Db.Workers.Clear();
        var before = h.Db.SaveCalls;
        Assert.Null(await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None));
        Assert.Equal(before, h.Db.SaveCalls);
        Assert.Empty(h.Db.Assignments);
    }

    [Fact]
    public async Task HappyPath_CommitsAtomicallyInOneSave()
    {
        var h = new Harness();
        var executionId = SeedScenario(h, withApp: true);
        var savesBefore = h.Db.SaveCalls;

        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);

        Assert.NotNull(claim);
        // Atomicity proof: the entire ownership unit (worker capacity, slot
        // claim, assignment insert, slot/test binding) committed in exactly
        // one persistence call — no split commits possible.
        Assert.Equal(savesBefore + 1, h.Db.SaveCalls);

        var assignment = Assert.Single(h.Db.Assignments.Values);
        Assert.Equal(GridAssignmentStatus.Claimed, assignment.Status);
        Assert.Equal(claim!.Assignment.Id, assignment.Id);

        var slot = Assert.Single(h.Db.Slots.Values);
        Assert.Equal(MobileSlotStatus.Claimed, slot.Status);
        Assert.Equal(assignment.Id, slot.AssignmentId);
        Assert.Equal(claim.Worker.Id, slot.WorkerId);
        Assert.Equal(claim.ClaimToken, slot.ClaimToken);
        Assert.NotNull(slot.ClaimExpiresAt);

        var worker = h.Db.Workers[claim.Worker.Id];
        Assert.Equal(1, worker.ActiveAssignmentCount);

        var test = FirstTest(h, executionId);
        Assert.NotNull(test);
        Assert.Equal(assignment.Id, test!.AssignmentId);
        Assert.Equal(assignment.AssignmentToken, test.AssignmentToken);

        Assert.NotEqual(assignment.AssignmentToken, claim.ClaimToken);
        Assert.Contains(h.Audit.Events, e => e.Action == "mobile.slot_claimed");
    }

    [Fact]
    public async Task OwnedSlot_TriesNextCandidate()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var firstSlot = Assert.Single(h.Db.Slots.Values);
        firstSlot.Status = MobileSlotStatus.Claimed;
        firstSlot.ClaimToken = Guid.NewGuid();
        firstSlot.ClaimExpiresAt = h.Clock.UtcNow.AddMinutes(5);
        firstSlot.WorkerId = Guid.NewGuid();
        h.Db.SlotVersions[firstSlot.Id] = new byte[] { 1 };

        var secondSlot = new MobileDeviceSlot
        {
            Id = Guid.NewGuid(), ProjectId = Project, PoolId = firstSlot.PoolId,
            DeviceId = firstSlot.DeviceId, SlotNumber = 2, Status = MobileSlotStatus.Free,
        };
        h.Db.Slots[secondSlot.Id] = secondSlot;

        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(secondSlot.Id, claim!.Slot.Id);
    }

    [Fact]
    public async Task IneligiblePairs_AreSkipped()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        // Draining worker + disabled device + platform-mismatched device.
        var worker = Assert.Single(h.Db.Workers.Values);
        worker.Status = GridWorkerStatus.Draining;
        var device = Assert.Single(h.Db.Devices.Values);
        device.Status = MobileDeviceStatus.Disabled;
        Assert.Null(await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None));
        Assert.Empty(h.Db.Assignments);
    }

    [Fact]
    public async Task DeterministicOrdering_LeastLoadedWorker_LowestSlot()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var poolId = h.Db.Executions[executionId].MobileDevicePoolId!.Value;
        var deviceId = Assert.Single(h.Db.Devices.Values).Id;

        var busyWorker = Assert.Single(h.Db.Workers.Values);
        busyWorker.WorkerKey = "appium-b";
        busyWorker.ActiveAssignmentCount = 2;
        var idleWorker = new GridWorker
        {
            Id = Guid.NewGuid(), WorkerKey = "appium-a", WorkerType = "appium", Framework = "appium",
            Browsers = new List<string>(), Status = GridWorkerStatus.Available,
            Capacity = 4, LastHeartbeatAt = h.Clock.UtcNow,
        };
        h.Db.Workers[idleWorker.Id] = idleWorker;
        h.Db.WorkerVersions[idleWorker.Id] = 0;

        var highSlot = new MobileDeviceSlot
        {
            Id = Guid.NewGuid(), ProjectId = Project, PoolId = poolId, DeviceId = deviceId,
            SlotNumber = 9, Status = MobileSlotStatus.Free,
        };
        var lowSlot = new MobileDeviceSlot
        {
            Id = Guid.NewGuid(), ProjectId = Project, PoolId = poolId, DeviceId = deviceId,
            SlotNumber = 2, Status = MobileSlotStatus.Free,
        };
        h.Db.Slots[highSlot.Id] = highSlot;
        h.Db.Slots[lowSlot.Id] = lowSlot;

        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(idleWorker.Id, claim!.Worker.Id);
        // Slot 1 (seeded) wins over 2 and 9 by SlotNumber ordering.
        Assert.Equal(1, claim.Slot.SlotNumber);
    }

    [Fact]
    public async Task DrainedAndSaturatedWorkers_NeverSelected()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var healthyId = h.Db.Workers.Keys.Single();
        var drained = new GridWorker
        {
            Id = Guid.NewGuid(), WorkerKey = "appium-0-drained", WorkerType = "appium", Framework = "appium",
            Browsers = new List<string>(), Status = GridWorkerStatus.Draining,
            Capacity = 4, LastHeartbeatAt = h.Clock.UtcNow,
        };
        var saturated = new GridWorker
        {
            Id = Guid.NewGuid(), WorkerKey = "appium-0-full", WorkerType = "appium", Framework = "appium",
            Browsers = new List<string>(), Status = GridWorkerStatus.Available,
            Capacity = 1, ActiveAssignmentCount = 1, LastHeartbeatAt = h.Clock.UtcNow,
        };
        h.Db.Workers[drained.Id] = drained;
        h.Db.WorkerVersions[drained.Id] = 0;
        h.Db.Workers[saturated.Id] = saturated;
        h.Db.WorkerVersions[saturated.Id] = 0;

        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(healthyId, claim!.Worker.Id);
    }

    [Fact]
    public async Task TwoSchedulers_OnSameSlot_ExactlyOneWins()
    {
        var db = new FakeDb();
        SeedScenario(db, new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var executionId = db.Executions.Keys.Single();
        var h1 = new Harness(db);
        var h2 = new Harness(db);

        var results = await Task.WhenAll(
            h1.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None),
            h2.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None));

        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Single(db.Assignments.Values);
        var winner = results.Single(r => r is not null)!;
        var slot = Assert.Single(db.Slots.Values);
        Assert.Equal(winner.Assignment.Id, slot.AssignmentId);
        Assert.Equal(winner.Worker.Id, slot.WorkerId);
        var worker = db.Workers[winner.Worker.Id];
        Assert.Equal(1, worker.ActiveAssignmentCount);
    }

    [Fact]
    public async Task SlotVersionRace_LoserSeesConflict()
    {
        var db = new FakeDb();
        SeedScenario(db, new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var slotId = db.Slots.Keys.Single();

        // Both readers load the same version before either writes (genuine
        // race emulation: shared committed state, independent tracked clones).
        var mobile1 = new FakeMobileStore(db, new FakeUnitOfWork(db));
        var mobile2 = new FakeMobileStore(db, new FakeUnitOfWork(db));
        var first = (await mobile1.GetSlotByIdAsync(slotId, CancellationToken.None))!;
        var second = (await mobile2.GetSlotByIdAsync(slotId, CancellationToken.None))!;
        first.Status = MobileSlotStatus.Claimed;
        second.Status = MobileSlotStatus.Claimed;
        await mobile1.SaveChangesAsync(CancellationToken.None);
        await Assert.ThrowsAsync<FakeDbUpdateConcurrencyException>(() =>
            mobile2.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Renew_PiggybacksSlotExpiry()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);
        var savesBefore = h.Db.SaveCalls;

        var slotBefore = h.Db.Slots[claim!.Slot.Id].ClaimExpiresAt;
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
        await h.Scheduler.RenewLeaseAsync(claim.Assignment.Id, CancellationToken.None);

        Assert.Equal(savesBefore + 1, h.Db.SaveCalls);
        Assert.True(h.Db.Slots[claim.Slot.Id].ClaimExpiresAt > slotBefore);
    }

    [Fact]
    public async Task Release_ReleasesSlotAtomically()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);
        var test = FirstTest(h, executionId)!;

        await h.Scheduler.ReleaseLeaseAsync(test.Id, "Completed", CancellationToken.None);

        var slot = h.Db.Slots[claim!.Slot.Id];
        Assert.Equal(MobileSlotStatus.Released, slot.Status);
        Assert.Null(slot.ClaimToken);
        Assert.Equal(claim.Assignment.Id, slot.AssignmentId);
        Assert.Equal(0, h.Db.Workers[claim.Worker.Id].ActiveAssignmentCount);
    }

    [Fact]
    public async Task Reap_ExpiresAssignmentAndSlotTogether()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);

        // Simulate worker loss: stale heartbeat beyond the offline threshold.
        var worker = h.Db.Workers[claim!.Worker.Id];
        worker.LastHeartbeatAt = h.Clock.UtcNow.AddMinutes(-30);
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(10);

        var reaped = await h.Scheduler.ReapExpiredLeasesAsync(CancellationToken.None);
        Assert.Equal(1, reaped);
        Assert.Equal(GridAssignmentStatus.Expired, h.Db.Assignments[claim.Assignment.Id].Status);
        var slot = h.Db.Slots[claim.Slot.Id];
        Assert.Equal(MobileSlotStatus.Expired, slot.Status);
        Assert.Equal(claim.Assignment.Id, slot.AssignmentId);
    }

    [Fact]
    public async Task LeaseManager_Release_ReleasesLinkedSlot()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);
        var test = FirstTest(h, executionId)!;

        await h.Leases.ReleaseAssignmentAsync(
            test.Id, claim!.Assignment.Id, claim.Assignment.AssignmentToken,
            nameof(ExecutionStatus.Cancelled), CancellationToken.None);

        Assert.Equal(GridAssignmentStatus.Cancelled, h.Db.Assignments[claim.Assignment.Id].Status);
        Assert.Equal(MobileSlotStatus.Released, h.Db.Slots[claim.Slot.Id].Status);
    }

    [Fact]
    public async Task StaleToken_CannotReleaseReboundSlot()
    {
        var h = new Harness();
        var executionId = SeedScenario(h);
        var claim = await h.Scheduler.TryClaimMobileAsync(executionId, NoExclusions, CancellationToken.None);
        Assert.NotNull(claim);

        // Simulate rebinding by a newer owner (direct row update).
        var slot = h.Db.Slots[claim!.Slot.Id];
        var staleToken = claim.ClaimToken;
        slot.ClaimToken = Guid.NewGuid();
        slot.Status = MobileSlotStatus.Active;

        var leases = new MobileSlotLeaseService(
            new FakeMobileStore(h.Db, new FakeUnitOfWork(h.Db)),
            new FakeAssignmentStore(h.Db, new FakeUnitOfWork(h.Db)), h.Clock,
            Options.Create(new MobileOptions()), h.Audit,
            NullLogger<MobileSlotLeaseService>.Instance);
        await Assert.ThrowsAsync<ConflictException>(() =>
            leases.ReleaseAsync(Project, slot.Id, staleToken, CancellationToken.None));
        Assert.Equal(MobileSlotStatus.Active, h.Db.Slots[slot.Id].Status);
    }

    [Fact]
    public async Task Registration_AllowsAppium_RejectsMixedCapabilities()
    {
        var db = new FakeDb();
        var uow = new FakeUnitOfWork(db);
        var workers = new FakeWorkerStore(db, uow);
        var assignments = new FakeAssignmentStore(db, uow);
        var auth = new AllowAuth();
        var grid = new ExecutionGridService(workers, assignments, auth,
            new FakeCurrentUser(), new FixedClock(DateTimeOffset.UtcNow), new FakeAudit(),
            new FakePublisher(), Options.Create(new GridOptions { ProvisioningToken = "provision-secret" }),
            NullLogger<ExecutionGridService>.Instance);

        var mobile = await grid.RegisterWorkerAsync(new RegisterWorkerCommand(
            "appium-1", "Appium 1", "appium", "appium", null, "2.0", 2,
            "http://worker:8090", "provision-secret"), CancellationToken.None);
        var registered = db.Workers[mobile.WorkerId];
        Assert.Equal("appium", registered.WorkerType);
        Assert.Equal("appium", registered.Framework);
        Assert.Empty(registered.Browsers);

        await Assert.ThrowsAsync<ValidationException>(() => grid.RegisterWorkerAsync(new RegisterWorkerCommand(
            "appium-2", null, "appium", "appium", new[] { "chromium" }, null, null, null, "provision-secret"),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => grid.RegisterWorkerAsync(new RegisterWorkerCommand(
            "web-1", null, "playwright", "playwright", null, null, null, null, "provision-secret"),
            CancellationToken.None));
    }

    private sealed class AllowAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public bool IsAuthenticated => true;
        public string? ExternalIdentityId => "test-user";
        public string? Email => "test@example.com";
        public string? DisplayName => "Test";
        public IReadOnlyCollection<string> Roles => new[] { "admin" };
        public IReadOnlyCollection<string> Permissions => new[] { "settings.manage" };
    }

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }
}
