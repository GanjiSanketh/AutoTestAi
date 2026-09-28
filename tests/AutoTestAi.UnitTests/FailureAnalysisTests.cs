using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.FailureAnalysis;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Defects;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 6 §53: validator, prompt builder, evidence bounds/redaction,
/// defect transitions, and stub determinism.</summary>
public sealed class FailureAnalysisTests
{
    private static AiAnalysisResult ValidResult() => new(
        Provider: "stub",
        Model: "stub-1.0",
        Classification: "TestFailure",
        RootCause: "The heading text did not match.",
        Confidence: 0.75m,
        Summary: "Heading assertion failed.",
        Evidence: new[] { "step 2 assertText failed" },
        Assumptions: new[] { "Login page under test." },
        Warnings: Array.Empty<string>(),
        RecommendedAction: "Inspect the heading selector.",
        IsLikelyDefect: false,
        PromptVersion: AiPromptVersions.FailureAnalysisV1);

    // ---------- validator ----------

    [Fact]
    public void Validator_AcceptsWellFormedResult()
    {
        Assert.Empty(new AiAnalysisValidator().Validate(ValidResult()));
    }

    [Fact]
    public void Validator_RejectsMissingFields_BadClassification_AndBadConfidence()
    {
        var validator = new AiAnalysisValidator();
        var bad = ValidResult() with
        {
            Classification = "DefinitelyABug",
            Summary = "  ",
            RootCause = "",
            Confidence = 2m,
        };
        var errors = validator.Validate(bad);
        Assert.Contains(errors, e => e.Field == "classification");
        Assert.Contains(errors, e => e.Field == "summary");
        Assert.Contains(errors, e => e.Field == "probableCause");
        Assert.Contains(errors, e => e.Field == "confidence");
        Assert.Throws<ValidationException>(() => validator.ValidateOrThrow(bad));
    }

    [Fact]
    public void Validator_RejectsOversizedLists()
    {
        var validator = new AiAnalysisValidator();
        var bad = ValidResult() with
        {
            Assumptions = Enumerable.Repeat("x", 25).ToList(),
        };
        Assert.Contains(validator.Validate(bad), e => e.Field == "assumptions");
    }

    // ---------- prompt builder ----------

