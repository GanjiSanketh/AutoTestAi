using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.SelfHealing;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-4B-2: mobile dispatch, outcome mapping, retry, cancel,
/// redaction, engine routing, and artifact persistence.</summary>
public sealed class MobileExecutionCoordinatorTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    // ---------- fakes ----------

    private sealed class FakeExecutionStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<ExecutionArtifact> Artifacts = new();
        public int SaveCalls;
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.Id == id));
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Tests.FirstOrDefault(t => t.Id == id));
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Tests.Where(t => t.ExecutionId == id).ToList());
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid projectId, string key, CancellationToken ct)
            => Task.FromResult<Execution?>(null);
        public Task AddExecutionAsync(Execution e, CancellationToken ct) { Executions.Add(e); return Task.CompletedTask; }
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) { Tests.Add(t); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) { SaveCalls++; return Task.CompletedTask; }
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int skip, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionListRow>>(Array.Empty<ExecutionListRow>());
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionStepResult>>(Array.Empty<ExecutionStepResult>());
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? afterId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionLog>>(Array.Empty<ExecutionLog>());
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteLogsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Artifacts.Where(a => a.ExecutionTestId == id).ToList());
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Artifacts.FirstOrDefault(a => a.Id == id));
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) { Artifacts.Add(a); return Task.CompletedTask; }
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FailureAnalysis>>(Array.Empty<FailureAnalysis>());
        public Task<FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<FailureAnalysis?>(null);
        public Task AddAnalysisAsync(FailureAnalysis a, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCases : ITestCaseStore
    {
        public readonly List<TestCase> Cases = new();
        public readonly List<TestCaseVersion> Versions = new();
        public Task<TestCase?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Cases.FirstOrDefault(c => c.Id == id));
        public Task<TestCaseVersion?> GetVersionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Versions.FirstOrDefault(v => v.Id == id));
        public Task<int> CountAsync(Guid p, string? s, TestCaseStatusFilter f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TestCase>> ListAsync(Guid p, string? s, TestCaseStatusFilter f, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCase?> GetByKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddTestCaseAsync(TestCase c, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseVersion> AddNextVersionAsync(Guid id, Func<int, TestCaseVersion> f, CancellationToken ct) => throw new NotImplementedException();
        public Task AddVersionAsync(TestCaseVersion v, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeMobileRegistry : IMobileRegistryStore
    {
        public readonly Dictionary<Guid, MobileDevicePool> Pools = new();
        public readonly Dictionary<Guid, MobileDevice> Devices = new();
        public readonly Dictionary<Guid, MobileDeviceSlot> Slots = new();
        public readonly Dictionary<Guid, MobileDeviceSession> Sessions = new();
        public readonly Dictionary<Guid, MobileApp> Apps = new();
        public Task<MobileDevicePool?> GetPoolByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Pools.TryGetValue(id, out var p) ? p : null);
        public Task<MobileDevice?> GetDeviceByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Devices.TryGetValue(id, out var d) ? d : null);
        public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Slots.TryGetValue(id, out var s) ? s : null);
        public Task<MobileApp?> GetAppByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Apps.TryGetValue(id, out var a) ? a : null);
        public Task<MobileDeviceSession?> GetSessionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Sessions.TryGetValue(id, out var s) ? s : null);
        public Task<MobileDeviceSession?> FindSessionByAssignmentAsync(Guid assignmentId, CancellationToken ct)
            => Task.FromResult(Sessions.Values.FirstOrDefault(s => s.AssignmentId == assignmentId));
        public Task<IReadOnlyList<MobileDeviceSession>> ListStaleSessionsAsync(DateTimeOffset s, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSession>>(Array.Empty<MobileDeviceSession>());
        public Task AddSessionAsync(MobileDeviceSession session, CancellationToken ct) { Sessions[session.Id] = session; return Task.CompletedTask; }
        public Task<MobileDevicePool?> FindPoolByNameAsync(Guid p, string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct) => throw new NotImplementedException();
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
        public Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAppAsync(MobileApp a, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAssignments : IGridAssignmentStore
    {
        public readonly Dictionary<Guid, GridAssignment> Rows = new();
        public Task<GridAssignment?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var a) ? a : null);
        public Task<GridAssignment?> FindActiveByTestAsync(Guid testId, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(a => a.ExecutionTestId == testId &&
                (a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running)));
        public Task<GridAssignment?> FindActiveByRefAsync(string r, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(a => a.WorkerAssignmentRef == r &&
                (a.Status == GridAssignmentStatus.Claimed || a.Status == GridAssignmentStatus.Running)));
        public Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveByProjectAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountQueuedExecutionsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task AddAsync(GridAssignment a, CancellationToken ct) { Rows[a.Id] = a; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeScheduler : IGridScheduler
    {
        public Func<Guid, Task<MobileGridClaim?>>? ClaimMobile;
        public readonly List<(Guid TestId, string Status)> Releases = new();
        public int Renewals;
        public Task<MobileGridClaim?> TryClaimMobileAsync(Guid executionId, IReadOnlySet<Guid> exclude, CancellationToken ct)
            => ClaimMobile is null ? Task.FromResult<MobileGridClaim?>(null) : ClaimMobile(executionId);
        public Task RenewLeaseAsync(Guid assignmentId, CancellationToken ct) { Renewals++; return Task.CompletedTask; }
        public Task ReleaseLeaseAsync(Guid executionTestId, string terminalStatus, CancellationToken ct)
        { Releases.Add((executionTestId, terminalStatus)); return Task.CompletedTask; }
        public Task<GridClaim?> TryClaimAsync(Guid executionId, IReadOnlySet<Guid> excludeWorkerIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ReapExpiredLeasesAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<int> CountActiveAssignmentsAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeWorkerClient : IMobileWorkerClient
    {
        public readonly List<MobileAssignmentDto> Started = new();
        public int CancelCalls;
        public Func<string, CancellationToken, Task<MobileAssignmentProgressDto>>? OnGet;
        public Exception? StartError;
        public Task<string> StartAssignmentAsync(MobileAssignmentDto assignment, CancellationToken ct)
        {
            if (StartError is not null) throw StartError;
            Started.Add(assignment);
            return Task.FromResult(assignment.AssignmentId);
        }
        public Task<MobileAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct)
            => OnGet is null ? throw new InvalidOperationException("No scripted progress.") : OnGet(assignmentId, ct);
        public Task CancelAssignmentAsync(string assignmentId, CancellationToken ct)
        { CancelCalls++; return Task.CompletedTask; }
    }

    private sealed class FakeEvents : IExecutionEventPublisher
    {
        public readonly List<string> Names = new();
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
        { Names.Add(eventName); return Task.CompletedTask; }
    }

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured { get; set; } = true;
        public bool ThrowOnUpload { get; set; }
        public readonly List<string> UploadedKeys = new();
        public readonly List<string> UploadedContentTypes = new();
        public readonly List<byte[]> UploadedBodies = new();
        public string PresignedUrl { get; set; } = "https://artifacts.example/mobile-apps/shop.apk?exp=900";
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct)
        {
            if (ThrowOnUpload) throw new InvalidOperationException("Storage is down.");
            UploadedKeys.Add(key);
            UploadedContentTypes.Add(contentType);
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            UploadedBodies.Add(ms.ToArray());
            return Task.CompletedTask;
        }
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult(PresignedUrl);
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<byte[]> DownloadAsync(string key, int maxBytes, CancellationToken ct)
            => throw new NotFoundException("Stored object not found.");
    }

    private sealed class FakeEnvelopes : IExecutionVariablesStore
    {
        public Guid? EnvironmentId;
        public Task<ExecutionVariables?> GetByExecutionAsync(Guid executionId, CancellationToken ct)
            => Task.FromResult<ExecutionVariables?>(EnvironmentId.HasValue
                ? new ExecutionVariables { ExecutionId = executionId, EnvironmentId = EnvironmentId } : null);
        public Task SaveAsync(ExecutionVariables envelope, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeVarResolver : IVariableResolutionService
    {
        public ResolvedVariables Resolved = new ResolvedVariables(
            new Dictionary<string, string>(), Array.Empty<string>(), Array.Empty<string>(), new HashSet<string>());
        public Task<ResolvedVariables> ResolveForExecutionAsync(Guid projectId, Guid environmentId, Guid? suiteId, Guid executionId, CancellationToken ct)
            => Task.FromResult(Resolved);
    }

    private sealed class FakeAudit : IAuditService
    {
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakePolicyStore : ISelfHealingPolicyStore
    {
        public SelfHealingPolicy? Row;
        public Task<SelfHealingPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult(Row is not null && Row.ProjectId == projectId ? Row : null);
        public Task AddAsync(SelfHealingPolicy policy, CancellationToken ct) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeWebWorker : IPlaywrightWorkerClient
    {
        public bool Called;
        public Task<string> StartAssignmentAsync(WorkerAssignmentDto assignment, CancellationToken ct)
        { Called = true; throw new InvalidOperationException("Web worker must not run mobile executions."); }
        public Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelAssignmentAsync(string assignmentId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCoordinator : IMobileExecutionCoordinator
    {
        public int Calls;
        public WorkerExecutionOutcome Outcome = new(ExecutionTestStatus.Passed, FailureClassification.Unknown,
            null, null, 5, Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(), 1);
        public Task<WorkerExecutionOutcome> RunMobileAsync(Guid executionId, Func<Task>? heartbeatAsync, CancellationToken ct)
        { Calls++; return Task.FromResult(Outcome); }
    }

    private sealed record Harness(
        MobileExecutionCoordinator Coordinator,
        FakeExecutionStore Store,
        FakeCases Cases,
        FakeMobileRegistry Mobile,
        FakeScheduler Scheduler,
        FakeAssignments Assignments,
        FakeWorkerClient Worker,
        FakeEvents Events,
        FakeArtifacts Artifacts,
        Execution Execution,
        ExecutionTest Test,
        Guid PoolId,
        Guid AppId,
        Guid SlotId,
        Guid DeviceId,
        GridWorker GridWorker);

    private static Harness Create(string stepsJson = """[{"order":1,"action":"launchApp"}]""",
        ISelfHealingPolicyStore? healingPolicies = null)
    {
        var store = new FakeExecutionStore();
        var cases = new FakeCases();
        var mobile = new FakeMobileRegistry();
        var scheduler = new FakeScheduler();
        var assignments = new FakeAssignments();
        var worker = new FakeWorkerClient();
        var events = new FakeEvents();
        var artifacts = new FakeArtifacts();

        var testCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "MOB-001", Title = "Mobile",
            Framework = "appium", Platform = "android",
            Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        var version = new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 1,
            StructuredSteps = JsonDocument.Parse(stepsJson),
            ReviewStatus = ReviewStatus.Approved,
        };
        cases.Versions.Add(version);

        var poolId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        mobile.Pools[poolId] = new MobileDevicePool
        {
            Id = poolId, ProjectId = ProjectA, Name = "android-smoke",
            Platform = MobilePlatform.Android, Status = MobilePoolStatus.Active,
        };
        mobile.Devices[deviceId] = new MobileDevice
        {
            Id = deviceId, ProjectId = ProjectA, PoolId = poolId, Platform = MobilePlatform.Android,
            Model = "Pixel 8", Udid = "emulator-5554",
            AutomationName = MobileAutomationNames.UiAutomator2, Status = MobileDeviceStatus.Available,
        };
        mobile.Slots[slotId] = new MobileDeviceSlot
        {
            Id = slotId, ProjectId = ProjectA, PoolId = poolId, DeviceId = deviceId,
            SlotNumber = 1, Status = MobileSlotStatus.Free,
        };
        mobile.Apps[appId] = new MobileApp
        {
            Id = appId, ProjectId = ProjectA, Platform = MobilePlatform.Android,
            Name = "Shop", PackageId = "com.example.shop", InstallPolicy = MobileInstallPolicy.Preinstalled,
        };
        var gridWorker = new GridWorker
        {
            Id = workerId, WorkerKey = "appium-1", DisplayName = "appium-1",
            WorkerType = "appium", Framework = "appium", Capacity = 4,
            Status = GridWorkerStatus.Available, BaseUrl = "http://appium-worker:8091",
        };

        var execution = new Execution
        {
            ProjectId = ProjectA, Status = ExecutionStatus.Running,
            MobileDevicePoolId = poolId, MobileAppId = appId,
        };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = testCase.Id, TestCaseVersionId = version.Id,
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);

        scheduler.ClaimMobile = _ =>
        {
            var assignmentId = Guid.NewGuid();
            var token = Guid.NewGuid();
            var lease = new GridAssignment
            {
                Id = assignmentId, ExecutionId = execution.Id, ExecutionTestId = test.Id,
                WorkerId = workerId, Status = GridAssignmentStatus.Claimed,
                WorkerAssignmentRef = test.Id.ToString("N"), AssignmentToken = token,
            };
            assignments.Rows[assignmentId] = lease;
            var slot = mobile.Slots[slotId];
            slot.Status = MobileSlotStatus.Claimed;
            slot.AssignmentId = assignmentId;
            slot.WorkerId = workerId;
            test.AssignmentId = assignmentId;
            test.AssignmentToken = token;
            return Task.FromResult<MobileGridClaim?>(
                new MobileGridClaim(lease, gridWorker, slot, token, Guid.NewGuid()));
        };

        var executionOptions = Options.Create(new ExecutionOptions { WorkerPollIntervalSeconds = 0 });
        var coordinator = new MobileExecutionCoordinator(
            store, cases, mobile, scheduler, assignments,
            new MobileSessionService(mobile, assignments, new SystemDateTimeProvider(),
                NullLogger<MobileSessionService>.Instance),
            worker, events, artifacts,
            executionOptions, Options.Create(new GridOptions()),
            new MobileCapabilityBuilder(Options.Create(new MobileOptions())),
            new SystemDateTimeProvider(),
            NullLogger<MobileExecutionCoordinator>.Instance,
            healingPolicies: healingPolicies);
        return new Harness(coordinator, store, cases, mobile, scheduler, assignments,
            worker, events, artifacts, execution, test, poolId, appId, slotId, deviceId, gridWorker);
    }

    private static MobileAssignmentProgressDto TerminalProgress(
        string status, string? classification, string? errorMessage = null,
        IReadOnlyList<MobileScreenshotDto>? screenshots = null,
        IReadOnlyList<MobileStepResultDto>? steps = null,
        IReadOnlyList<MobilePageSourceDto>? pageSources = null,
        IReadOnlyList<MobileServerLogDto>? serverLogs = null,
        IReadOnlyList<WorkerHealingAttemptDto>? healingAttempts = null)
        => new("test", status, 1,
            steps ?? new[] { new MobileStepResultDto(1, "launchApp", null, "passed", 1, 2, 1, null) },
            Array.Empty<MobileLogDto>(),
            new MobileAssignmentResultDto("test", status, classification, status == "passed" ? null : "StepError",
                errorMessage, 5,
                steps ?? new[] { new MobileStepResultDto(1, "launchApp", null, "passed", 1, 2, 1, null) },
                Array.Empty<MobileLogDto>(),
                screenshots ?? Array.Empty<MobileScreenshotDto>(), "appium-session-1",
                pageSources, serverLogs, healingAttempts),
            "appium-session-1");

    private static void ScriptTerminal(Harness h, MobileAssignmentProgressDto terminal)
    {
        var calls = 0;
        h.Worker.OnGet = (_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new MobileAssignmentProgressDto("test", "running", null,
                    Array.Empty<MobileStepResultDto>(), Array.Empty<MobileLogDto>(), null, "appium-session-1")
                : terminal);
        };
    }

    // ---------- dispatch + outcomes ----------

    [Fact]
    public async Task SuccessfulRun_ReturnsPassedOutcome_AndClosesSession()
    {
        var h = Create();
        var shot = new MobileScreenshotDto(1, "step-1-failure.png", "image/png",
            Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        ScriptTerminal(h, TerminalProgress("passed", "unknown",
            screenshots: new[] { shot }));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        Assert.Equal(1, outcome.Attempt);
        Assert.Single(outcome.Screenshots);
        Assert.Equal("step-1-failure.png", outcome.Screenshots[0].FileName);
        var session = Assert.Single(h.Mobile.Sessions.Values);
        Assert.Equal(MobileSessionStatus.Closed, session.Status);
        Assert.Equal("appium-session-1", session.AppiumSessionId);
        Assert.Equal(h.Test.StartedAssignmentId, session.AssignmentId);
        Assert.Single(h.Worker.Started);
        var sent = h.Worker.Started[0];
        Assert.Equal(h.Test.Id.ToString("N"), sent.AssignmentId);
        Assert.Null(sent.App.DownloadUrl);
        Assert.Equal("com.example.shop", sent.Capabilities.AppPackage);
        Assert.Null(typeof(MobileAssignmentDto).GetProperty("ClaimToken"));
        Assert.Contains(h.Events.Names, n => n == ExecutionEvents.ExecutionStepCompleted);
    }

    [Fact]
    public async Task AssertionFailure_MapsToFailedTestFailure_WithoutRetry()
    {
        var h = Create();
        ScriptTerminal(h, TerminalProgress("failed", "test", "Step 1 (assertVisible) failed"));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
        Assert.Equal(FailureClassification.TestFailure, outcome.Classification);
        Assert.Equal(1, outcome.Attempt);
        Assert.Equal(1, h.Test.Attempt);
        var session = Assert.Single(h.Mobile.Sessions.Values);
        Assert.Equal(MobileSessionStatus.Closed, session.Status);
    }

    [Fact]
    public async Task EnvironmentFailure_MapsToErrorEnvironment()
    {
        var h = Create();
        ScriptTerminal(h, TerminalProgress("error", "environment", "Appium session failure: device offline"));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Equal(FailureClassification.EnvironmentFailure, outcome.Classification);
    }

    [Fact]
    public async Task ContractRejection_MapsToAutomation_WithoutRetry()
    {
        var h = Create();
        h.Worker.StartError = new WorkerInfrastructureException("The Appium worker rejected the request (HTTP 400).")
            { IsRetryable = false };

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Equal(FailureClassification.AutomationFailure, outcome.Classification);
        Assert.Equal(1, outcome.Attempt);
        Assert.Contains(h.Scheduler.Releases, r => r.Status == nameof(GridAssignmentStatus.Released));
    }

    [Fact]
    public async Task RetryableFailure_RetriesOnce_ThenSucceeds()
    {
        var h = Create();
        var calls = 0;
        h.Worker.OnGet = (_, _) =>
        {
            calls++;
            if (calls == 1)
                throw new WorkerInfrastructureException("The Appium worker is unreachable.");
            if (calls == 2)
                return Task.FromResult(new MobileAssignmentProgressDto("test", "running", null,
                    Array.Empty<MobileStepResultDto>(), Array.Empty<MobileLogDto>(), null, "appium-session-1"));
            return Task.FromResult(TerminalProgress("passed", "unknown"));
        };

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        Assert.Equal(2, outcome.Attempt);
    }

    [Fact]
    public async Task RetryableFailure_Exhausted_MapsToEnvironment()
    {
        var h = Create();
        h.Worker.OnGet = (_, _) => throw new WorkerInfrastructureException("The Appium worker is unreachable.");

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Equal(FailureClassification.EnvironmentFailure, outcome.Classification);
        Assert.Equal(2, outcome.Attempt);
    }

    [Fact]
    public async Task TrackPhaseInfraFailure_PropagatesToAttemptLoop_DoesNotLoopInternally()
    {
        // Regression for the track-phase infinite-loop bug: a failure AFTER
        // successful dispatch must leave ClaimDispatchTrackAsync (one dispatch
        // per attempt) and consume the single bounded outer retry.
        var h = Create();
        var polls = 0;
        h.Worker.OnGet = (_, _) =>
        {
            polls++;
            if (polls == 1)
                throw new WorkerInfrastructureException("The Appium worker is unreachable.");
            if (polls == 2)
                return Task.FromResult(new MobileAssignmentProgressDto("test", "running", null,
                    Array.Empty<MobileStepResultDto>(), Array.Empty<MobileLogDto>(), null, "appium-session-1"));
            return Task.FromResult(TerminalProgress("passed", "unknown"));
        };

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        Assert.Equal(2, outcome.Attempt);
        Assert.Equal(2, h.Worker.Started.Count);
    }

    [Fact]
    public async Task Cancellation_AbortsWorker_AndClosesSession()
    {
        var h = Create();
        h.Worker.OnGet = (_, ct) => Task.Delay(Timeout.Infinite, ct)
            .ContinueWith(_ => TerminalProgress("passed", "unknown"),
                TaskContinuationOptions.OnlyOnRanToCompletion);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Coordinator.RunMobileAsync(h.Execution.Id, null, cts.Token));

        Assert.Equal(1, h.Worker.CancelCalls);
        var session = Assert.Single(h.Mobile.Sessions.Values);
        Assert.Equal(MobileSessionStatus.Closed, session.Status);
    }

    [Fact]
    public async Task InstallApp_WithoutStorage_FailsAutomation()
    {
        var h = Create();
        h.Mobile.Apps[h.AppId].InstallPolicy = MobileInstallPolicy.Install;
        h.Mobile.Apps[h.AppId].StorageKey = null;
        h.Worker.OnGet = (_, _) => Task.FromResult(TerminalProgress("passed", "unknown"));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Equal(FailureClassification.AutomationFailure, outcome.Classification);
        Assert.Empty(h.Worker.Started);
    }

    [Fact]
    public async Task Preinstalled_DownloadUrlStaysNull()
    {
        var h = Create();
        ScriptTerminal(h, TerminalProgress("passed", "unknown"));

        await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        var sent = Assert.Single(h.Worker.Started);
        Assert.Null(sent.App.DownloadUrl);
        Assert.Null(sent.Capabilities.App);
    }

    [Fact]
    public async Task Install_PopulatesDownloadUrl()
    {
        var h = Create();
        h.Mobile.Apps[h.AppId].InstallPolicy = MobileInstallPolicy.Install;
        h.Mobile.Apps[h.AppId].StorageKey = "mobile-apps/shop.apk";
        ScriptTerminal(h, TerminalProgress("passed", "unknown"));

        await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        var sent = Assert.Single(h.Worker.Started);
        Assert.Equal(h.Artifacts.PresignedUrl, sent.App.DownloadUrl);
        Assert.Equal(h.Artifacts.PresignedUrl, sent.Capabilities.App);
    }

    [Fact]
    public async Task Reinstall_PopulatesDownloadUrl()
    {
        var h = Create();
        h.Mobile.Apps[h.AppId].InstallPolicy = MobileInstallPolicy.Reinstall;
        h.Mobile.Apps[h.AppId].StorageKey = "mobile-apps/shop.apk";
        ScriptTerminal(h, TerminalProgress("passed", "unknown"));

        await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        var sent = Assert.Single(h.Worker.Started);
        Assert.Equal(h.Artifacts.PresignedUrl, sent.App.DownloadUrl);
        Assert.Equal(h.Artifacts.PresignedUrl, sent.Capabilities.App);
    }

    [Fact]
    public async Task DownloadUrl_NeverLeaksIntoOutcome()
    {
        var h = Create();
        h.Mobile.Apps[h.AppId].InstallPolicy = MobileInstallPolicy.Install;
        h.Mobile.Apps[h.AppId].StorageKey = "mobile-apps/shop.apk";
        var url = h.Artifacts.PresignedUrl;
        ScriptTerminal(h, TerminalProgress("failed", "test", "Step 1 (tap) failed: element missing"));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
        var sent = Assert.Single(h.Worker.Started);
        Assert.Equal(url, sent.App.DownloadUrl);
        Assert.DoesNotContain(url, outcome.ErrorMessage ?? string.Empty);
        foreach (var step in outcome.Steps)
            Assert.DoesNotContain(url, step.ErrorMessage ?? string.Empty);
        foreach (var log in outcome.Logs)
            Assert.DoesNotContain(url, log.Message);
        Assert.DoesNotContain("mobile-apps/shop.apk", outcome.ErrorMessage ?? string.Empty);
        Assert.Null(typeof(MobileAssignmentDto).GetProperty("ClaimToken"));
    }

    // ---------- Slice 3C-4B-3: failure evidence ----------

    [Fact]
    public async Task FailedRun_MapsEvidenceIntoOutcome()
    {
        var h = Create();
        var source = new MobilePageSourceDto(1, "step-1-pagesource.xml", "text/xml",
            "<hierarchy><node text=\"ok\" /></hierarchy>");
        var log = new MobileServerLogDto("appium.log", "text/plain", "[1 info] worker accepted assignment");
        ScriptTerminal(h, TerminalProgress("failed", "test", "Step 1 (tap) failed",
            pageSources: new[] { source }, serverLogs: new[] { log }));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
        var mapped = Assert.Single(outcome.PageSources ?? Array.Empty<WorkerPageSourceDto>());
        Assert.Equal("step-1-pagesource.xml", mapped.FileName);
        Assert.Equal("text/xml", mapped.ContentType);
        Assert.Contains("ok", mapped.XmlContent);
        var mappedLog = Assert.Single(outcome.ServerLogs ?? Array.Empty<WorkerServerLogDto>());
        Assert.Equal("appium.log", mappedLog.FileName);
        Assert.Equal("text/plain", mappedLog.ContentType);
    }

    [Fact]
    public async Task Evidence_SanitizedWithSecrets_NoTokenOrUrlLeak()
    {
        var h = Create();
        var token = h.Test.AssignmentToken?.ToString() ?? "assignment-token-value";
        var url = h.Artifacts.PresignedUrl;
        var envelopes = new FakeEnvelopes { EnvironmentId = Guid.NewGuid() };
        var resolver = new FakeVarResolver
        {
            Resolved = new ResolvedVariables(
                new Dictionary<string, string>(),
                new[] { "hunter2-secret", token, url }, Array.Empty<string>(),
                new HashSet<string>()),
        };
        var coordinator = new MobileExecutionCoordinator(
            h.Store, h.Cases, h.Mobile, h.Scheduler, h.Assignments,
            new MobileSessionService(h.Mobile, h.Assignments, new SystemDateTimeProvider(),
                NullLogger<MobileSessionService>.Instance),
            h.Worker, h.Events, h.Artifacts,
            Options.Create(new ExecutionOptions { WorkerPollIntervalSeconds = 0 }),
            Options.Create(new GridOptions()),
            new MobileCapabilityBuilder(Options.Create(new MobileOptions())),
            new SystemDateTimeProvider(),
            NullLogger<MobileExecutionCoordinator>.Instance,
            resolver, envelopes);
        var source = new MobilePageSourceDto(1, "step-1-pagesource.xml", "text/xml",
            $"<hierarchy><node text=\"hunter2-secret\" /><node id=\"{token}\" /><link href=\"{url}\" /></hierarchy>");
        var log = new MobileServerLogDto("appium.log", "text/plain",
            $"token {token} url {url} secret hunter2-secret");
        ScriptTerminal(h, TerminalProgress("failed", "test", "Step 1 (tap) failed",
            pageSources: new[] { source }, serverLogs: new[] { log }));

        var outcome = await coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
        var mapped = Assert.Single(outcome.PageSources ?? Array.Empty<WorkerPageSourceDto>());
        Assert.DoesNotContain("hunter2-secret", mapped.XmlContent);
        Assert.DoesNotContain(token, mapped.XmlContent);
        Assert.DoesNotContain(url, mapped.XmlContent);
        Assert.DoesNotContain("artifacts.example", mapped.XmlContent);
        var mappedLog = Assert.Single(outcome.ServerLogs ?? Array.Empty<WorkerServerLogDto>());
        Assert.DoesNotContain("hunter2-secret", mappedLog.TextContent);
        Assert.DoesNotContain(token, mappedLog.TextContent);
        Assert.DoesNotContain(url, mappedLog.TextContent);
    }

    [Fact]
    public async Task Evidence_DoesNotAlterRetryClassification()
    {
        var h = Create();
        var source = new MobilePageSourceDto(1, "step-1-pagesource.xml", "text/xml", "<hierarchy/>");
        var calls = 0;
        h.Worker.OnGet = (_, _) =>
        {
            calls++;
            if (calls == 1)
                throw new WorkerInfrastructureException("The Appium worker is unreachable.");
            if (calls == 2)
                return Task.FromResult(new MobileAssignmentProgressDto("test", "running", null,
                    Array.Empty<MobileStepResultDto>(), Array.Empty<MobileLogDto>(), null, "appium-session-1"));
            return Task.FromResult(TerminalProgress("failed", "test", "Step 1 (tap) failed",
                pageSources: new[] { source }));
        };

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        // Infrastructure retry still bounded to one retry; the terminal
        // assertion failure maps to test failure with evidence attached.
        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
        Assert.Equal(FailureClassification.TestFailure, outcome.Classification);
        Assert.Equal(2, outcome.Attempt);
        Assert.Single(outcome.PageSources ?? Array.Empty<WorkerPageSourceDto>());
    }

    [Fact]
    public void Transport_RoundTrip_ParsesEvidenceCollections()
    {
        var json = """
            {"assignmentId":"test","status":"failed","currentStepOrder":1,
             "stepResults":[],"logs":[],
             "result":{"assignmentId":"test","status":"failed","classification":"test",
              "errorType":"AssertionError","errorMessage":"Step 1 failed","durationMs":5,
              "stepResults":[],"logs":[],"screenshots":[],
              "pageSources":[{"stepOrder":1,"fileName":"step-1-pagesource.xml","contentType":"text/xml","xmlContent":"<hierarchy/>"}],
              "serverLogs":[{"fileName":"appium.log","contentType":"text/plain","textContent":"[1 info] tail"}],
              "appiumSessionId":"s"},"appiumSessionId":"s"}
            """;
        using var doc = JsonDocument.Parse(json);
        var progress = AutoTestAi.Infrastructure.Executions.MobileWorkerTransport.ParseProgress(doc.RootElement);

        Assert.Equal("failed", progress.Status);
        var source = Assert.Single(progress.Result!.PageSources ?? Array.Empty<MobilePageSourceDto>());
        Assert.Equal("step-1-pagesource.xml", source.FileName);
        Assert.Equal("<hierarchy/>", source.XmlContent);
        var log = Assert.Single(progress.Result!.ServerLogs ?? Array.Empty<MobileServerLogDto>());
        Assert.Equal("appium.log", log.FileName);
        Assert.Equal("[1 info] tail", log.TextContent);
    }

    [Fact]
    public async Task PersistResult_Evidence_PersistAsArtifacts()
    {
        var store = new FakeExecutionStore();
        var artifacts = new FakeArtifacts();
        var engine = new ExecutionEngine(
            store, new FakeCases(), new FakeWebWorker(), new FakeEvents(), artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Failed, FailureClassification.TestFailure, "AssertionError", "Step 1 failed", 5,
            new[] { new WorkerStepResultDto(1, "tap", "accessibilityId=x", "failed", 1, 2, 1, "gone") },
            Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(),
            1,
            null,
            new[] { new WorkerPageSourceDto(1, "step-1-pagesource.xml", "text/xml", "<hierarchy/>") },
            new[] { new WorkerServerLogDto("appium.log", "text/plain", "[1 info] tail") });

        await engine.PersistResultAsync(execution.Id, outcome, CancellationToken.None);

        Assert.Equal(2, store.Artifacts.Count);
        var source = store.Artifacts.Single(a => a.ArtifactType == "page-source");
        Assert.Equal("text/xml", source.ContentType);
        Assert.Equal(1, source.StepOrder);
        Assert.Contains("step-001-step-1-pagesource.xml", source.StorageKey);
        var log = store.Artifacts.Single(a => a.ArtifactType == "appium-log");
        Assert.Equal("text/plain", log.ContentType);
        Assert.Contains("finish-appium.log", log.StorageKey);
        Assert.Equal(2, artifacts.UploadedKeys.Count);
        Assert.Contains(artifacts.UploadedContentTypes, c => c == "text/xml");
        Assert.Contains(artifacts.UploadedContentTypes, c => c == "text/plain");
        Assert.Contains("<hierarchy/>", System.Text.Encoding.UTF8.GetString(artifacts.UploadedBodies[0]));
    }

    [Fact]
    public async Task PersistResult_OversizedEvidence_Skipped_WithoutCorruptingResult()
    {
        var store = new FakeExecutionStore();
        var artifacts = new FakeArtifacts();
        var engine = new ExecutionEngine(
            store, new FakeCases(), new FakeWebWorker(), new FakeEvents(), artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Failed, FailureClassification.TestFailure, "AssertionError", "Step 1 failed", 5,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(), Array.Empty<WorkerScreenshotDto>(), 1,
            null,
            new[] { new WorkerPageSourceDto(1, "step-1-pagesource.xml", "text/xml", new string('x', MobileEvidenceBounds.MaxPageSourceChars + 1)) },
            new[] { new WorkerServerLogDto("appium.log", "text/plain", new string('y', MobileEvidenceBounds.MaxServerLogChars + 1)) });

        await engine.PersistResultAsync(execution.Id, outcome, CancellationToken.None);

        Assert.Empty(store.Artifacts);
        Assert.Empty(artifacts.UploadedKeys);
        Assert.Equal(ExecutionTestStatus.Failed, test.Status);
    }

    [Fact]
    public async Task PersistResult_EvidenceUploadFailure_DoesNotCorruptResult()
    {
        var store = new FakeExecutionStore();
        var artifacts = new FakeArtifacts { ThrowOnUpload = true };
        var engine = new ExecutionEngine(
            store, new FakeCases(), new FakeWebWorker(), new FakeEvents(), artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Failed, FailureClassification.TestFailure, "AssertionError", "Step 1 failed", 5,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(), Array.Empty<WorkerScreenshotDto>(), 1,
            null,
            new[] { new WorkerPageSourceDto(1, "step-1-pagesource.xml", "text/xml", "<hierarchy/>") },
            null);

        await engine.PersistResultAsync(execution.Id, outcome, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, test.Status);
        Assert.Empty(store.Artifacts);
    }

    [Fact]
    public async Task PersistResult_Evidence_IsIdempotent()
    {
        var store = new FakeExecutionStore();
        var artifacts = new FakeArtifacts();
        var engine = new ExecutionEngine(
            store, new FakeCases(), new FakeWebWorker(), new FakeEvents(), artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Failed, FailureClassification.TestFailure, "AssertionError", "Step 1 failed", 5,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(), Array.Empty<WorkerScreenshotDto>(), 1,
            null,
            new[] { new WorkerPageSourceDto(1, "step-1-pagesource.xml", "text/xml", "<hierarchy/>") },
            null);

        await engine.PersistResultAsync(execution.Id, outcome, CancellationToken.None);
        await engine.PersistResultAsync(execution.Id, outcome, CancellationToken.None);

        Assert.Single(store.Artifacts);
        Assert.Single(artifacts.UploadedKeys);
    }

    [Fact]
    public async Task SecretValues_MaskedInOutcome()
    {
        var h = Create("""[{"order":1,"action":"inputText","target":"resourceId=com.shop:id/password","value":"${{ PWD }}"}]""");
        var envelopes = new FakeEnvelopes { EnvironmentId = Guid.NewGuid() };
        var resolver = new FakeVarResolver
        {
            Resolved = new ResolvedVariables(
                new Dictionary<string, string> { ["PWD"] = "hunter2-secret" },
                new[] { "hunter2-secret" }, Array.Empty<string>(),
                new HashSet<string> { "PWD" }),
        };
        var coordinator = new MobileExecutionCoordinator(
            h.Store, h.Cases, h.Mobile, h.Scheduler, h.Assignments,
            new MobileSessionService(h.Mobile, h.Assignments, new SystemDateTimeProvider(),
                NullLogger<MobileSessionService>.Instance),
            h.Worker, h.Events, h.Artifacts,
            Options.Create(new ExecutionOptions { WorkerPollIntervalSeconds = 0 }),
            Options.Create(new GridOptions()),
            new MobileCapabilityBuilder(Options.Create(new MobileOptions())),
            new SystemDateTimeProvider(),
            NullLogger<MobileExecutionCoordinator>.Instance,
            resolver, envelopes);
        h.Worker.OnGet = (_, _) => Task.FromResult(
            TerminalProgress("failed", "test", "Step 1 (inputText) failed after typing hunter2-secret"));

        var outcome = await coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
        Assert.DoesNotContain("hunter2-secret", outcome.ErrorMessage ?? string.Empty);
        // The trusted transport still carries the resolved value to the worker.
        var sent = Assert.Single(h.Worker.Started);
        Assert.Equal("hunter2-secret", sent.Steps[0].Value);
    }

    // ---------- Slice 3C-4C: self-healing ----------

    private static FakePolicyStore EnabledPolicy(bool aiFallback = false) => new()
    {
        Row = new SelfHealingPolicy
        {
            ProjectId = ProjectA, Enabled = true, AiFallbackEnabled = aiFallback,
            AllowedStrategies = "accessibilityid,resourceid",
        },
    };

    [Fact]
    public void Transport_RoundTrip_ParsesHealing()
    {
        var json = """
            {"assignmentId":"test","status":"passed","currentStepOrder":1,
             "stepResults":[{"order":1,"action":"tap","target":"accessibilityId=gone","status":"passed",
              "startedAtUnixMs":1,"completedAtUnixMs":2,"durationMs":1,"errorMessage":null,
              "healed":true,"recoveredTarget":"resourceId=com.shop:id/login","healingStrategy":"resourceId","aiAssisted":false}],
             "logs":[],
             "result":{"assignmentId":"test","status":"passed","durationMs":5,
              "stepResults":[],"logs":[],"screenshots":[],
              "healingAttempts":[{"stepOrder":1,"stepAction":"tap","originalStrategy":"accessibilityId",
               "originalValue":"gone","recoveredStrategy":"resourceId","recoveredValue":"com.shop:id/login",
               "healingStrategy":"Structural","status":"Applied","candidateCount":1,
               "wasApplied":true,"isAiAssisted":false,"errorMessage":null}],
              "appiumSessionId":"s"},"appiumSessionId":"s"}
            """;
        using var doc = JsonDocument.Parse(json);
        var progress = AutoTestAi.Infrastructure.Executions.MobileWorkerTransport.ParseProgress(doc.RootElement);

        var step = Assert.Single(progress.StepResults);
        Assert.True(step.Healed);
        Assert.Equal("resourceId=com.shop:id/login", step.RecoveredTarget);
        var attempt = Assert.Single(progress.Result!.HealingAttempts ?? Array.Empty<WorkerHealingAttemptDto>());
        Assert.Equal("Structural", attempt.HealingStrategy);
        Assert.True(attempt.WasApplied);
    }

    [Fact]
    public async Task Policy_Enabled_PassedIntoAssignment()
    {
        var h = Create(healingPolicies: EnabledPolicy());
        ScriptTerminal(h, TerminalProgress("passed", "unknown"));

        await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        var sent = Assert.Single(h.Worker.Started);
        Assert.NotNull(sent.Healing);
        Assert.True(sent.Healing!.Enabled);
        Assert.Equal(1, sent.Healing!.MaxAttemptsPerStep);
        Assert.Contains("accessibilityid", sent.Healing!.AllowedStrategies);
    }

    [Fact]
    public async Task Policy_Absent_StaysNull_PreservingLegacyBehavior()
    {
        var h = Create();
        ScriptTerminal(h, TerminalProgress("passed", "unknown"));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        var sent = Assert.Single(h.Worker.Started);
        Assert.Null(sent.Healing);
        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        Assert.Null(outcome.HealingAttempts);
    }

    [Fact]
    public async Task HealedSteps_MappedIntoOutcome_WithAttempts()
    {
        var h = Create(healingPolicies: EnabledPolicy());
        var healedStep = new MobileStepResultDto(1, "tap", "accessibilityId=gone", "passed",
            1, 2, 1, null, true, "accessibilityId=found", "accessibilityId", false);
        var attempt = new WorkerHealingAttemptDto(1, "tap", "accessibilityId", "gone",
            "accessibilityId", "found", "TestAttribute", "Applied", 1, true, false, null);
        ScriptTerminal(h, TerminalProgress("passed", "unknown",
            steps: new[] { healedStep }, healingAttempts: new[] { attempt }));

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        var step = Assert.Single(outcome.Steps);
        Assert.True(step.Healed);
        Assert.Equal("accessibilityId=found", step.RecoveredTarget);
        var mapped = Assert.Single(outcome.HealingAttempts ?? Array.Empty<WorkerHealingAttemptDto>());
        Assert.Equal("TestAttribute", mapped.HealingStrategy);
        Assert.True(mapped.WasApplied);
        Assert.False(mapped.IsAiAssisted);
    }

    [Fact]
    public async Task Healing_DoesNotAlterRetryClassification()
    {
        var h = Create(healingPolicies: EnabledPolicy());
        var calls = 0;
        h.Worker.OnGet = (_, _) =>
        {
            calls++;
            if (calls == 1)
                throw new WorkerInfrastructureException("The Appium worker is unreachable.");
            if (calls == 2)
                return Task.FromResult(new MobileAssignmentProgressDto("test", "running", null,
                    Array.Empty<MobileStepResultDto>(), Array.Empty<MobileLogDto>(), null, "appium-session-1"));
            return Task.FromResult(TerminalProgress("passed", "unknown",
                healingAttempts: new[]
                {
                    new WorkerHealingAttemptDto(1, "tap", "accessibilityId", "gone",
                        "resourceId", "com.shop:id/login", "Structural", "Applied", 1, true, false, null),
                }));
        };

        var outcome = await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);

        // The infrastructure retry stays bounded to one retry; the healed
        // recovery maps to passed with its attempt record attached.
        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        Assert.Equal(2, outcome.Attempt);
        var mapped = Assert.Single(outcome.HealingAttempts ?? Array.Empty<WorkerHealingAttemptDto>());
        Assert.Equal("Structural", mapped.HealingStrategy);
    }

    // ---------- engine routing + persistence ----------

    [Fact]
    public async Task Engine_RoutesAppiumToCoordinator_LeavingWebUntouched()
    {
        var store = new FakeExecutionStore();
        var cases = new FakeCases();
        var webWorker = new FakeWebWorker();
        var events = new FakeEvents();
        var artifacts = new FakeArtifacts();
        var audit = new FakeAudit();
        var coordinator = new FakeCoordinator();
        var engine = new ExecutionEngine(
            store, cases, webWorker, events, artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), audit,
            NullLogger<ExecutionEngine>.Instance,
            mobileCoordinator: coordinator);

        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);

        var outcome = await engine.RunWorkerAsync(execution.Id, null, CancellationToken.None);

        Assert.Equal(1, coordinator.Calls);
        Assert.False(webWorker.Called);
        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
    }

    [Fact]
    public async Task Engine_WithoutCoordinator_FailsMobileClosed()
    {
        var store = new FakeExecutionStore();
        var engine = new ExecutionEngine(
            store, new FakeCases(), new FakeWebWorker(), new FakeEvents(), new FakeArtifacts(),
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);

        var outcome = await engine.RunWorkerAsync(execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Equal(FailureClassification.AutomationFailure, outcome.Classification);
    }

    [Fact]
    public async Task PersistResult_MobileScreenshots_PersistAsArtifacts()
    {
        var store = new FakeExecutionStore();
        var artifacts = new FakeArtifacts();
        var engine = new ExecutionEngine(
            store, new FakeCases(), new FakeWebWorker(), new FakeEvents(), artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Running, Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Passed, FailureClassification.Unknown, null, null, 5,
            new[] { new WorkerStepResultDto(1, "tap", "accessibilityId=x", "passed", 1, 2, 1, null) },
            Array.Empty<WorkerLogDto>(),
            new[] { new WorkerScreenshotDto(1, "step-1-failure.png", "image/png", Convert.ToBase64String(new byte[] { 9 })) },
            1);

        await engine.PersistResultAsync(execution.Id, outcome, CancellationToken.None);

        var row = Assert.Single(store.Artifacts);
        Assert.Equal("screenshot", row.ArtifactType);
        Assert.Equal(1, row.StepOrder);
        Assert.Contains("step-001-step-1-failure.png", row.StorageKey);
        Assert.Single(artifacts.UploadedKeys);
    }

    [Fact]
    public async Task CloseForAssignment_ClosesAndIsIdempotent()
    {
        var h = Create();
        ScriptTerminal(h, TerminalProgress("passed", "unknown"));
        await h.Coordinator.RunMobileAsync(h.Execution.Id, null, CancellationToken.None);
        var session = Assert.Single(h.Mobile.Sessions.Values);
        var sessions = new MobileSessionService(h.Mobile, h.Assignments, new SystemDateTimeProvider(),
            NullLogger<MobileSessionService>.Instance);
        // Already closed by the run: idempotent no-op.
        await sessions.CloseForAssignmentAsync(ProjectA, session.AssignmentId!.Value,
            h.Test.AssignmentToken!.Value, CancellationToken.None);
        Assert.Equal(MobileSessionStatus.Closed, session.Status);
        // Unknown assignment: no-op, never throws.
        await sessions.CloseForAssignmentAsync(ProjectA, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
    }
}
