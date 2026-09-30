using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3A: trusted resolution boundary, secret-bearing transport
/// control, and secret-aware redaction across SignalR/persist/Temporal outcome.</summary>
public sealed class Slice3AEngineSecurityTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid EnvironmentId = Guid.NewGuid();
    private const string SecretValue = "s3cr3t-value-xyz-999";
    private const string BaseUrl = "https://qa.example.com";

    // ---------- fakes ----------

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
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Array.Empty<ExecutionArtifact>());
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<ExecutionArtifact?>(null);
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct)
        { Artifacts.Add(a); return Task.CompletedTask; }
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct)
        { Artifacts.RemoveAll(a => a.ExecutionTestId == id); return Task.CompletedTask; }
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
        public TestCase? Case;
        public TestCaseVersion? Version;
        public Task<TestCase?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Case);
        public Task<TestCaseVersion?> GetVersionByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Version);
        public Task<int> CountAsync(Guid p, string? s, TestCaseStatusFilter f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TestCase>> ListAsync(Guid p, string? s, TestCaseStatusFilter f, int sk, int t, CancellationToken ct) => throw new NotImplementedException();
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
        public WorkerAssignmentDto? Captured;
        public Func<WorkerAssignmentDto, string> StartHandler = _ => "assign-1";
        public Func<string, WorkerAssignmentProgressDto>? ProgressHandler;
        public Task<string> StartAssignmentAsync(WorkerAssignmentDto assignment, CancellationToken ct)
        { Captured = assignment; return Task.FromResult(StartHandler(assignment)); }
        public Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string id, CancellationToken ct)
            => Task.FromResult(ProgressHandler!(id));
        public Task CancelAssignmentAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record PublishedEvent(string EventName, object Payload);

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        public readonly List<PublishedEvent> Events = new();
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
        { Events.Add(new PublishedEvent(eventName, payload)); return Task.CompletedTask; }
    }

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured => false;
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<(string Action, string? Metadata)> Records = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Records.Add((action, metadataJson)); return Task.CompletedTask; }
    }

    private sealed class FakeResolver : IVariableResolutionService
    {
        public Task<ResolvedVariables> ResolveForExecutionAsync(Guid p, Guid e, Guid? s, Guid x, CancellationToken ct)
            => Task.FromResult(new ResolvedVariables(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BASE_URL"] = BaseUrl,
                    ["API_TOKEN"] = SecretValue,
                    ["BROWSER"] = "chromium",
                },
                new List<string> { SecretValue },
                Array.Empty<string>(),
                new HashSet<string>(StringComparer.Ordinal) { "API_TOKEN" }));
    }

    private sealed class FakeEnvelopes : IExecutionVariablesStore
    {
        public Task<ExecutionVariables?> GetByExecutionAsync(Guid executionId, CancellationToken ct)
            => Task.FromResult<ExecutionVariables?>(null);
        public Task SaveAsync(ExecutionVariables envelope, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => true;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct) => Task.CompletedTask;
    }

    private static ExecutionEngine BuildEngine(
        FakeStore store, FakeCases cases, FakeWorker worker, FakePublisher publisher,
        FakeAudit audit, IVariableResolutionService? resolver = null)
        => new(store, cases, worker, publisher, new FakeArtifacts(),
            Options.Create(new ExecutionOptions
            {
                DefaultExecutionTimeoutSeconds = 300,
                DefaultStepTimeoutSeconds = 10,
                WorkerPollIntervalSeconds = 0,
            }),
            new SystemDateTimeProvider(), audit, NullLogger<ExecutionEngine>.Instance,
            null, null, null, null,
            resolver ?? new FakeResolver(), new FakeEnvelopes());

    private static (FakeStore Store, FakeCases Cases, Execution Execution, ExecutionTest Test) ArrangeExecution(
        string stepsJson)
    {
        var store = new FakeStore();
        var cases = new FakeCases();
        var testCaseId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        cases.Case = new TestCase
        {
            Id = testCaseId, ProjectId = ProjectId, TestKey = "K-1",
            Title = "T", Status = TestCaseStatus.Active,
        };
        cases.Version = new TestCaseVersion
        {
            Id = versionId, TestCaseId = testCaseId, VersionNumber = 1,
            StructuredSteps = JsonDocument.Parse(stepsJson),
            ReviewStatus = ReviewStatus.Approved,
        };
        var execution = new Execution
        {
            ProjectId = ProjectId, EnvironmentId = EnvironmentId,
            Status = ExecutionStatus.Running, TriggerType = TriggerType.Manual,
        };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = testCaseId,
            TestCaseVersionId = versionId, Status = ExecutionTestStatus.Running,
            Framework = "playwright", Browser = "chromium", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        return (store, cases, execution, test);
    }

    private static WorkerAssignmentProgressDto TerminalProgress(string errorEcho)
    {
        var result = new WorkerAssignmentResultDto(
            "assign-1", "failed", "test", "AssertionError", errorEcho, 100,
            new List<WorkerStepResultDto>
            {
                new(1, "assertValue", "#token", "failed", 1, 2, 1, errorEcho),
            },
            new List<WorkerLogDto>
            {
                new(1, 2, "Information", $"typing {SecretValue} into field"),
            },
            new List<WorkerScreenshotDto>(),
            new List<WorkerHealingAttemptDto>());
        return new WorkerAssignmentProgressDto("assign-1", "completed", 1,
            result.StepResults, result.Logs, result);
    }

    // ---------- tests ----------

    [Fact]
    public async Task RunWorker_SubstitutesVariables_AndRedactsOutcomeAndSignalR()
    {
        var (store, cases, execution, _) = ArrangeExecution(
            """[{"order":1,"action":"navigate","target":"${{ BASE_URL }}/login"},{"order":2,"action":"fill","target":"#token","value":"${{ API_TOKEN }}"}]""");
        var worker = new FakeWorker();
        var publisher = new FakePublisher();
        var audit = new FakeAudit();
        worker.ProgressHandler = _ => TerminalProgress($"Expected value '{SecretValue}' but found 'other'");
        var engine = BuildEngine(store, cases, worker, publisher, audit);

        var outcome = await engine.RunWorkerAsync(execution.Id, null, CancellationToken.None);

        // Trusted transport carries substituted plaintext (sole secret bearer).
        Assert.NotNull(worker.Captured);
        Assert.Contains("/login", worker.Captured!.TargetUrl);
        Assert.Equal(SecretValue, worker.Captured.Steps.First(s => s.Order == 2).Value);

        // Temporal-bound outcome is sanitized.
        Assert.DoesNotContain(SecretValue, outcome.ErrorMessage);
        Assert.DoesNotContain(SecretValue, outcome.Steps[0].ErrorMessage);
        Assert.DoesNotContain(SecretValue, outcome.Logs[0].Message);

        // Live SignalR parity: same redaction as persistence.
        var logEvents = publisher.Events.Where(e => e.EventName == ExecutionEvents.ExecutionLogReceived).ToList();
        Assert.NotEmpty(logEvents);
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(logEvents));

        // Persisted rows redacted.
        await engine.PersistResultAsync(execution.Id, outcome, CancellationToken.None);
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(store.Logs));
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(store.Steps));
        Assert.DoesNotContain(SecretValue, store.Tests[0].ErrorMessage);
    }

    [Fact]
    public async Task Prepare_MasksSecretValues_ForTemporalHistory()
    {
        var (store, cases, execution, test) = ArrangeExecution(
            """[{"order":1,"action":"fill","target":"#token","value":"${{ API_TOKEN }}"}]""");
        test.Status = ExecutionTestStatus.Queued;
        execution.Status = ExecutionStatus.Queued;
        var worker = new FakeWorker();
        var publisher = new FakePublisher();
        var engine = BuildEngine(store, cases, worker, publisher, new FakeAudit());

        var prepared = await engine.PrepareAsync(execution.Id, CancellationToken.None);

        Assert.True(prepared.CanRun);
        Assert.NotNull(prepared.Assignment);
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(prepared.Assignment));
    }

    [Fact]
    public async Task RunWorker_MissingVariable_FailsDeterministically()
    {
        var (store, cases, execution, _) = ArrangeExecution(
            """[{"order":1,"action":"navigate","target":"${{ NOPE }}/x"}]""");
        var engine = BuildEngine(store, cases, new FakeWorker(), new FakePublisher(), new FakeAudit());

        var outcome = await engine.RunWorkerAsync(execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionTestStatus.Error, outcome.Status);
        Assert.Contains("NOPE", outcome.ErrorMessage);
    }

    [Fact]
    public async Task LegacyExecution_WithoutEnvironment_RunsLiterally()
    {
        var (store, cases, execution, _) = ArrangeExecution(
            """[{"order":1,"action":"navigate","target":"https://example.test"}]""");
        execution.EnvironmentId = null;
        var worker = new FakeWorker();
        worker.ProgressHandler = _ => TerminalProgress("plain failure");
        var engine = BuildEngine(store, cases, worker, new FakePublisher(), new FakeAudit());

        var outcome = await engine.RunWorkerAsync(execution.Id, null, CancellationToken.None);

        Assert.NotNull(worker.Captured);
        Assert.Equal("https://example.test", worker.Captured!.TargetUrl);
        Assert.Equal(ExecutionTestStatus.Failed, outcome.Status);
    }

    [Fact]
    public void ExecutionComponents_DoNotDependOnSecretStore()
    {
        foreach (var type in new[] { typeof(ExecutionEngine), typeof(TestExecutionService) })
            foreach (var ctor in type.GetConstructors())
                Assert.DoesNotContain(ctor.GetParameters(),
                    p => p.ParameterType == typeof(ISecretStore));
    }

    [Fact]
    public void ExecutionComponents_CanResolveSecretsOnly()
    {
        var engineResolver = typeof(ExecutionEngine).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(IVariableResolutionService));
        Assert.True(engineResolver);
    }
}
