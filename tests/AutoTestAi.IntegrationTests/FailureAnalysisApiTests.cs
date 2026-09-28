using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 6: failure-analysis API — auth matrix, failed-only gate, attempt
/// lifecycle, concurrency guard, redaction, audit, and authority boundaries
/// (docs/06 §13). The stub provider backs analysis; provider failures are
/// covered with configuration overrides + unit tests.
/// </summary>
public sealed class FailureAnalysisApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    private Guid _failedExecutionA = Guid.Empty;
    private Guid _passedExecutionA = Guid.Empty;

    public FailureAnalysisApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var pa = _projectA;
            await _factory.SeedAsync(async db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }

                var mgr = new User { ExternalIdentityId = "fa-manager", Email = "m@x", DisplayName = "Manager" };
                var tester = new User { ExternalIdentityId = "fa-tester", Email = "t@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "fa-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "fa-outsider", Email = "o@x", DisplayName = "Outsider" };
                db.Users.AddRange(mgr, tester, viewer, outsider);
                db.Projects.Add(new Project { Id = pa, Name = "Analysis Alpha", Key = "FAA" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                var testCase = new TestCase
                {
                    ProjectId = pa, TestKey = "LOGIN-001", Title = "Login works",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(testCase);
                var version = new TestCaseVersion
                {
                    TestCaseId = testCase.Id, VersionNumber = 1, SourceCode = "// v1",
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                };
                db.TestCaseVersions.Add(version);

                var failed = new Execution
                {
                    ProjectId = pa, Status = ExecutionStatus.Failed,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    CompletedAt = DateTimeOffset.UtcNow,
                };
                var failedTest = new ExecutionTest
                {
                    ExecutionId = failed.Id, TestCaseId = testCase.Id,
                    TestCaseVersionId = version.Id, Status = ExecutionTestStatus.Failed,
                    Framework = "playwright", Browser = "chromium", Attempt = 1,
                    FailureClassification = FailureClassification.TestFailure,
                    ErrorType = "AssertionError", ErrorMessage = "Expected 'Welcome'.",
                    DurationMs = 4200,
                };
                db.Executions.Add(failed);
                db.ExecutionTests.Add(failedTest);
                db.ExecutionStepResults.Add(new ExecutionStepResult
                {
                    ExecutionTestId = failedTest.Id, StepOrder = 1, Action = "assertText",
                    Target = "#heading", Status = ExecutionTestStatus.Failed,
                    ErrorMessage = "login rejected password=hunter2-secret",
                });
                db.ExecutionLogs.Add(new ExecutionLog
                {
                    ExecutionTestId = failedTest.Id, Level = "error",
                    Message = """step failed {"api_key": "live-abc123"}""",
                });

                var passed = new Execution { ProjectId = pa, Status = ExecutionStatus.Passed };
                db.Executions.Add(passed);
                db.ExecutionTests.Add(new ExecutionTest
                {
                    ExecutionId = passed.Id, TestCaseId = testCase.Id,
                    TestCaseVersionId = version.Id, Status = ExecutionTestStatus.Passed,
                });

                await db.SaveChangesAsync();
                _failedExecutionA = failed.Id;
                _passedExecutionA = passed.Id;
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(string sub, string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private sealed record AnalysisPayload(
        Guid Id, Guid ExecutionId, int Attempt, string Status, string Classification,
        string? Summary, decimal? Confidence, bool IsLikelyDefect,
        string? Provider, string? PromptVersion, long? LatencyMs);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    [Fact]
    public async Task Analyze_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null)).StatusCode);
    }

    [Fact]
    public async Task Analyze_Outsider_AndViewerWithoutAnalyze_Return403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("fa-outsider", ["tester"]).PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null)).StatusCode);

        var viewer = await Client("fa-viewer", ["viewer"]).PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
    }

    [Fact]
    public async Task Analyze_PassedExecution_Returns409()
    {
        await SeedOnceAsync();
        var response = await Client("fa-tester", ["tester"]).PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_passedExecutionA}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Analyze_UnknownExecution_AsAdmin_Returns404()
    {
        await SeedOnceAsync();
        var response = await Client("fa-admin", ["admin"]).PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{Guid.NewGuid()}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Analyze_Success_PersistsAdvisoryAttempt()
    {
        await SeedOnceAsync();
        var response = await Client("fa-tester", ["tester"]).PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var analysis = await response.Content.ReadFromJsonAsync<AnalysisPayload>();
        Assert.NotNull(analysis);
        Assert.Equal("Completed", analysis!.Status);
        Assert.Equal("TestFailure", analysis.Classification);
        Assert.Equal(1, analysis.Attempt);
        Assert.Equal("stub", analysis.Provider);
        Assert.Equal("failure-analysis-v1", analysis.PromptVersion);
        Assert.True(analysis.LatencyMs >= 0);
        Assert.False(analysis.IsLikelyDefect);

        // Execution history untouched by advisory analysis.
        await _factory.SeedAsync(async db =>
        {
            var execution = await db.Executions.FirstAsync(e => e.Id == _failedExecutionA);
            Assert.Equal(ExecutionStatus.Failed, execution.Status);
            var test = await db.ExecutionTests.FirstAsync(t => t.ExecutionId == _failedExecutionA);
            Assert.Equal(FailureClassification.TestFailure, test.FailureClassification);
            var row = await db.FailureAnalyses.FirstAsync(a => a.Id == analysis.Id);
            Assert.Equal(AnalysisStatus.Completed, row.Status);
            Assert.Equal("stub", row.Provider);
            Assert.NotNull(row.Evidence);
            // Secrets from step errors/logs never survive the evidence boundary.
            var snapshot = row.Evidence!.RootElement.GetRawText();
            Assert.DoesNotContain("hunter2-secret", snapshot, StringComparison.Ordinal);
            Assert.DoesNotContain("live-abc123", snapshot, StringComparison.Ordinal);
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "failure-analysis.requested");
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "failure-analysis.completed");
            foreach (var audit in db.AuditEvents.Where(e => e.Action.StartsWith("failure-analysis", StringComparison.Ordinal)))
                Assert.DoesNotContain("sk-", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        });

        // Latest + attempts endpoints expose the persisted attempt.
        var latest = await Client("fa-viewer", ["viewer"]).GetFromJsonAsync<AnalysisPayload>(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis");
        Assert.Equal(analysis.Id, latest!.Id);
        var attempts = await Client("fa-viewer", ["viewer"]).GetFromJsonAsync<List<AnalysisPayload>>(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis/attempts");
        Assert.Single(attempts!);
    }

    [Fact]
    public async Task Analyze_Retry_CreatesNewAttempt_PreservingHistory()
    {
        await SeedOnceAsync();
        var client = Client("fa-tester", ["tester"]);
        var first = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var payload = await second.Content.ReadFromJsonAsync<AnalysisPayload>();
        Assert.Equal(2, payload!.Attempt);

        var attempts = await client.GetFromJsonAsync<List<AnalysisPayload>>(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis/attempts");
        Assert.Equal(2, attempts!.Count);
    }

    [Fact]
    public async Task Analyze_ConcurrentRunning_Returns409()
    {
        await SeedOnceAsync();
        await _factory.SeedAsync(async db =>
        {
            var test = await db.ExecutionTests.FirstAsync(t => t.ExecutionId == _failedExecutionA);
            db.FailureAnalyses.Add(new FailureAnalysis
            {
                ExecutionTestId = test.Id, Attempt = 1, Status = AnalysisStatus.Running,
            });
            await db.SaveChangesAsync();
        });
        var response = await Client("fa-tester", ["tester"]).PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Analyze_ProviderNotConfigured_Returns503()
    {
        await SeedOnceAsync();
        using var custom = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<AiOptions>(o =>
                {
                    o.Provider = "openai";
                    o.Model = "gpt-4o-mini";
                    o.ApiKey = "";
                })));
        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("fa-tester", ["tester"]));
        var response = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("PROVIDER_NOT_CONFIGURED", body?.Error.Code);
    }

    [Fact]
    public async Task Analyze_Response_ContainsNoSecretsOrRawPrompt()
    {
        await SeedOnceAsync();
        var response = await Client("fa-tester", ["tester"]).PostAsync(
            $"/api/v1/projects/{_projectA}/executions/{_failedExecutionA}/failure-analysis", null);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("hunter2-secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("live-abc123", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemPrompt", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLatest_Missing_Returns404()
    {
        await SeedOnceAsync();
        var response = await Client("fa-viewer", ["viewer"]).GetAsync(
            $"/api/v1/projects/{_projectA}/executions/{_passedExecutionA}/failure-analysis");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
