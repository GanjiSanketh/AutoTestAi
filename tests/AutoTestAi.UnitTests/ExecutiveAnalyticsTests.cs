using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Reports;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Phase 2 Slice 12: deterministic analytics formulas, report shaping, CSV
/// export, and service composition. No AI, no live infrastructure.
/// </summary>
public sealed class ExecutiveAnalyticsTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    private static ReportDateRange Range()
        => new(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    private static StubCurrentUser Member() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "user-1",
        Roles = ["tester"], Permissions = RolePermissions.Resolve(["tester"]),
    };

    private static (DashboardService Service, FakeReportQueryStore Store) Dashboards(ICurrentUserService user)
    {
        var store = new FakeReportQueryStore();
        var memberships = new StubMembershipStore();
        memberships.Add(user.ExternalIdentityId!, ProjectA);
        return (new DashboardService(store, new AuthorizationService(user, memberships)), store);
    }

    private static (ReportService Service, FakeReportQueryStore Store) Reports(ICurrentUserService user)
    {
        var store = new FakeReportQueryStore();
        var memberships = new StubMembershipStore();
        memberships.Add(user.ExternalIdentityId!, ProjectA);
        return (new ReportService(store, new AuthorizationService(user, memberships)), store);
    }

    private static FlakyCandidate Candidate(
        string key = "T-001", int passed = 0, int failed = 0, int other = 0,
        string? lastRun = null, int healing = 0, int healed = 0,
        string module = "auth", string priority = "High", string? framework = "playwright")
        => new(Guid.NewGuid(), key, $"Title {key}", module, priority, framework, "web",
            passed, failed, other,
            lastRun is null ? null : new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero),
            healing, healed);

    // ---------- A.1-10 metric calculations ----------

    [Fact]
    public void PassRate_Empty_IsNull_NotZero()
        => Assert.Null(AnalyticsCalculations.PassRate(0, 0));

    [Fact]
    public void PassRate_AllPassed_IsOne()
        => Assert.Equal(1.0, AnalyticsCalculations.PassRate(5, 5));

    [Fact]
    public void Flakiness_ConsistentOutcomes_AreNotFlaky()
    {
        Assert.False(AnalyticsCalculations.IsFlaky(5, 0));
        Assert.False(AnalyticsCalculations.IsFlaky(0, 5));
        Assert.False(AnalyticsCalculations.IsFlaky(0, 0));
        Assert.True(AnalyticsCalculations.IsFlaky(3, 1));
    }

    [Fact]
    public void FlakinessIndex_SingleFailure_CannotExaggerate()
    {
        // No eligible tests: insufficient data (null, never zero).
        Assert.Null(AnalyticsCalculations.FlakinessIndex(0, 0));
        // A failed-only test is failure-prone, not flaky: contributes 0, not 100.
        Assert.Equal(0.0, AnalyticsCalculations.FlakinessIndex(0, 1));
        Assert.Equal(50.0, AnalyticsCalculations.FlakinessIndex(1, 2));
    }

    [Fact]
    public void TestFlakinessRate_UsesMinorityShare_NullsOnSmallSample()
    {
        Assert.Null(AnalyticsCalculations.TestFlakinessRate(1, 0));
        Assert.Equal(25.0, AnalyticsCalculations.TestFlakinessRate(3, 1));
        Assert.Equal(50.0, AnalyticsCalculations.TestFlakinessRate(2, 2));
    }

    [Fact]
    public void Coverage_Empty_IsNull()
    {
        Assert.Null(AnalyticsCalculations.AutomationCoverage(0, 0));
        Assert.Equal(50.0, AnalyticsCalculations.AutomationCoverage(1, 2));
    }

    [Fact]
    public void HealingSuccessRate_Empty_IsNull()
    {
        Assert.Null(AnalyticsCalculations.HealingSuccessRate(0, 0));
        Assert.Equal(75.0, AnalyticsCalculations.HealingSuccessRate(3, 4));
    }

    [Fact]
    public void DefectDensity_Empty_IsNull()
    {
        Assert.Null(AnalyticsCalculations.DefectsPer100Executions(3, 0));
        Assert.Equal(150.0, AnalyticsCalculations.DefectsPer100Executions(3, 2));
    }

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        var sorted = new List<long> { 10, 20, 30, 40 };
        Assert.Equal(20, AnalyticsCalculations.Percentile(sorted, 50));
        Assert.Equal(40, AnalyticsCalculations.Percentile(sorted, 90));
        Assert.Null(AnalyticsCalculations.Percentile(Array.Empty<long>(), 50));
    }

    [Fact]
    public void Readiness_InsufficientData_WhenNoTerminal()
    {
        var (score, status, _) = AnalyticsCalculations.ReleaseReadiness(
            null, null, null, 0, null, 0);
        Assert.Null(score);
        Assert.Equal("InsufficientData", status);
    }

    [Fact]
    public void Readiness_Weights_And_Bands_AreTransparent()
    {
        var (score, status, components) = AnalyticsCalculations.ReleaseReadiness(
            0.9, 5.0, 80.0, 0, 1.0, 20);
        Assert.NotNull(score);
        Assert.Equal("Ready", status);
        Assert.Equal(100, components.Sum(c => c.Weight));
        // Flakiness unknown: weights renormalize over the known components.
        var (partial, _, partialComponents) = AnalyticsCalculations.ReleaseReadiness(
            0.9, null, 80.0, 1, 1.0, 20);
        Assert.NotNull(partial);
        Assert.DoesNotContain(partialComponents, c => c.Component == "FlakinessHealth");
        // Open defects penalize deterministically.
        var (_, _, withDefects) = AnalyticsCalculations.ReleaseReadiness(1.0, 0.0, 100.0, 2, 1.0, 10);
        Assert.Equal(50.0, withDefects.First(c => c.Component == "DefectHealth").Value);
    }

    // ---------- shaping / sorting / CSV ----------

    [Fact]
    public void Shaper_Filters_BySearch_FlakyOnly_MinExecutions_HealedOnly()
    {
        var rows = new[]
        {
            Candidate("LOGIN-001", passed: 3, failed: 1),
            Candidate("LOGIN-002", passed: 0, failed: 4),
            Candidate("CHECKOUT-001", passed: 2, failed: 2, healing: 2, healed: 1),
        };
        var filters = new FlakyTestsFilters("login", true, 2, null, null, null, false);
        var result = FlakyReportShaper.ApplyFilters(rows, filters);
        Assert.Single(result);
        Assert.Equal("LOGIN-001", result[0].TestKey);

        var healed = FlakyReportShaper.ApplyFilters(
            rows, new FlakyTestsFilters(null, false, 0, null, null, null, true));
        Assert.Single(healed);
    }

    [Fact]
    public void Shaper_Sort_IsDeterministic_NullsLast()
    {
        var rows = new[]
        {
            Candidate("B", passed: 1, failed: 0),
            Candidate("A", passed: 3, failed: 1),
            Candidate("C", passed: 2, failed: 2),
        };
        var sorted = FlakyReportShaper.ApplySort(rows, "flakinessRate", descending: true);
        Assert.Equal(new[] { "C", "A", "B" }, sorted.Select(c => c.TestKey).ToArray());
        var byKey = FlakyReportShaper.ApplySort(rows, "testKey", descending: false);
        Assert.Equal(new[] { "A", "B", "C" }, byKey.Select(c => c.TestKey).ToArray());
    }

    [Fact]
    public void CsvExporter_Quotes_Delimiters_And_OmitsSecrets()
    {
        var rows = new[]
        {
            new FlakyTestDto(Guid.NewGuid(), "K,1", "Title \"quoted\"", "m", "High", "pw", "web",
                4, 3, 1, 0, true, 25.0, "Failed",
                new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), 1, 1),
        };
        var text = System.Text.Encoding.UTF8.GetString(CsvExporter.ExportFlakyTests(rows));
        Assert.Contains("testKey,title,module", text);
        Assert.Contains("\"K,1\",\"Title \"\"quoted\"\"\"", text);
        Assert.DoesNotContain("SecretReference", text);
    }

    [Fact]
    public void TrendBuilder_Distinguishes_NoData_From_Zero()
    {
        var rows = new List<TestDayOutcomeRow>
        {
            new(Guid.NewGuid(), 2026, 9, 27, "Passed", 2),
            new(Guid.NewGuid(), 2026, 9, 27, "Failed", 1),
        };
        var range = new ReportDateRange(
            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        var daily = FlakinessTrendBuilder.BuildDaily(range, rows);
        Assert.Equal(2, daily.Count);
        Assert.NotNull(daily[0].Index);
        Assert.Null(daily[1].Index);
        Assert.Equal(0, daily[1].EligibleTests);
    }

    // ---------- service composition ----------

    [Fact]
    public async Task Overview_EmptyProject_ReturnsNulls_NotZeroes()
    {
        var (service, _) = Dashboards(Member());
        var dto = await service.GetExecutiveOverviewAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Null(dto.PassRate);
        Assert.Null(dto.FlakinessIndex);
        Assert.Equal("InsufficientData", dto.ReadinessStatus);
        Assert.Null(dto.ReleaseReadiness);
        Assert.Null(dto.HealingSuccessRate);
        Assert.Null(dto.AverageDurationMs);
        Assert.Equal(0, dto.HighRiskTests);
        Assert.Equal(0, dto.InsufficientHistoryTests);
    }

    [Fact]
    public async Task Overview_CountsRiskBands_FromVerdictHistory()
    {
        var (service, store) = Dashboards(Member());
        var high = Guid.NewGuid();
        var low = Guid.NewGuid();
        var thin = Guid.NewGuid();
        store.OutcomeRows.Add(new TestOutcomeRow(high, 0, 4, 0, Day(28)));
        store.OutcomeRows.Add(new TestOutcomeRow(low, 4, 0, 0, Day(28)));
        store.OutcomeRows.Add(new TestOutcomeRow(thin, 0, 1, 0, Day(28)));
        var at = Day(28);
        foreach (var pass in new[] { false, false, false, false })
        {
            store.VerdictRows.Add(new TestVerdictRow(high, pass, at));
            at = at.AddDays(-1);
        }
        at = Day(28);
        foreach (var pass in new[] { true, true, true, true })
        {
            store.VerdictRows.Add(new TestVerdictRow(low, pass, at));
            at = at.AddDays(-1);
        }
        store.VerdictRows.Add(new TestVerdictRow(thin, false, Day(28)));

        var dto = await service.GetExecutiveOverviewAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(1, dto.HighRiskTests);
        Assert.Equal(0, dto.MediumRiskTests);
        Assert.Equal(1, dto.LowRiskTests);
        Assert.Equal(1, dto.InsufficientHistoryTests);
    }

    private static DateTimeOffset Day(int day)
        => new(2026, 9, day, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Overview_Composes_RealAggregates()
    {
        var (service, store) = Dashboards(Member());
        store.ExecutionsValue = new ExecutionKpis(10, 6, 2, 1, 1, 0, 0, 0.6);
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        store.OutcomeRows.Add(new TestOutcomeRow(t1, 3, 1, 0,
            new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)));
        store.OutcomeRows.Add(new TestOutcomeRow(t2, 4, 0, 0,
            new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)));
        store.CoverageValue = new CoverageCounts(4, 3);
        store.OpenCritHighValue = 1;
        store.DefectsCreatedValue = 2;
        store.DurationStatsValue = new DurationStats(8, 1200.0, 100, 5000, 9600);
        store.HealingStatsValue = new HealingStats(4, 3, 3, 1, 2, 3);

        var dto = await service.GetExecutiveOverviewAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(0.6, dto.PassRate);
        Assert.Equal(50.0, dto.FlakinessIndex);
        Assert.Equal(75.0, dto.AutomationCoverage);
        Assert.Equal(75.0, dto.HealingSuccessRate);
        Assert.Equal(20.0, dto.DefectsPer100Executions);
        Assert.NotNull(dto.ReleaseReadiness);
        Assert.Contains(dto.ReadinessComponents, c => c.Component == "DefectHealth");
    }

    [Fact]
    public async Task Duration_Exposes_Percentiles_And_Aging()
    {
        var (service, store) = Dashboards(Member());
        store.DurationStatsValue = new DurationStats(4, 25.0, 10, 40, 100);
        store.DurationsCapped.AddRange(new[] { 10L, 20L, 30L, 40L });
        store.AgingBuckets.Add(new NamedCount("0-7 days", 2));
        var dto = await service.GetDurationAnalyticsAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(20, dto.P50Ms);
        Assert.Equal(40, dto.P90Ms);
        Assert.False(dto.SlaConfigured);
        Assert.Single(dto.OpenDefectAging);
    }

    [Fact]
    public async Task Healing_Correlates_Neutrally()
    {
        var (service, store) = Dashboards(Member());
        var t1 = Guid.NewGuid();
        store.OutcomeRows.Add(new TestOutcomeRow(t1, 2, 2, 0, null));
        store.HealingRows.Add(new TestHealingRow(t1, 3, 2));
        store.HealingStatsValue = new HealingStats(3, 2, 2, 1, 1, 2);
        var dto = await service.GetHealingAnalyticsAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(1, dto.TestsHealedAndFlaky);
        Assert.Equal(1, dto.AiAssisted);
    }

    [Fact]
    public async Task FlakyReport_Paginates_Sorts_And_Validates()
    {
        var (service, store) = Reports(Member());
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        store.OutcomeRows.Add(new TestOutcomeRow(t1, 3, 1, 0, null));
        store.OutcomeRows.Add(new TestOutcomeRow(t2, 0, 5, 0, null));
        store.MetaRows.Add(new TestCaseMetaRow(t1, "B-001", "B", null, "High", "playwright", "web"));
        store.MetaRows.Add(new TestCaseMetaRow(t2, "A-001", "A", null, "High", "playwright", "web"));
        store.LastRuns.Add(new TestLastRunRow(t1, Guid.NewGuid(), "Failed",
            new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)));

        var page = await service.GetFlakyTestsAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            "testKey", false, 1, 1, CancellationToken.None);
        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal("A-001", page.Items[0].TestKey);

        await Assert.ThrowsAsync<ValidationException>(() => service.GetFlakyTestsAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            "drop-table", false, 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Export_RespectsFilters_And_IsBounded()
    {
        var (service, store) = Reports(Member());
        var t1 = Guid.NewGuid();
        store.OutcomeRows.Add(new TestOutcomeRow(t1, 2, 2, 0, null));
        store.MetaRows.Add(new TestCaseMetaRow(t1, "K-001", "K", null, "Medium", null, null));
        var export = await service.ExportFlakyTestsCsvAsync(ProjectA, Range(),
            new FlakyTestsFilters("k-001", false, 0, null, null, null, false),
            CancellationToken.None);
        Assert.EndsWith(".csv", export.FileName);
        Assert.Equal("text/csv", export.ContentType);
        Assert.Contains("K-001", System.Text.Encoding.UTF8.GetString(export.Content));
    }

    [Fact]
    public async Task Analytics_Require_Membership()
    {
        var store = new FakeReportQueryStore();
        var memberships = new StubMembershipStore();
        var user = Member();
        var dashboard = new DashboardService(store, new AuthorizationService(user, memberships));
        var reports = new ReportService(store, new AuthorizationService(user, memberships));
        await Assert.ThrowsAsync<ForbiddenException>(
            () => dashboard.GetExecutiveOverviewAsync(ProjectA, Range(), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(
            () => reports.GetFlakyTestsAsync(ProjectA, Range(),
                new FlakyTestsFilters(null, false, 0, null, null, null, false),
                null, false, 1, 25, CancellationToken.None));
    }
}
