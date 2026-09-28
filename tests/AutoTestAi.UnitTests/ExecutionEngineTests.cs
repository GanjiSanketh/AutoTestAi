using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 5 §47: engine prepare/run/persist/finalize paths, retry
/// discipline, redaction, and idempotent terminal states.</summary>
public sealed class ExecutionEngineTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    // ---------- fakes (small, local) ----------

    private sealed class FakeStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<ExecutionStepResult> Steps = new();
        public readonly List<ExecutionLog> Logs = new();
        public readonly List<ExecutionArtifact> Artifacts = new();
        private long _logId;

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
        { foreach (var row in rows) { row.Id = ++_logId; Logs.Add(row); } return Task.CompletedTask; }
        public Task DeleteLogsAsync(Guid id, CancellationToken ct)
        { Logs.RemoveAll(l => l.ExecutionTestId == id); return Task.CompletedTask; }
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Artifacts.Where(a => a.ExecutionTestId == id).ToList());
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Artifacts.FirstOrDefault(a => a.Id == id));
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) { Artifacts.Add(a); return Task.CompletedTask; }
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct)
        { Artifacts.RemoveAll(a => a.ExecutionTestId == id); return Task.CompletedTask; }
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int sk, int ta, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionAsync(Execution e, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeWorker : IPlaywrightWorkerClient
    {
        public int Starts;
        public int Cancels;
        public Func<WorkerAssignmentDto, string> StartHandler = _ => "assign-1";
        public Func<string, WorkerAssignmentProgressDto>? ProgressHandler;
        public Exception? StartFailure;

        public Task<string> StartAssignmentAsync(WorkerAssignmentDto assignment, CancellationToken ct)
        {
            Starts++;
            if (StartFailure is not null) throw StartFailure;
            return Task.FromResult(StartHandler(assignment));
        }

        public Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string id, CancellationToken ct)
        {
            if (ProgressHandler is not null) return Task.FromResult(ProgressHandler(id));
            throw new InvalidOperationException("ProgressHandler not configured.");
        }

        public Task CancelAssignmentAsync(string id, CancellationToken ct)
        {
            Cancels++;
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedEvent(string EventName, object Payload);

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        public readonly List<PublishedEvent> Events = new();
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
        {
            Events.Add(new PublishedEvent(eventName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured { get; set; } = true;
        public readonly List<string> UploadedKeys = new();
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct)
        { UploadedKeys.Add(key); return Task.CompletedTask; }
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string a, string e, string? id, Guid? p, string? m, CancellationToken ct)
        { Actions.Add(a); return Task.CompletedTask; }
    }

    // ---------- builders ----------

    private sealed record Harness(
        ExecutionEngine Engine, FakeStore Store, FakeTestCaseStore Cases,
        FakeWorker Worker, FakePublisher Events, FakeArtifacts Artifacts, FakeAudit Audit,
        Guid ExecutionId, Guid TestId, Guid VersionId);

    private static Harness Create(ReviewStatus review = ReviewStatus.Approved, bool withSteps = true)
    {
        var cases = new FakeTestCaseStore();
        var store = new FakeStore();
        var worker = new FakeWorker();
        var events = new FakePublisher();
        var artifacts = new FakeArtifacts();
        var audit = new FakeAudit();

        var testCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "LOGIN-001", Title = "Login",
            Framework = "playwright", Priority = Priority.High,
            Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        var version = new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 3, SourceCode = "// v3",
            StructuredSteps = withSteps
                ? JsonDocument.Parse("""[{"order":1,"action":"navigate","target":"https://example.test/login"},{"order":2,"action":"fill","target":"#password","value":"hunter2"}]""")
                : null,
            ReviewStatus = review,
        };
        cases.Versions.Add(version);

        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Queued };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = testCase.Id,
            TestCaseVersionId = version.Id, Status = ExecutionTestStatus.Queued,
            Framework = "playwright", Browser = "chromium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);

        var engine = new ExecutionEngine(
            store, cases, worker, events, artifacts,
            Options.Create(new ExecutionOptions()),
            new SystemDateTimeProvider(), audit,
            NullLogger<ExecutionEngine>.Instance);
        return new Harness(engine, store, cases, worker, events, artifacts, audit,
            execution.Id, test.Id, version.Id);
    }

    private static WorkerAssignmentProgressDto TerminalProgress(
        string status = "passed", string? classification = null)
        => new("assign-1", status switch
        {
            "passed" => "passed",
            "failed" => "failed",
            "timedOut" => "timedOut",
            _ => status,
        }, 2,
            new[]
            {
                new WorkerStepResultDto(1, "navigate", "https://example.test/login", "passed", 1, 2, 1, null),
                new WorkerStepResultDto(2, "fill", "#password", "passed", 2, 3, 1, null),
            },
            new[] { new WorkerLogDto(1, 2, "info", "done") },
            new WorkerAssignmentResultDto("assign-1", status, classification, null, null, 5,
                new[]
                {
                    new WorkerStepResultDto(1, "navigate", "https://example.test/login", "passed", 1, 2, 1, null),
                    new WorkerStepResultDto(2, "fill", "#password", "passed", 2, 3, 1, null),
                },
                new[] { new WorkerLogDto(1, 2, "info", "done") },
                Array.Empty<WorkerScreenshotDto>()));

    // ---------- prepare ----------

    [Fact]
    public async Task Prepare_Success_TransitionsToRunning_WithRedactedAssignment()
    {
        var h = Create();

        var prepared = await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);

        Assert.True(prepared.CanRun);
        Assert.Equal(ExecutionStatus.Running, h.Store.Executions.First().Status);
        Assert.Equal(ExecutionTestStatus.Running, h.Store.Tests.First().Status);
        Assert.NotNull(prepared.Assignment);
        var assignment = prepared.Assignment!;
        Assert.Equal(2, assignment.Steps.Count);
        Assert.Equal("https://example.test/login", assignment.TargetUrl);
        var fill = assignment.Steps.First(s => s.Order == 2);
        Assert.Equal("[REDACTED]", fill.Value); // never dispatched in plaintext
        Assert.Contains(h.Events.Events, e => e.EventName == ExecutionEvents.ExecutionStarted);
        Assert.Contains(h.Audit.Actions, a => a == "execution.started");
    }

    [Fact]
    public async Task Prepare_UnapprovedVersion_FailsSafe()
    {
        var h = Create(ReviewStatus.Pending);
        var prepared = await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        Assert.False(prepared.CanRun);
        Assert.Equal(ExecutionStatus.Failed, h.Store.Executions.First().Status);
        Assert.Equal(FailureClassification.AutomationFailure, h.Store.Tests.First().FailureClassification);
        Assert.Equal(0, h.Worker.Starts);
    }

    [Fact]
    public async Task Prepare_AlreadyTerminal_SkipsWithoutSideEffects()
    {
        var h = Create();
        h.Store.Tests.First().Status = ExecutionTestStatus.Cancelled;
        var prepared = await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        Assert.False(prepared.CanRun);
        Assert.Empty(h.Audit.Actions);
    }

    [Fact]
    public async Task Prepare_MissingExecution_ThrowsNotFound()
    {
        var h = Create();
        await Assert.ThrowsAsync<NotFoundException>(
            () => h.Engine.PrepareAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // ---------- run ----------

    [Fact]
    public async Task Run_Success_ReturnsPassedOutcome_AndPublishesProgress()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        h.Worker.ProgressHandler = _ => TerminalProgress("passed");
        var heartbeats = 0;

        var outcome = await h.Engine.RunWorkerAsync(h.ExecutionId, () => { heartbeats++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        Assert.Equal(1, h.Worker.Starts); // no retry on success
        Assert.True(heartbeats > 0);
        Assert.Contains(h.Events.Events, e => e.EventName == ExecutionEvents.ExecutionStepCompleted);
        Assert.Contains(h.Events.Events, e => e.EventName == ExecutionEvents.ExecutionLogReceived);
    }

    [Fact]
    public async Task Run_FunctionalFailure_DoesNotRetry()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        h.Worker.ProgressHandler = _ => TerminalProgress("failed", "test");

        var outcome = await h.Engine.RunWorkerAsync(h.ExecutionId, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
        Assert.Equal(FailureClassification.TestFailure, outcome.Classification);
        Assert.Equal(1, h.Worker.Starts);
        Assert.Equal(1, h.Store.Tests.First().Attempt);
    }

    [Fact]
    public async Task Run_InfrastructureFailure_RetriesExactlyOnce()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        var calls = 0;
        h.Worker.StartHandler = _ =>
        {
            calls++;
            if (calls == 1) throw new WorkerInfrastructureException("down");
            return "assign-1";
        };
        h.Worker.ProgressHandler = _ => TerminalProgress("passed");

        var outcome = await h.Engine.RunWorkerAsync(h.ExecutionId, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Passed, outcome.Status);
        Assert.Equal(2, h.Worker.Starts);
        Assert.Equal(2, h.Store.Tests.First().Attempt);
    }

    [Fact]
    public async Task Run_PersistentInfrastructureFailure_YieldsErrorOutcome()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        h.Worker.StartFailure = new WorkerInfrastructureException("down");

        var outcome = await h.Engine.RunWorkerAsync(h.ExecutionId, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Equal(FailureClassification.EnvironmentFailure, outcome.Classification);
        Assert.Equal(2, h.Worker.Starts);
    }

    [Fact]
    public async Task Run_NonRetryableRejection_DoesNotRetry()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        h.Worker.StartFailure = new WorkerInfrastructureException("bad contract") { IsRetryable = false };

        var outcome = await h.Engine.RunWorkerAsync(h.ExecutionId, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Equal(FailureClassification.AutomationFailure, outcome.Classification);
        Assert.Equal(1, h.Worker.Starts);
    }

    [Fact]
    public async Task Run_Cancellation_AbortsWorker_AndSurfacesCancellation()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        h.Worker.ProgressHandler = _ => new WorkerAssignmentProgressDto(
            "assign-1", "running", 1,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(), null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Engine.RunWorkerAsync(h.ExecutionId, null, cts.Token));
        Assert.Equal(1, h.Worker.Cancels);
    }

    [Fact]
    public async Task Run_WorkerTimeout_MapsToTimedOut()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        h.Worker.ProgressHandler = _ => TerminalProgress("timedOut", "environment");

        var outcome = await h.Engine.RunWorkerAsync(h.ExecutionId, null, CancellationToken.None);
        Assert.Equal(ExecutionTestStatus.TimedOut, outcome.Status);
        Assert.Equal(FailureClassification.EnvironmentFailure, outcome.Classification);
    }

    // ---------- persist ----------

    [Fact]
    public async Task Persist_WritesRedactedStepsLogsAndArtifacts_WithTerminalState()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Failed, FailureClassification.TestFailure,
            "AssertionError", "expected x",
            123,
            new[]
            {
                new WorkerStepResultDto(1, "navigate", "https://example.test/login", "passed", 1, 2, 1, null),
                new WorkerStepResultDto(2, "fill", "#password", "failed", 2, 3, 1, "login rejected password=hunter2"),
            },
            new[] { new WorkerLogDto(1, 2, "info", "login rejected api_key=hunter2") },
            new[]
            {
                new WorkerScreenshotDto(2, "step-2-failure.png", "image/png",
                    Convert.ToBase64String(new byte[] { 1, 2, 3 })),
            },
            1);

        await h.Engine.PersistResultAsync(h.ExecutionId, outcome, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Failed, h.Store.Tests.First().Status);
        Assert.Equal(ExecutionStatus.Failed, h.Store.Executions.First().Status);
        Assert.Equal(123, h.Store.Tests.First().DurationMs);
        Assert.Equal(FailureClassification.TestFailure, h.Store.Tests.First().FailureClassification);
        Assert.Equal(2, h.Store.Steps.Count);
        Assert.Single(h.Store.Logs);
        // Sensitive values never persist (worker echoes are re-redacted defensively).
        Assert.DoesNotContain("hunter2", h.Store.Logs.First().Message, StringComparison.Ordinal);
        var failedStep = h.Store.Steps.First(s => s.StepOrder == 2);
        Assert.DoesNotContain("hunter2", failedStep.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        var artifact = Assert.Single(h.Store.Artifacts);
        Assert.Equal("screenshot", artifact.ArtifactType);
        Assert.Equal(2, artifact.StepOrder);
        Assert.StartsWith($"projects/{ProjectA}/executions/{h.ExecutionId}/", artifact.StorageKey, StringComparison.Ordinal);
        Assert.Single(h.Artifacts.UploadedKeys);
        Assert.Contains(h.Audit.Actions, a => a == "execution.failed");
        Assert.Contains(h.Events.Events, e => e.EventName == ExecutionEvents.ExecutionFailed);
    }

    [Fact]
    public async Task Persist_IsIdempotent_WhenAlreadyTerminal()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Passed, FailureClassification.Unknown, null, null, 10,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            Array.Empty<WorkerScreenshotDto>(), 1);
        await h.Engine.PersistResultAsync(h.ExecutionId, outcome, CancellationToken.None);
        var auditCount = h.Audit.Actions.Count;
        await h.Engine.PersistResultAsync(h.ExecutionId, outcome, CancellationToken.None);
        Assert.Equal(auditCount, h.Audit.Actions.Count);
        Assert.Empty(h.Store.Steps);
    }

    [Fact]
    public async Task Persist_SkipsUpload_WhenStorageUnconfigured()
    {
        var h = Create();
        h.Artifacts.IsConfigured = false;
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        var outcome = new WorkerExecutionOutcome(
            ExecutionTestStatus.Failed, FailureClassification.TestFailure, null, "x", 5,
            Array.Empty<WorkerStepResultDto>(), Array.Empty<WorkerLogDto>(),
            new[] { new WorkerScreenshotDto(1, "s.png", "image/png", Convert.ToBase64String(new byte[] { 9 })) },
            1);
        await h.Engine.PersistResultAsync(h.ExecutionId, outcome, CancellationToken.None);
        Assert.Equal(ExecutionTestStatus.Failed, h.Store.Tests.First().Status);
        Assert.Empty(h.Store.Artifacts);
        Assert.Empty(h.Artifacts.UploadedKeys);
    }

    // ---------- finalizers ----------

    [Fact]
    public async Task Finalizers_SetTerminalState_Once()
    {
        var h = Create();
        await h.Engine.PrepareAsync(h.ExecutionId, CancellationToken.None);
        await h.Engine.FinalizeCancelledAsync(h.ExecutionId, "user stop", CancellationToken.None);
        Assert.Equal(ExecutionStatus.Cancelled, h.Store.Executions.First().Status);
        Assert.Equal(ExecutionTestStatus.Cancelled, h.Store.Tests.First().Status);
        Assert.Contains(h.Audit.Actions, a => a == "execution.cancelled");

        // Second finalizer cannot overwrite history.
        await h.Engine.FinalizeTimedOutAsync(h.ExecutionId, "too slow", CancellationToken.None);
        Assert.Equal(ExecutionStatus.Cancelled, h.Store.Executions.First().Status);
    }

    [Fact]
    public void Redactor_MasksOnlySensitiveWrites()
    {
        Assert.Equal("[REDACTED]", ExecutionValueRedactor.RedactStepValue("fill", "#password", "x"));
        Assert.Equal("v", ExecutionValueRedactor.RedactStepValue("assertValue", "#password", "v"));
    }
}
