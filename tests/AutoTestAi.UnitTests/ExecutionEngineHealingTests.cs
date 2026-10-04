using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.SelfHealing;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Phase 2 Slice 11: the engine embeds the project healing policy in the
/// worker assignment (disabled by default), persists fenced healing outcomes,
/// keeps the bound test version immutable, and never lets healing failures
/// break the normal result/failure pipeline.
/// </summary>
public sealed class ExecutionEngineHealingTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    private sealed class FakeStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<ExecutionStepResult> Steps = new();
        public readonly List<ExecutionLog> Logs = new();
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.Id == id));
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Tests.Where(t => t.ExecutionId == id).ToList());
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionStepResult>>(Steps.Where(s => s.ExecutionTestId == id).ToList());
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct)
        { Steps.AddRange(rows); return Task.CompletedTask; }
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct)
        { Steps.RemoveAll(s => s.ExecutionTestId == id); return Task.CompletedTask; }
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? afterId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionLog>>(Logs.Where(l => l.ExecutionTestId == id).ToList());
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct)
        { Logs.AddRange(rows); return Task.CompletedTask; }
        public Task DeleteLogsAsync(Guid id, CancellationToken ct)
        { Logs.RemoveAll(l => l.ExecutionTestId == id); return Task.CompletedTask; }
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Array.Empty<ExecutionArtifact>());
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<ExecutionArtifact?>(null);
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FailureAnalysis>>(Array.Empty<FailureAnalysis>());
        public Task<FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<FailureAnalysis?>(null);
        public Task AddAnalysisAsync(FailureAnalysis a, CancellationToken ct) => Task.CompletedTask;
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int sk, int ta, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionAsync(Execution e, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) => throw new NotImplementedException();
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
        public Task<IReadOnlyList<TestCase>> ListAsync(Guid p, string? s, TestCaseStatusFilter f, int sk, int ta, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCase?> GetByKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddTestCaseAsync(TestCase t, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseVersion> AddNextVersionAsync(Guid id, Func<int, TestCaseVersion> f, CancellationToken ct) => throw new NotImplementedException();
        public Task AddVersionAsync(TestCaseVersion v, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeWorker : IPlaywrightWorkerClient
    {
        public Task<string> StartAssignmentAsync(WorkerAssignmentDto a, CancellationToken ct) => Task.FromResult("assign-1");
        public Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelAssignmentAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        public readonly List<string> Events = new();
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
        { Events.Add(eventName); return Task.CompletedTask; }
    }

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured => false;
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<byte[]> DownloadAsync(string key, int maxBytes, CancellationToken ct)
            => throw new NotFoundException("Stored object not found.");
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string a, string e, string? id, Guid? p, string? m, CancellationToken ct)
        { Actions.Add(a); return Task.CompletedTask; }
    }

    private sealed class FakePolicies : ISelfHealingPolicyStore
    {
        public SelfHealingPolicy? Row { get; set; }
        public Task<SelfHealingPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult(Row?.ProjectId == projectId ? Row : null);
        public Task AddAsync(SelfHealingPolicy p, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHealing : ISelfHealingService
    {
        public Guid? SeenAssignmentId { get; private set; }
        public int SeenCount { get; private set; }
        public Exception? RecordFailure { get; set; }
        public Task<WorkerHealingPolicyDto> ResolvePolicyAsync(Guid executionId, CancellationToken ct)
            => Task.FromResult(new WorkerHealingPolicyDto(false, false, 1, null, null, new[] { "css" }));
        public Task<IReadOnlyList<HealingCandidateDto>> SuggestCandidatesAsync(
            Guid executionId, HealingEvidenceDto evidence, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HealingCandidateDto>>(Array.Empty<HealingCandidateDto>());
        public Task<int> RecordAttemptsAsync(
            Guid executionId, Guid? assignmentId,
            IReadOnlyList<WorkerHealingAttemptDto> attempts, CancellationToken ct)
        {
            SeenAssignmentId = assignmentId;
            SeenCount = attempts.Count;
            if (RecordFailure is not null) throw RecordFailure;
            return Task.FromResult(attempts.Count);
        }
    }

    private sealed class FakeAssignments : IGridAssignmentStore
    {
        public GridAssignment? Active { get; set; }
        public Task<GridAssignment?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<GridAssignment?> FindActiveByTestAsync(Guid testId, CancellationToken ct)
            => Task.FromResult(Active?.ExecutionTestId == testId ? Active : null);
        public Task<GridAssignment?> FindActiveByRefAsync(string workerRef, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset n, int t, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveByProjectAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountQueuedExecutionsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task AddAsync(GridAssignment a, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record Harness(
        ExecutionEngine Engine, FakeStore Store, FakeCases Cases, FakeHealing Healing,
        FakeAssignments Assignments, Guid ExecutionId, Guid TestId, Guid VersionId, string VersionJson);

    private static Harness Create(SelfHealingPolicy? policy, GridAssignment? lease)
    {
        var cases = new FakeCases();
        var store = new FakeStore();
        var policies = new FakePolicies { Row = policy };
        var healing = new FakeHealing();
        var assignments = new FakeAssignments { Active = lease };

        const string stepsJson = """[{"order":1,"action":"click","target":"css=#old"}]""";
        var testCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "HEAL-001", Title = "Heal",
            Framework = "playwright", Priority = Priority.High,
            Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        var version = new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 1, SourceCode = "// v1",
            StructuredSteps = JsonDocument.Parse(stepsJson),
            ReviewStatus = ReviewStatus.Approved,
        };
        cases.Versions.Add(version);

        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Queued };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = testCase.Id,
            TestCaseVersionId = version.Id, Status = ExecutionTestStatus.Queued,
            Framework = "playwright", Browser = "chromium", Attempt = 1,
        };
        if (lease is not null)
        {
            lease.ExecutionId = execution.Id;
            lease.ExecutionTestId = test.Id;
            test.StartedAssignmentId = lease.Id;
            test.AssignmentId = lease.Id;
        }
        store.Executions.Add(execution);
        store.Tests.Add(test);

        var engine = new ExecutionEngine(
            store, cases, new FakeWorker(), new FakePublisher(), new FakeArtifacts(),
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance,
            leases: null, assignments: assignments, healingPolicies: policies, healing: healing);
        return new Harness(engine, store, cases, healing, assignments,
            execution.Id, test.Id, version.Id, stepsJson);
    }

    private static WorkerExecutionOutcome FailedOutcomeWithHealing() => new(
        ExecutionTestStatus.Failed, FailureClassification.AutomationFailure,
        "StepError", "Step 1 (click) failed: Timeout waiting for locator.",
        12,
        new[] { new WorkerStepResultDto(1, "click", "css=#old", "failed", 1, 2, 1, "Timeout waiting for locator.") },
        Array.Empty<WorkerLogDto>(), Array.Empty<WorkerScreenshotDto>(), 1,
        new[]
        {
            new WorkerHealingAttemptDto(1, "click", "css", "css=#old", "testid", "new-btn",
                "test-attribute", "Applied", 1, true, false, null),
        });

    [Fact]
    public async Task Prepare_AttachesPolicy_WhenEnabled_Null_WhenDisabled()
    {
        var enabled = Create(new SelfHealingPolicy { ProjectId = ProjectA, Enabled = true }, null);
        var prepared = await enabled.Engine.PrepareAsync(enabled.ExecutionId, CancellationToken.None);
        Assert.True(prepared.CanRun);
        Assert.NotNull(prepared.Assignment!.Healing);
        Assert.True(prepared.Assignment.Healing!.Enabled);

        var disabled = Create(null, null);
        var preparedOff = await disabled.Engine.PrepareAsync(disabled.ExecutionId, CancellationToken.None);
        Assert.True(preparedOff.CanRun);
        Assert.Null(preparedOff.Assignment!.Healing);
    }

    [Fact]
    public async Task PersistResult_RecordsHealing_WithFencedLease_And_PreservesVersion()
    {
        var lease = new GridAssignment
        {
            WorkerAssignmentRef = "ref", Status = GridAssignmentStatus.Running,
            AssignmentToken = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        var h = Create(new SelfHealingPolicy { ProjectId = ProjectA, Enabled = true }, lease);
        h.Store.Tests.First().Status = ExecutionTestStatus.Running;

        await h.Engine.PersistResultAsync(h.ExecutionId, FailedOutcomeWithHealing(), CancellationToken.None);

        Assert.Equal(lease.Id, h.Healing.SeenAssignmentId);
        Assert.Equal(1, h.Healing.SeenCount);
        // Normal failure pipeline intact: classification + terminal state stand.
        Assert.Equal(ExecutionStatus.Failed, h.Store.Executions.First().Status);
        Assert.Equal(FailureClassification.AutomationFailure, h.Store.Tests.First().FailureClassification);
        // Immutable test version: structured steps byte-identical.
        var version = h.Cases.Versions.First(v => v.Id == h.VersionId);
        Assert.Equal(h.VersionJson, version.StructuredSteps!.RootElement.GetRawText());
    }

    [Fact]
    public async Task PersistResult_StaleHealingReport_NeverFailsTheResult()
    {
        var lease = new GridAssignment
        {
            WorkerAssignmentRef = "ref", Status = GridAssignmentStatus.Running,
            AssignmentToken = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        var h = Create(new SelfHealingPolicy { ProjectId = ProjectA, Enabled = true }, lease);
        h.Healing.RecordFailure = new ConflictException("Stale healing report rejected.");
        h.Store.Tests.First().Status = ExecutionTestStatus.Running;

        await h.Engine.PersistResultAsync(h.ExecutionId, FailedOutcomeWithHealing(), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, h.Store.Executions.First().Status);
        Assert.Single(h.Store.Steps);
    }

    [Fact]
    public async Task PersistResult_WithoutHealing_SkipsRecording()
    {
        var h = Create(null, null);
        h.Store.Tests.First().Status = ExecutionTestStatus.Running;
        var outcome = FailedOutcomeWithHealing() with
        {
            HealingAttempts = Array.Empty<WorkerHealingAttemptDto>(),
        };
        await h.Engine.PersistResultAsync(h.ExecutionId, outcome, CancellationToken.None);
        Assert.Equal(0, h.Healing.SeenCount);
        Assert.Equal(ExecutionStatus.Failed, h.Store.Executions.First().Status);
    }
}
