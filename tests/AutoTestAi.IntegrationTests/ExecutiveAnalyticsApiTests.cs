using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 2 Slice 12: executive analytics aggregates, flakiness report,
/// CSV export, isolation, and empty-project semantics. InMemory EF;
/// no live infrastructure.
/// </summary>
public sealed class ExecutiveAnalyticsApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    public ExecutiveAnalyticsApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            var (pa, pb) = (_projectA, _projectB);
            var now = DateTimeOffset.UtcNow;
            await _factory.SeedAsync(async db =>
            {
                if (await db.Projects.AnyAsync(p => p.Id == pa)) return;
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var manager = new User { ExternalIdentityId = "ea-manager", Email = "m@x", DisplayName = "M" };
                var viewer = new User { ExternalIdentityId = "ea-viewer", Email = "v@x", DisplayName = "V" };
                var outsider = new User { ExternalIdentityId = "ea-outsider", Email = "o@x", DisplayName = "O" };
                db.Users.AddRange(manager, viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Analytics Alpha", Key = "EAA" },
                    new Project { Id = pb, Name = "Analytics Beta", Key = "EAB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = manager.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                // --- test cases: 1 automated, 1 draft, 1 archived (eligible=2, automated=1) ---
                var auto = new TestCase { ProjectId = pa, TestKey = "FLK-001", Title = "Flaky login", Module = "auth", Priority = Priority.High, Status = TestCaseStatus.Active, Framework = "playwright", Platform = "web", SourceType = "manual" };
                var draft = new TestCase { ProjectId = pa, TestKey = "FLK-002", Title = "Failing checkout", Module = "shop", Priority = Priority.Critical, Status = TestCaseStatus.Active, Framework = "playwright", SourceType = "manual" };
                var archived = new TestCase { ProjectId = pa, TestKey = "FLK-003", Title = "Old test", Priority = Priority.Low, Status = TestCaseStatus.Archived, SourceType = "manual" };
                var other = new TestCase { ProjectId = pb, TestKey = "FLK-999", Title = "Other project", Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual" };
                db.TestCases.AddRange(auto, draft, archived, other);
                db.TestCaseVersions.AddRange(
                    new TestCaseVersion { TestCaseId = auto.Id, VersionNumber = 1, ReviewStatus = ReviewStatus.Approved, StructuredSteps = System.Text.Json.JsonDocument.Parse("""[{"order":1,"action":"click","target":"css=#a"}]""") },
                    new TestCaseVersion { TestCaseId = archived.Id, VersionNumber = 1, ReviewStatus = ReviewStatus.Approved, StructuredSteps = System.Text.Json.JsonDocument.Parse("""[{"order":1,"action":"click","target":"css=#o"}]""") },
                    new TestCaseVersion { TestCaseId = other.Id, VersionNumber = 1, ReviewStatus = ReviewStatus.Approved, StructuredSteps = System.Text.Json.JsonDocument.Parse("""[{"order":1,"action":"click","target":"css=#z"}]""") });

                // --- executions: auto = 2 passed + 1 failed (flaky); draft = 3 failed ---
                async Task AddRun(TestCase tc, Guid project, ExecutionStatus status, int daysAgo, long? durationMs)
                {
                    var exec = new Execution { ProjectId = project, Status = status, CreatedAt = now.AddDays(-daysAgo), StartedAt = now.AddDays(-daysAgo), CompletedAt = now.AddDays(-daysAgo) };
                    db.Executions.Add(exec);
                    await db.SaveChangesAsync();
                    db.ExecutionTests.Add(new ExecutionTest
                    {
                        ExecutionId = exec.Id, TestCaseId = tc.Id, Status = MapTest(status),
                        FailureClassification = status == ExecutionStatus.Passed ? FailureClassification.Unknown : FailureClassification.TestFailure,
                        DurationMs = durationMs, CreatedAt = exec.CreatedAt,
                    });
                }
                await AddRun(auto, pa, ExecutionStatus.Passed, 3, 100);
                await AddRun(auto, pa, ExecutionStatus.Passed, 2, 200);
                await AddRun(auto, pa, ExecutionStatus.Failed, 1, 300);
                await AddRun(draft, pa, ExecutionStatus.Failed, 2, 400);
                await AddRun(draft, pa, ExecutionStatus.Failed, 1, null);
                await AddRun(draft, pa, ExecutionStatus.Failed, 1, -5);
                await AddRun(other, pb, ExecutionStatus.Passed, 1, 50);

                db.Defects.Add(new Defect
                {
                    ProjectId = pa, Title = "Login broken", Severity = Severity.High,
                    Status = DefectStatus.Open, RootCauseType = FailureClassification.TestFailure,
                });
                db.SelfHealingAttempts.AddRange(
                    new SelfHealingAttempt
                    {
                        ProjectId = pa, ExecutionId = Guid.NewGuid(), ExecutionTestId = Guid.NewGuid(),
                        TestCaseId = auto.Id, StepOrder = 1, StepAction = "click",
                        HealingStrategy = SelfHealingStrategy.TestAttribute, Status = SelfHealingStatus.Applied,
                        CandidateCount = 1, WasApplied = true, IsAiAssisted = false, CreatedAt = now.AddDays(-1),
                    },
                    new SelfHealingAttempt
                    {
                        ProjectId = pa, ExecutionId = Guid.NewGuid(), ExecutionTestId = Guid.NewGuid(),
                        TestCaseId = auto.Id, StepOrder = 2, StepAction = "fill",
                        HealingStrategy = SelfHealingStrategy.Ai, Status = SelfHealingStatus.Applied,
                        CandidateCount = 2, WasApplied = true, IsAiAssisted = true, CreatedAt = now.AddDays(-1),
                    },
                    new SelfHealingAttempt
                    {
                        ProjectId = pa, ExecutionId = Guid.NewGuid(), ExecutionTestId = Guid.NewGuid(),
                        TestCaseId = draft.Id, StepOrder = 1, StepAction = "click",
                        HealingStrategy = SelfHealingStrategy.None, Status = SelfHealingStatus.Failed,
                        CandidateCount = 0, WasApplied = false, IsAiAssisted = false, CreatedAt = now.AddDays(-2),
                    });
                await db.SaveChangesAsync();
            });
            _seeded = true;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static ExecutionTestStatus MapTest(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Passed => ExecutionTestStatus.Passed,
        ExecutionStatus.Failed => ExecutionTestStatus.Failed,
        ExecutionStatus.Cancelled => ExecutionTestStatus.Cancelled,
        ExecutionStatus.TimedOut => ExecutionTestStatus.TimedOut,
        _ => ExecutionTestStatus.Error,
    };

    private HttpClient Client(string sub, string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private string Window()
    {
        var to = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var from = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        return $"from={from}&to={to}";
    }

    private sealed record OverviewPayload(
        Guid ProjectId, int TerminalExecutions, int TotalExecutions,
        double? PassRate, double? FailRate, double? FlakinessIndex,
        int FlakyTests, int EligibleTests, double? AutomationCoverage,
        int AutomatedCases, int EligibleCases, double? ReleaseReadiness,
        string ReadinessStatus, int OpenCriticalHighDefects,
        double? DefectsPer100Executions, double? AverageDurationMs,
        double? HealingSuccessRate, int HealingAttempts, int HealingApplied,
        int UnstableExecutions, int CancelledExecutions);
    private sealed record FlakyPage(IReadOnlyList<FlakyRow> Items, int TotalCount, int Page, int PageSize);
    private sealed record FlakyRow(
        Guid TestCaseId, string TestKey, string Title, int TotalExecutions,
        int Passed, int Failed, bool IsFlaky, double? FlakinessRate,
        string? LastOutcome, int HealingAttempts, int HealedRuns);
    private sealed record TrendPayload(IReadOnlyList<TrendPoint> Points);
    private sealed record TrendPoint(string Date, int EligibleTests, int FlakyTests, double? Index);
    private sealed record HealingPayload(
        int Attempts, int Applied, int Failed, int Deterministic, int AiAssisted,
        double? SuccessRate, int TestsWithHealing, int TestsHealedAndFlaky);
    private sealed record DurationPayload(
        int Count, double? AverageMs, long? MinMs, long? MaxMs,
        double? P50Ms, double? P90Ms, bool SlaConfigured);
    private sealed record ReadinessPayload(double? Score, string Status, int SampleSize);

    [Fact]
    public async Task Overview_Auth_And_Aggregates()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient()
            .GetAsync($"/api/v1/projects/{_projectA}/dashboard/executive-overview?{Window()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("ea-outsider", ["viewer"])
            .GetAsync($"/api/v1/projects/{_projectA}/dashboard/executive-overview?{Window()}")).StatusCode);

        var dto = await Client("ea-viewer", ["viewer"]).GetFromJsonAsync<OverviewPayload>(
            $"/api/v1/projects/{_projectA}/dashboard/executive-overview?{Window()}");
        Assert.NotNull(dto);
        Assert.Equal(6, dto!.TerminalExecutions);
        Assert.Equal(2.0 / 6, dto.PassRate!.Value, 5);
        Assert.Equal(50.0, dto.FlakinessIndex!.Value, 5);
        Assert.Equal(1, dto.FlakyTests);
        Assert.Equal(2, dto.EligibleTests);
        Assert.Equal(50.0, dto.AutomationCoverage!.Value, 5);
        Assert.Equal(1, dto.OpenCriticalHighDefects);
        Assert.NotNull(dto.ReleaseReadiness);
        Assert.Equal(200.0 / 3, dto.HealingSuccessRate!.Value, 5);
        Assert.Equal(250.0, dto.AverageDurationMs!.Value, 5);
        Assert.Equal(0, dto.UnstableExecutions);
    }

    [Fact]
    public async Task Overview_Isolation_ProjectB_Data_Stays_In_B()
    {
        await SeedOnceAsync();
        var dto = await Client("ea-viewer", ["viewer"]).GetFromJsonAsync<OverviewPayload>(
            $"/api/v1/projects/{_projectA}/dashboard/executive-overview?{Window()}");
        // Project B's passed execution must not leak into A's aggregates.
        Assert.Equal(6, dto!.TerminalExecutions);
    }

    [Fact]
    public async Task FlakinessReport_Filters_Sorts_Paginates()
    {
        await SeedOnceAsync();
        var viewer = Client("ea-viewer", ["viewer"]);
        var baseUrl = $"/api/v1/projects/{_projectA}/reports/flakiness?{Window()}";

        var all = await viewer.GetFromJsonAsync<FlakyPage>($"{baseUrl}&sort=testKey&pageSize=10");
        Assert.Equal(2, all!.TotalCount);

        var flakyOnly = await viewer.GetFromJsonAsync<FlakyPage>($"{baseUrl}&flakyOnly=true");
        Assert.Single(flakyOnly!.Items);
        Assert.Equal("FLK-001", flakyOnly.Items[0].TestKey);
        Assert.True(flakyOnly.Items[0].IsFlaky);

        var search = await viewer.GetFromJsonAsync<FlakyPage>($"{baseUrl}&search=checkout");
        Assert.Single(search!.Items);

        var minExec = await viewer.GetFromJsonAsync<FlakyPage>($"{baseUrl}&minExecutions=4");
        Assert.Equal(0, minExec!.TotalCount);

        var page2 = await viewer.GetFromJsonAsync<FlakyPage>($"{baseUrl}&sort=testKey&page=2&pageSize=1");
        Assert.Equal(2, page2!.TotalCount);
        Assert.Single(page2.Items);

        var badSort = await viewer.GetAsync($"{baseUrl}&sort=drop-table");
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);
    }

    [Fact]
    public async Task FlakinessReport_Isolation()
    {
        await SeedOnceAsync();
        var rows = await Client("ea-viewer", ["viewer"]).GetFromJsonAsync<FlakyPage>(
            $"/api/v1/projects/{_projectA}/reports/flakiness?{Window()}&pageSize=50");
        Assert.DoesNotContain(rows!.Items, r => r.TestKey == "FLK-999");
    }

    [Fact]
    public async Task ExportCsv_Auth_Content_And_Filters()
    {
        await SeedOnceAsync();
        var url = $"/api/v1/projects/{_projectA}/reports/flakiness/export?{Window()}";
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(url)).StatusCode);
        var response = await Client("ea-viewer", ["viewer"]).GetAsync(url);
        Assert.True(response.IsSuccessStatusCode);
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("testKey,title,", csv);
        Assert.Contains("FLK-001", csv);
        Assert.DoesNotContain("SecretReference", csv);

        var filtered = await Client("ea-viewer", ["viewer"])
            .GetAsync($"{url}&flakyOnly=true");
        var filteredCsv = await filtered.Content.ReadAsStringAsync();
        Assert.Contains("FLK-001", filteredCsv);
        Assert.DoesNotContain("FLK-002", filteredCsv);
    }

    [Fact]
    public async Task Trend_Healing_Duration_Readiness_Shape()
    {
        await SeedOnceAsync();
        var viewer = Client("ea-viewer", ["viewer"]);
        var trend = await viewer.GetFromJsonAsync<TrendPayload>(
            $"/api/v1/projects/{_projectA}/dashboard/flakiness-trend?{Window()}");
        Assert.NotNull(trend);
        Assert.True(trend!.Points.Count > 0);
        Assert.Contains(trend.Points, p => p.Index.HasValue);

        var healing = await viewer.GetFromJsonAsync<HealingPayload>(
            $"/api/v1/projects/{_projectA}/dashboard/healing?{Window()}");
        Assert.Equal(3, healing!.Attempts);
        Assert.Equal(2, healing.Applied);
        Assert.Equal(1, healing.AiAssisted);
        Assert.Equal(1, healing.TestsHealedAndFlaky);

        var durations = await viewer.GetFromJsonAsync<DurationPayload>(
            $"/api/v1/projects/{_projectA}/dashboard/durations?{Window()}");
        Assert.Equal(4, durations!.Count);
        Assert.Equal(200, durations.P50Ms);
        Assert.Equal(400, durations.P90Ms);
        Assert.False(durations.SlaConfigured);

        var readiness = await viewer.GetFromJsonAsync<ReadinessPayload>(
            $"/api/v1/projects/{_projectA}/dashboard/readiness?{Window()}");
        Assert.NotNull(readiness!.Score);
        Assert.Equal(6, readiness.SampleSize);
    }

    [Fact]
    public async Task SingleExecutionProject_Returns_Null_Flakiness_Not_Zero()
    {
        await SeedOnceAsync();
        // Project B has one passing execution: pass rate exists, but flakiness
        // is insufficient data (null) rather than a misleading zero.
        var dto = await Client("ea-manager", ["admin"]).GetFromJsonAsync<OverviewPayload>(
            $"/api/v1/projects/{_projectB}/dashboard/executive-overview?{Window()}");
        Assert.NotNull(dto);
        Assert.Equal(1.0, dto!.PassRate!.Value, 5);
        Assert.Null(dto.FlakinessIndex);
        Assert.Equal(0, dto.EligibleTests);
    }

    [Fact]
    public async Task CrossProject_Member_Cannot_Read_Other_Project()
    {
        await SeedOnceAsync();
        var forbidden = await Client("ea-manager", ["qa-lead"])
            .GetAsync($"/api/v1/projects/{_projectB}/dashboard/executive-overview?{Window()}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var forbiddenReport = await Client("ea-manager", ["qa-lead"])
            .GetAsync($"/api/v1/projects/{_projectB}/reports/flakiness?{Window()}");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenReport.StatusCode);
    }
}
