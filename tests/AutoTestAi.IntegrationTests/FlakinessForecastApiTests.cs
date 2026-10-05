using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase-4 Slice 1: flakiness risk forecast through the report API —
/// advisory values, auth matrix, isolation, sorting, CSV, dashboard
/// band counts. Deterministic InMemory EF; no workers, no AI.
/// </summary>
public sealed class FlakinessForecastApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    public FlakinessForecastApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            var (pa, pb) = (_projectA, _projectB);
            var now = DateTimeOffset.UtcNow;
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.Add(new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var viewer = new User { ExternalIdentityId = "fc-viewer", Email = "v@x", DisplayName = "V" };
                var outsider = new User { ExternalIdentityId = "fc-outsider", Email = "o@x", DisplayName = "O" };
                db.Users.AddRange(viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Forecast Alpha", Key = "FCA" },
                    new Project { Id = pb, Name = "Forecast Beta", Key = "FCB" });
                db.ProjectMembers.Add(
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                // Newest-first [F,F,P,F,P]: score 45 Medium, all factors.
                var forecast = new TestCase
                {
                    ProjectId = pa, TestKey = "FRC-001", Title = "Forecast me",
                    Priority = Priority.High, Status = TestCaseStatus.Active,
                    Framework = "playwright", SourceType = "manual",
                };
                // Single verdict: insufficient history (null forecast).
                var thin = new TestCase
                {
                    ProjectId = pa, TestKey = "FRC-002", Title = "Thin history",
                    Priority = Priority.Low, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var other = new TestCase
                {
                    ProjectId = pb, TestKey = "FRC-999", Title = "Other project",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.AddRange(forecast, thin, other);

                void AddRun(TestCase tc, Guid project, ExecutionStatus status, int daysAgo)
                {
                    var exec = new Execution
                    {
                        ProjectId = project, Status = status,
                        CreatedAt = now.AddDays(-daysAgo),
                        StartedAt = now.AddDays(-daysAgo),
                        CompletedAt = now.AddDays(-daysAgo),
                    };
                    db.Executions.Add(exec);
                    db.ExecutionTests.Add(new ExecutionTest
                    {
                        ExecutionId = exec.Id, TestCaseId = tc.Id,
                        Status = status == ExecutionStatus.Passed
                            ? ExecutionTestStatus.Passed : ExecutionTestStatus.Failed,
                        CreatedAt = exec.CreatedAt,
                    });
                }
                AddRun(forecast, pa, ExecutionStatus.Failed, 1);
                AddRun(forecast, pa, ExecutionStatus.Failed, 2);
                AddRun(forecast, pa, ExecutionStatus.Passed, 3);
                AddRun(forecast, pa, ExecutionStatus.Failed, 4);
                AddRun(forecast, pa, ExecutionStatus.Passed, 5);
                AddRun(thin, pa, ExecutionStatus.Failed, 1);
                AddRun(other, pb, ExecutionStatus.Passed, 1);
                return Task.CompletedTask;
            });
            _seeded = true;
        }
        finally { _seedLock.Release(); }
    }

    private HttpClient Client(string sub, string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private static string Window()
    {
        var to = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var from = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        return $"from={from}&to={to}";
    }

    private sealed record FlakyPage(IReadOnlyList<FlakyRow> Items, int TotalCount);
    private sealed record FlakyRow(
        Guid TestCaseId, string TestKey, int? RiskScore, string? RiskBand,
        IReadOnlyList<string>? RiskFactors);
    private sealed record OverviewPayload(
        int HighRiskTests, int MediumRiskTests, int LowRiskTests, int InsufficientHistoryTests);

    [Fact]
    public async Task Report_IncludesForecast_And_NullForThinHistory()
    {
        await SeedOnceAsync();
        var page = await Client("fc-viewer", ["viewer"]).GetFromJsonAsync<FlakyPage>(
            $"/api/v1/projects/{_projectA}/reports/flakiness?{Window()}&sort=testKey&pageSize=10");

        var forecast = Assert.Single(page!.Items, r => r.TestKey == "FRC-001");
        Assert.Equal(45, forecast.RiskScore);
        Assert.Equal("Medium", forecast.RiskBand);
        Assert.Equal(3, forecast.RiskFactors!.Count);

        var thin = Assert.Single(page.Items, r => r.TestKey == "FRC-002");
        Assert.Null(thin.RiskScore);
        Assert.Null(thin.RiskBand);
        Assert.True(thin.RiskFactors is null || thin.RiskFactors.Count == 0);
    }

    [Fact]
    public async Task Report_SortByRiskScore_NullsLast()
    {
        await SeedOnceAsync();
        var page = await Client("fc-viewer", ["viewer"]).GetFromJsonAsync<FlakyPage>(
            $"/api/v1/projects/{_projectA}/reports/flakiness?{Window()}&sort=riskScore&descending=true&pageSize=10");

        Assert.Equal("FRC-001", page!.Items[0].TestKey);
        Assert.Equal("FRC-002", page.Items[^1].TestKey);
    }

    [Fact]
    public async Task Report_AuthMatrix()
    {
        await SeedOnceAsync();
        var url = $"/api/v1/projects/{_projectA}/reports/flakiness?{Window()}";
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("fc-outsider", ["viewer"]).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Report_Isolation()
    {
        await SeedOnceAsync();
        var rows = await Client("fc-viewer", ["viewer"]).GetFromJsonAsync<FlakyPage>(
            $"/api/v1/projects/{_projectA}/reports/flakiness?{Window()}&pageSize=50");
        Assert.DoesNotContain(rows!.Items, r => r.TestKey == "FRC-999");
    }

    [Fact]
    public async Task ExportCsv_ContainsForecastColumns()
    {
        await SeedOnceAsync();
        var response = await Client("fc-viewer", ["viewer"]).GetAsync(
            $"/api/v1/projects/{_projectA}/reports/flakiness/export?{Window()}");
        Assert.True(response.IsSuccessStatusCode);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("riskScore,riskBand,riskFactors", csv);
        Assert.Contains(",45,Medium,", csv);
    }

    [Fact]
    public async Task Overview_ReportsBandCounts()
    {
        await SeedOnceAsync();
        var dto = await Client("fc-viewer", ["viewer"]).GetFromJsonAsync<OverviewPayload>(
            $"/api/v1/projects/{_projectA}/dashboard/executive-overview?{Window()}");
        Assert.NotNull(dto);
        Assert.Equal(0, dto!.HighRiskTests);
        Assert.Equal(1, dto.MediumRiskTests);
        Assert.Equal(0, dto.LowRiskTests);
        Assert.Equal(1, dto.InsufficientHistoryTests);
    }
}
