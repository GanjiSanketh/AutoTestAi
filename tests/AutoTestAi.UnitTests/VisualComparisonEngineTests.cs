using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Visual;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.TestCases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-4D-2: visual comparison inside terminal persistence —
/// checkpoint gating, mismatch verdicts, diff artifacts, skip/error
/// discipline, fencing, idempotency, cancellation.</summary>
public sealed class VisualComparisonEngineTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    private sealed class FakeExecutionStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<ExecutionArtifact> Artifacts = new();
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int skip, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionListRow>>(Array.Empty<ExecutionListRow>());
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
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
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
        public Guid VersionId = Guid.NewGuid();
        public Task<TestCase?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<TestCase?>(null);
        public Task<TestCaseVersion?> GetVersionByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<TestCaseVersion?>(null);
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

    private sealed class FakeWorker : IPlaywrightWorkerClient
    {
        public Task<string> StartAssignmentAsync(WorkerAssignmentDto assignment, CancellationToken ct) => throw new NotImplementedException();
        public Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelAssignmentAsync(string assignmentId, CancellationToken ct) => Task.CompletedTask;
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
        public readonly List<byte[]> UploadedBodies = new();
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct)
        {
            if (ThrowOnUpload) throw new InvalidOperationException("Storage is down.");
            UploadedKeys.Add(key);
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            UploadedBodies.Add(ms.ToArray());
            return Task.CompletedTask;
        }
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<byte[]> DownloadAsync(string key, int maxBytes, CancellationToken ct)
            => throw new NotFoundException("Stored object not found.");
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    private sealed class FakeAssignments : IGridAssignmentStore
    {
        public GridAssignment? Active;
        public Task<GridAssignment?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Active);
        public Task<GridAssignment?> FindActiveByTestAsync(Guid executionTestId, CancellationToken ct) => Task.FromResult(Active);
        public Task<GridAssignment?> FindActiveByRefAsync(string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveByProjectAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountQueuedExecutionsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task AddAsync(GridAssignment a, CancellationToken ct) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeVisual : IVisualComparisonService
    {
        public int Calls;
        public Func<Guid, int, VisualComparisonOutcome?>? OnCompare;
        public Task<VisualComparisonOutcome?> CompareCheckpointAsync(Guid projectId, Guid testCaseVersionId, int stepOrder, byte[] actualPng, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(OnCompare?.Invoke(testCaseVersionId, stepOrder));
        }
    }

    private sealed record Harness(
        ExecutionEngine Engine,
        FakeExecutionStore Store,
        FakeArtifacts Artifacts,
        FakeAudit Audit,
        FakeVisual Visual,
        Execution Execution,
        ExecutionTest Test);

    private static Harness Create(FakeVisual? visual = null)
    {
        var store = new FakeExecutionStore();
        var artifacts = new FakeArtifacts();
        var audit = new FakeAudit();
        var visualService = visual ?? new FakeVisual();
        var engine = new ExecutionEngine(
            store, new FakeCases(), new FakeWorker(), new FakeEvents(), artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), audit,
            NullLogger<ExecutionEngine>.Instance,
            visualComparison: visualService);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Running };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            TestCaseVersionId = Guid.NewGuid(), Status = ExecutionTestStatus.Running,
            Framework = "appium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        return new Harness(engine, store, artifacts, audit, visualService, execution, test);
    }

    private static byte[] Solid(int width, int height, SixLabors.ImageSharp.PixelFormats.Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(color);
        });
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static WorkerExecutionOutcome PassedOutcome(params (int Order, string Action, string ShotName)[] checkpoints)
    {
        var steps = checkpoints.Select(c => new WorkerStepResultDto(
            c.Order, c.Action, null, "passed", 1, 2, 1, null)).ToList();
        var shots = checkpoints.Select(c => new WorkerScreenshotDto(
            c.Order, c.ShotName, "image/png",
            Convert.ToBase64String(Solid(10, 10, new SixLabors.ImageSharp.PixelFormats.Rgba32(0, 0, 0, 255))))).ToList();
        return new WorkerExecutionOutcome(
            ExecutionTestStatus.Passed, FailureClassification.Unknown, null, null, 5,
            steps, Array.Empty<WorkerLogDto>(), shots, 1);
    }

    private static VisualComparisonOutcome MatchOutcome(Guid baselineId, int stepOrder = 3) => new(
        baselineId, new string('b', 64), 10, 10, 10, 10, false, 0, 10, false,
        RgbaPixelComparer.AlgorithmVersion, 3, null);

    private static VisualComparisonOutcome MismatchOutcome(Guid baselineId, int stepOrder = 3) => new(
        baselineId, new string('c', 64), 10, 10, 10, 10, false, 234, 10, true,
        RgbaPixelComparer.AlgorithmVersion, 4,
        Solid(10, 10, new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 0, 0, 255)));

    [Fact]
    public async Task NoVerifyScreenshot_ComparisonNeverRuns()
    {
        var h = Create();
        var outcome = PassedOutcome((1, "tap", "step-1-failure.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(0, h.Visual.Calls);
        Assert.Equal(ExecutionTestStatus.Passed, h.Test.Status);
        Assert.DoesNotContain(h.Audit.Actions, a => a == "visual.compared");
    }

    [Fact]
    public async Task Match_LeavesPassedVerdict_WithAudit_AndNoDiff()
    {
        var baselineId = Guid.NewGuid();
        var visual = new FakeVisual { OnCompare = (_, _) => MatchOutcome(baselineId) };
        var h = Create(visual);
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(1, visual.Calls);
        Assert.Equal(ExecutionTestStatus.Passed, h.Test.Status);
        Assert.Contains(h.Audit.Actions, a => a == "visual.compared");
        Assert.DoesNotContain(h.Store.Artifacts, a => a.ArtifactType == "visual-diff");
    }

    [Fact]
    public async Task Mismatch_BecomesFailedTestFailure_WithDiffArtifact()
    {
        var baselineId = Guid.NewGuid();
        var visual = new FakeVisual { OnCompare = (_, _) => MismatchOutcome(baselineId) };
        var h = Create(visual);
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, h.Test.Status);
        Assert.Equal(FailureClassification.TestFailure, h.Test.FailureClassification);
        Assert.Equal("VisualMismatch", h.Test.ErrorType);
        Assert.Contains("step 3", h.Test.ErrorMessage);
        Assert.Contains("234 bps exceeds 10 bps", h.Test.ErrorMessage);
        Assert.Contains("baseline cccccccc", h.Test.ErrorMessage);
        var diff = Assert.Single(h.Store.Artifacts, a => a.ArtifactType == "visual-diff");
        Assert.Equal("image/png", diff.ContentType);
        Assert.Equal(3, diff.StepOrder);
        Assert.Contains("step-003-step-3-visual-diff.png", diff.StorageKey);
        Assert.Contains(h.Audit.Actions, a => a == "visual.compared");
        Assert.Single(h.Artifacts.UploadedKeys, k => k == diff.StorageKey);
    }

    [Fact]
    public async Task FailedOutcome_IsNeverRewritten()
    {
        var visual = new FakeVisual { OnCompare = (_, _) => MismatchOutcome(Guid.NewGuid()) };
        var h = Create(visual);
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png")) with
        {
            Status = ExecutionTestStatus.Failed,
            Classification = FailureClassification.TestFailure,
            ErrorType = "AssertionError",
            ErrorMessage = "Step 1 (tap) failed",
        };

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(0, visual.Calls);
        Assert.Equal(ExecutionTestStatus.Failed, h.Test.Status);
        Assert.Equal("AssertionError", h.Test.ErrorType);
        Assert.Equal("Step 1 (tap) failed", h.Test.ErrorMessage);
        Assert.DoesNotContain(h.Audit.Actions, a => a == "visual.compared");
    }

    [Fact]
    public async Task MissingBaseline_LeavesVerdict_WithWarningOnly()
    {
        var visual = new FakeVisual { OnCompare = (_, _) => null };
        var h = Create(visual);
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(1, visual.Calls);
        Assert.Equal(ExecutionTestStatus.Passed, h.Test.Status);
        Assert.DoesNotContain(h.Audit.Actions, a => a == "visual.compared");
        Assert.DoesNotContain(h.Store.Artifacts, a => a.ArtifactType == "visual-diff");
    }

    [Fact]
    public async Task MultipleCheckpoints_AllEvaluated_FirstMismatchWinsMessage()
    {
        var calls = new List<int>();
        var visual = new FakeVisual
        {
            OnCompare = (_, order) =>
            {
                calls.Add(order);
                return order == 1
                    ? MatchOutcome(Guid.NewGuid(), order)
                    : MismatchOutcome(Guid.NewGuid(), order);
            },
        };
        var h = Create(visual);
        var outcome = PassedOutcome(
            (1, "verifyScreenshot", "step-1-verify.png"),
            (2, "verifyScreenshot", "step-2-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.Equal(ExecutionTestStatus.Failed, h.Test.Status);
        Assert.Contains("step 2", h.Test.ErrorMessage);
        Assert.Single(h.Store.Artifacts, a => a.ArtifactType == "visual-diff");
    }

    [Fact]
    public async Task MissingBaselineForOne_RestStillEvaluated()
    {
        var visual = new FakeVisual
        {
            OnCompare = (_, order) => order == 1 ? null : MatchOutcome(Guid.NewGuid(), order),
        };
        var h = Create(visual);
        var outcome = PassedOutcome(
            (1, "verifyScreenshot", "step-1-verify.png"),
            (2, "verifyScreenshot", "step-2-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(2, visual.Calls);
        Assert.Equal(ExecutionTestStatus.Passed, h.Test.Status);
    }

    [Fact]
    public async Task DiffUploadFailure_PreservesMismatchVerdict()
    {
        var visual = new FakeVisual { OnCompare = (_, _) => MismatchOutcome(Guid.NewGuid()) };
        var h = Create(visual);
        h.Artifacts.ThrowOnUpload = true;
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, h.Test.Status);
        Assert.Equal(FailureClassification.TestFailure, h.Test.FailureClassification);
    }

    [Fact]
    public async Task DoublePersist_IsIdempotent_NoDuplicateDiff()
    {
        var visual = new FakeVisual { OnCompare = (_, _) => MismatchOutcome(Guid.NewGuid()) };
        var h = Create(visual);
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);
        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Single(h.Store.Artifacts, a => a.ArtifactType == "visual-diff");
        Assert.Equal(1, visual.Calls);
    }

    [Fact]
    public async Task StaleAssignment_RejectedBeforeComparison()
    {
        var visual = new FakeVisual { OnCompare = (_, _) => MismatchOutcome(Guid.NewGuid()) };
        var h = Create(visual);
        var live = new GridAssignment { Id = Guid.NewGuid(), Status = GridAssignmentStatus.Running };
        h.Test.StartedAssignmentId = Guid.NewGuid();
        var assignments = new FakeAssignments { Active = live };
        var engine = new ExecutionEngine(
            h.Store, new FakeCases(), new FakeWorker(), new FakeEvents(), h.Artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), new FakeAudit(),
            NullLogger<ExecutionEngine>.Instance,
            assignments: assignments,
            visualComparison: visual);
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None));
        Assert.Equal(0, visual.Calls);
    }

    [Fact]
    public async Task NullVersionId_SkipsComparison()
    {
        var visual = new FakeVisual { OnCompare = (_, _) => MismatchOutcome(Guid.NewGuid()) };
        var h = Create(visual);
        h.Test.TestCaseVersionId = null;
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));

        await h.Engine.PersistResultAsync(h.Execution.Id, outcome, CancellationToken.None);

        Assert.Equal(0, visual.Calls);
        Assert.Equal(ExecutionTestStatus.Passed, h.Test.Status);
    }

    [Fact]
    public async Task CancelledToken_Propagates()
    {
        var visual = new FakeVisual { OnCompare = (_, _) => MatchOutcome(Guid.NewGuid()) };
        var h = Create(visual);
        var outcome = PassedOutcome((3, "verifyScreenshot", "step-3-verify.png"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Engine.PersistResultAsync(h.Execution.Id, outcome, cts.Token));
    }
}