    [Fact]
    public void PromptBuilder_ContainsEvidenceConstraintsAndVersion()
    {
        var builder = new AiFailureAnalysisPromptBuilder();
        var prompt = builder.Build(new AiFailureAnalysisRequest(
            Guid.NewGuid(), "AssertionError", "Expected 'Welcome'.", "Login works",
            new AiFailureAnalysisContext(
                Guid.NewGuid(), Guid.NewGuid(), "LOGIN-001", "Login works",
                "playwright", "web", "chromium", "TestFailure",
                "step 2 (assertText) Failed.",
                new[] { new AiFailureEvidenceStep(2, "assertText", "#heading", "Failed", "mismatch") },
                new[] { new AiFailureEvidenceLog("error", "step 2 failed") },
                1, new[] { "step-2-failure.png" }, 1, false)));

        Assert.Equal(AiPromptVersions.FailureAnalysisV1, builder.PromptVersion);
        Assert.Contains("LOGIN-001", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("TestFailure", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("assertText", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("step 2 failed", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("step-2-failure.png", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("JSON", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("never execute", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("advisory", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromptBuilder_BareRequest_OmitsEvidenceSection()
    {
        var builder = new AiFailureAnalysisPromptBuilder();
        var prompt = builder.Build(new AiFailureAnalysisRequest(Guid.NewGuid(), "AssertionError", "m", "T"));
        Assert.Contains("AssertionError", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Equal("failure-analysis-v1", prompt.PromptVersion);
    }

    // ---------- evidence bounds ----------

    private sealed class EvidenceStore : IExecutionStore
    {
        public readonly List<ExecutionStepResult> Steps = new();
        public readonly List<ExecutionLog> Logs = new();
        public readonly List<ExecutionArtifact> Artifacts = new();
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionStepResult>>(Steps);
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? afterId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionLog>>(Logs);
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Artifacts);
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int sk, int ta, CancellationToken ct) => throw new NotImplementedException();
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionAsync(Execution e, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteLogsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAnalysisAsync(FailureAnalysis a, CancellationToken ct) => throw new NotImplementedException();
    }

    private static (FailureEvidenceService Service, EvidenceStore Store, FakeTestCaseStore Cases)
        CreateEvidence(FailureAnalysisOptions? options = null)
    {
        var store = new EvidenceStore();
        var cases = new FakeTestCaseStore();
        var testCase = new TestCase
        {
            ProjectId = Guid.NewGuid(), TestKey = "LOGIN-001", Title = "Login",
            Framework = "playwright", Platform = "web",
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        var service = new FailureEvidenceService(
            store, cases, Options.Create(options ?? new FailureAnalysisOptions()));
        return (service, store, cases);
    }

    private static (Execution Execution, ExecutionTest Test) FailedPair(Guid projectId, Guid caseId)
    {
        var execution = new Execution { ProjectId = projectId, Status = ExecutionStatus.Failed };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = caseId,
            Status = ExecutionTestStatus.Failed,
            FailureClassification = FailureClassification.TestFailure,
            ErrorType = "AssertionError",
            ErrorMessage = "Expected 'Welcome'.",
            Browser = "chromium", Framework = "playwright", Attempt = 1,
        };
        return (execution, test);
    }

    [Fact]
    public async Task Evidence_BoundsLogs_AndMarksTruncation()
    {
        var (service, store, cases) = CreateEvidence(new FailureAnalysisOptions
        {
            MaxLogLines = 3,
            MaxLogCharsPerMessage = 20,
            MaxTotalLogChars = 1000,
        });
        var caseId = cases.Cases.First().Id;
        var (execution, test) = FailedPair(cases.Cases.First().ProjectId, caseId);
        for (var i = 0; i < 10; i++)
            store.Logs.Add(new ExecutionLog { ExecutionTestId = test.Id, Level = "info", Message = $"line {i}" });

        var evidence = await service.BuildAsync(execution, test, CancellationToken.None);

        Assert.Equal(3, evidence.Logs.Count);
        Assert.True(evidence.Truncated);
        Assert.NotEmpty(evidence.TruncationNotes);
        Assert.Equal("line 9", evidence.Logs.Last().Message);
    }

    [Fact]
    public async Task Evidence_RedactsSecrets_AndBoundsError()
    {
        var (service, store, cases) = CreateEvidence(new FailureAnalysisOptions { MaxErrorChars = 20 });
        var testCase = cases.Cases.First();
        var (execution, test) = FailedPair(testCase.ProjectId, testCase.Id);
        test.ErrorMessage = """{"password": "supersecret-value", "detail": "mismatch"}""";
        store.Logs.Add(new ExecutionLog
        {
            ExecutionTestId = test.Id, Level = "info", Message = "Authorization: Bearer abcdefgh12345678",
        });

        var evidence = await service.BuildAsync(execution, test, CancellationToken.None);

        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("supersecret-value", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh12345678", serialized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", serialized, StringComparison.Ordinal);
        Assert.True(evidence.Truncated); // error exceeded 20 chars
    }

    // ---------- defect transitions ----------

    [Theory]
    [InlineData(DefectStatus.Open, DefectStatus.InProgress, true)]
    [InlineData(DefectStatus.Open, DefectStatus.Closed, true)]
    [InlineData(DefectStatus.InProgress, DefectStatus.Resolved, true)]
    [InlineData(DefectStatus.Resolved, DefectStatus.Open, true)]
    [InlineData(DefectStatus.Closed, DefectStatus.Open, true)]
    [InlineData(DefectStatus.Rejected, DefectStatus.Open, true)]
    [InlineData(DefectStatus.Closed, DefectStatus.Resolved, false)]
    [InlineData(DefectStatus.Resolved, DefectStatus.InProgress, false)]
    [InlineData(DefectStatus.Open, DefectStatus.Open, true)]
    public void Defect_Transitions(DefectStatus from, DefectStatus to, bool allowed)
        => Assert.Equal(allowed, DefectTransitions.IsValidTransition(from, to));

    // ---------- stub determinism ----------

    [Fact]
    public async Task Stub_BareRequest_RemainsUnknown()
    {
        var provider = new StubAiProvider(new SystemDateTimeProvider());
        var result = await provider.AnalyzeFailureAsync(
            new AiFailureAnalysisRequest(Guid.NewGuid(), "E", "m", "T"), CancellationToken.None);
        Assert.Equal("Unknown", result.Classification);
        Assert.Equal(0m, result.Confidence);
    }

    [Fact]
    public async Task Stub_ContextRequest_MirrorsClassification_Deterministically()
    {
        var provider = new StubAiProvider(new SystemDateTimeProvider());
        var context = new AiFailureAnalysisContext(
            Guid.NewGuid(), Guid.NewGuid(), "LOGIN-001", "Login", "playwright", "web",
            "chromium", "ApplicationDefect", "step 1 failed",
            Array.Empty<AiFailureEvidenceStep>(), Array.Empty<AiFailureEvidenceLog>(),
            0, Array.Empty<string>(), 1, false);
        var first = await provider.AnalyzeFailureAsync(
            new AiFailureAnalysisRequest(Guid.NewGuid(), "E", "m", "T", context), CancellationToken.None);
        var second = await provider.AnalyzeFailureAsync(
            new AiFailureAnalysisRequest(Guid.NewGuid(), "E", "m", "T", context), CancellationToken.None);
        Assert.Equal("ApplicationDefect", first.Classification);
        Assert.True(first.IsLikelyDefect);
        Assert.Equal("failure-analysis-v1", first.PromptVersion);
        Assert.Equal(
            JsonSerializer.Serialize(first with { LatencyMs = 0 }),
            JsonSerializer.Serialize(second with { LatencyMs = 0 }));
    }
}
