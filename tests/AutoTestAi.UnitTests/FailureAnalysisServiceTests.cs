using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.FailureAnalysis;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 6 §54: analysis lifecycle, concurrency guard, redaction,
/// validation, cancellation, retry-as-new-attempt, audit, and authority boundaries.</summary>
public sealed class FailureAnalysisServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    // ---------- fakes ----------

    private sealed class FakeStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<ExecutionStepResult> Steps = new();
        public readonly List<ExecutionLog> Logs = new();
        public readonly List<ExecutionArtifact> Artifacts = new();
        public readonly List<FailureAnalysis> Analyses = new();
        private long _logId;

        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.Id == id));
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Tests.Where(t => t.ExecutionId == id).ToList());
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionStepResult>>(Steps.Where(s => s.ExecutionTestId == id).ToList());
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? afterId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionLog>>(Logs.Where(l => l.ExecutionTestId == id).ToList());
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Artifacts.Where(a => a.ExecutionTestId == id).ToList());
        public Task<IReadOnlyList<FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FailureAnalysis>>(Analyses.Where(a => a.ExecutionTestId == id).ToList());
        public Task<FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Analyses.FirstOrDefault(a => a.Id == id));
        public Task AddAnalysisAsync(FailureAnalysis a, CancellationToken ct)
        {
            if (Analyses.Any(x => x.ExecutionTestId == a.ExecutionTestId && x.Status == AnalysisStatus.Running))
                throw new ConflictException("already running");
            Analyses.Add(a);
            return Task.CompletedTask;
        }
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int sk, int ta, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionAsync(Execution e, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) => throw new NotImplementedException();
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct)
        { foreach (var row in rows) { row.Id = ++_logId; Logs.Add(row); } return Task.CompletedTask; }
        public Task DeleteLogsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeProvider : IAiProvider
    {
        public string Name => "stub";
        public AiFailureAnalysisRequest? SeenRequest { get; private set; }
        public AiAnalysisResult? Result { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public Task<AiGenerationResult> GenerateTestAsync(AiGenerationRequest request, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AiAnalysisResult> AnalyzeFailureAsync(AiFailureAnalysisRequest request, CancellationToken ct)
        {
            Calls++;
            SeenRequest = request;
            ct.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Task.FromResult(Result!);
        }
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        public IAiProvider Provider { get; set; } = null!;
        public Exception? Failure { get; set; }
        public IAiProvider Resolve()
        {
            if (Failure is not null) throw Failure;
            return Provider;
        }
        public AiProviderStatus GetStatus()
            => new(Provider?.Name ?? "stub", null, true, null, AiPromptVersions.FailureAnalysisV1);
    }

    private sealed record PublishedEvent(string EventName);

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        public readonly List<PublishedEvent> Events = new();
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
        {
            Events.Add(new PublishedEvent(eventName));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public readonly List<string?> Metadata = new();
        public Task RecordAsync(string a, string e, string? id, Guid? p, string? m, CancellationToken ct)
        { Actions.Add(a); Metadata.Add(m); return Task.CompletedTask; }
    }

    private sealed class FakeAuthorization : IAuthorizationService
    {
        public bool Allowed { get; set; } = true;
        public bool HasPermission(string permission) => Allowed;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(Allowed);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct)
            => Allowed ? Task.CompletedTask : throw new ForbiddenException("denied");
    }

    // ---------- builders ----------

    private sealed record Harness(
        FailureAnalysisService Service, FakeStore Store, FakeProvider Provider,
        FakePublisher Events, FakeAudit Audit, Guid ExecutionId, Guid TestId);

    private static AiAnalysisResult ValidAnalysisResult() => new(
        Provider: "stub", Model: "stub-1.0", Classification: "TestFailure",
        RootCause: "The heading text did not match.", Confidence: 0.75m,
        Summary: "Heading assertion failed.",
        Evidence: new[] { "step 2 failed" },
        Assumptions: Array.Empty<string>(), Warnings: Array.Empty<string>(),
        RecommendedAction: "Inspect the selector.", IsLikelyDefect: false,
        PromptVersion: AiPromptVersions.FailureAnalysisV1);

    private static Harness Create(ExecutionTestStatus status = ExecutionTestStatus.Failed)
    {
        var store = new FakeStore();
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Failed };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = Guid.NewGuid(),
            Status = status, FailureClassification = FailureClassification.TestFailure,
            ErrorType = "AssertionError", ErrorMessage = "Expected 'Welcome'.",
            Browser = "chromium", Framework = "playwright", Attempt = 1,
        };
        store.Executions.Add(execution);
        store.Tests.Add(test);
        store.Steps.Add(new ExecutionStepResult
        {
            ExecutionTestId = test.Id, StepOrder = 2, Action = "assertText",
            Target = "#heading", Status = ExecutionTestStatus.Failed, ErrorMessage = "mismatch",
        });
        store.Logs.Add(new ExecutionLog
        {
            ExecutionTestId = test.Id, Level = "error", Message = "step 2 failed",
        });

        var provider = new FakeProvider { Result = ValidAnalysisResult() };
        var events = new FakePublisher();
        var audit = new FakeAudit();
        var service = new FailureAnalysisService(
            store,
            new FailureEvidenceService(store, new FakeTestCaseStore(), Options.Create(new FailureAnalysisOptions())),
            new FakeResolver { Provider = provider },
            new AiAnalysisValidator(),
            new AiGenerationRateLimiter(Options.Create(new AiOptions()), new SystemDateTimeProvider()),
            Options.Create(new FailureAnalysisOptions()),
            new FakeAuthorization(), audit, events,
            new SystemDateTimeProvider());
        return new Harness(service, store, provider, events, audit, execution.Id, test.Id);
    }

    // ---------- lifecycle ----------

    [Fact]
    public async Task Analyze_Success_PersistsCompletedAttempt_WithMetadata()
    {
        var h = Create();

        var result = await h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None);

        Assert.Equal("Completed", result.Status);
        Assert.Equal("TestFailure", result.Classification);
        Assert.Equal(0.75m, result.Confidence);
        Assert.False(result.IsLikelyDefect);
        Assert.Equal("failure-analysis-v1", result.PromptVersion);
        Assert.Equal(1, result.Attempt);
        Assert.True(result.LatencyMs >= 0);
        Assert.Single(result.Evidence);

        var row = h.Store.Analyses.Single();
        Assert.Equal(AnalysisStatus.Completed, row.Status);
        Assert.Equal("stub", row.Provider);
        Assert.NotNull(row.Evidence);

        // Provider received bounded redacted context, never raw secrets.
        var seenRequest = h.Provider.SeenRequest!;
        Assert.NotNull(seenRequest.Context);
        var seen = seenRequest.Context!;
        Assert.Equal(h.TestId, seen.ExecutionTestId);
        Assert.Single(seen.FailedSteps);

        Assert.Contains(h.Audit.Actions, a => a == "failure-analysis.requested");
        Assert.Contains(h.Audit.Actions, a => a == "failure-analysis.completed");
        Assert.Contains(h.Events.Events, e => e.EventName == "FailureAnalysisCompleted");
        Assert.All(h.Audit.Metadata, m => Assert.DoesNotContain("sk-", m ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_NonFailedExecution_Returns409_WithoutProviderCall()
    {
        var h = Create(ExecutionTestStatus.Passed);
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None));
        Assert.Equal(0, h.Provider.Calls);
        Assert.Empty(h.Store.Analyses);
    }

    [Fact]
    public async Task Analyze_ConcurrentRunning_Returns409()
    {
        var h = Create();
        h.Store.Analyses.Add(new FailureAnalysis
        {
            ExecutionTestId = h.TestId, Attempt = 1, Status = AnalysisStatus.Running,
        });
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None));
        Assert.Equal(0, h.Provider.Calls);
    }

    [Fact]
    public async Task Analyze_RetryAfterFailure_CreatesNewAttempt()
    {
        var h = Create();
        h.Store.Analyses.Add(new FailureAnalysis
        {
            ExecutionTestId = h.TestId, Attempt = 1, Status = AnalysisStatus.Failed,
            Classification = FailureClassification.Unknown,
        });

        var result = await h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None);
        Assert.Equal(2, result.Attempt);
        Assert.Equal(2, h.Store.Analyses.Count); // history preserved
    }

    [Fact]
    public async Task Analyze_Forbidden_WhenNoAccess()
    {
        var store = new FakeStore();
        var auth = new FakeAuthorization { Allowed = false };
        var service = new FailureAnalysisService(
            store, new FailureEvidenceService(store, new FakeTestCaseStore(), Options.Create(new FailureAnalysisOptions())),
            new FakeResolver { Provider = new FakeProvider { Result = ValidAnalysisResult() } },
            new AiAnalysisValidator(),
            new AiGenerationRateLimiter(Options.Create(new AiOptions()), new SystemDateTimeProvider()),
            Options.Create(new FailureAnalysisOptions()),
            auth, new FakeAudit(), new FakePublisher(), new SystemDateTimeProvider());
        await Assert.ThrowsAsync<ForbiddenException>(
            () => service.AnalyzeAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Analyze_ProviderFailure_MarksAttemptFailed_AndRethrows()
    {
        var h = Create();
        h.Provider.Failure = AiProviderException.Unavailable("stub", "down");

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.Unavailable, ex.Kind);
        Assert.Equal(AnalysisStatus.Failed, h.Store.Analyses.Single().Status);
        Assert.Contains(h.Audit.Actions, a => a == "failure-analysis.failed");
    }

    [Fact]
    public async Task Analyze_MalformedResponse_MarksFailed_AndThrowsValidation()
    {
        var h = Create();
        h.Provider.Result = ValidAnalysisResult() with { Summary = "" };

        await Assert.ThrowsAsync<ValidationException>(
            () => h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None));
        Assert.Equal(AnalysisStatus.Failed, h.Store.Analyses.Single().Status);
    }

    [Fact]
    public async Task Analyze_Cancelled_MarksCancelled_AndRethrows()
    {
        var h = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Service.AnalyzeAsync(h.ExecutionId, cts.Token));
        Assert.Equal(AnalysisStatus.Cancelled, h.Store.Analyses.Single().Status);
        Assert.Contains(h.Audit.Actions, a => a == "failure-analysis.cancelled");
    }

    [Fact]
    public async Task Analyze_RateLimited_Surfaces429Kind()
    {
        var h = Create();
        h.Provider.Failure = AiProviderException.RateLimited("stub", "busy");
        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task GetLatest_Missing_Returns404_And_List_ReturnsHistory()
    {
        var h = Create();
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.GetLatestAsync(h.ExecutionId, CancellationToken.None));
        Assert.Empty(await h.Service.ListAttemptsAsync(h.ExecutionId, CancellationToken.None));

        var created = await h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None);
        var latest = await h.Service.GetLatestAsync(h.ExecutionId, CancellationToken.None);
        Assert.Equal(created.Id, latest.Id);
        Assert.Single(await h.Service.ListAttemptsAsync(h.ExecutionId, CancellationToken.None));
    }

    [Fact]
    public async Task Analyze_DoesNotTouchExecution_StatusOrClassification()
    {
        var h = Create();
        await h.Service.AnalyzeAsync(h.ExecutionId, CancellationToken.None);
        var execution = h.Store.Executions.First();
        var test = h.Store.Tests.First();
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal(FailureClassification.TestFailure, test.FailureClassification);
    }
}
