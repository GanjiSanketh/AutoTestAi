using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Reports;

namespace AutoTestAi.UnitTests;

internal sealed class FakeReportQueryStore : IReportQueryStore
{
    public TestCaseKpis TestCasesValue = new(0, 0);
    public ExecutionKpis ExecutionsValue = new(0, 0, 0, 0, 0, 0, 0, null);
    public DefectKpis DefectsValue = new(0, 0, 0, 0, 0, 0, 0);
    public TicketKpis TicketsValue = new(0, 0, 0, 0);
    public List<StatusDayCount> DayBuckets = new();
    public List<NamedCount> Classifications = new();
    public List<NamedCount> Severities = new();
    public List<NamedCount> DefectClasses = new();
    public List<NamedCount> Providers = new();
    public List<RecentExecutionItem> RecentExecutions = new();
    public List<RecentDefectItem> RecentDefects = new();
    public List<RecentTicketItem> RecentTickets = new();
    public List<ActivityItem> Activity = new();
    public List<TestOutcomeRow> OutcomeRows = new();
    public List<TestDayOutcomeRow> DayOutcomeRows = new();
    public List<TestHealingRow> HealingRows = new();
    public List<TestCaseMetaRow> MetaRows = new();
    public List<TestLastRunRow> LastRuns = new();
    public CoverageCounts CoverageValue = new(0, 0);
    public int OpenCritHighValue;
    public int DefectsCreatedValue;
    public List<NamedCount> AgingBuckets = new();
    public DurationStats DurationStatsValue = new(0, null, null, null, null);
    public List<long> DurationsCapped = new();
    public List<DurationDayRow> DurationDays = new();
    public HealingStats HealingStatsValue = new(0, 0, 0, 0, 0, 0);
    public List<HealingDayRow> HealingDays = new();
    public Func<Guid, ReportDateRange, ExecutionReportFilters, int, int, CancellationToken, Task<PagedResult<ExecutionReportItem>>>? ExecQuery;
    public Func<Guid, ReportDateRange, DefectReportFilters, int, int, CancellationToken, Task<PagedResult<DefectReportItem>>>? DefectQuery;
    public Func<Guid, ReportDateRange, TicketReportFilters, int, int, CancellationToken, Task<PagedResult<TicketReportItem>>>? TicketQuery;

    public Task<TestCaseKpis> GetTestCaseKpisAsync(Guid p, CancellationToken ct) => Task.FromResult(TestCasesValue);
    public Task<ExecutionKpis> GetExecutionKpisAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult(ExecutionsValue);
    public Task<DefectKpis> GetDefectKpisAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult(DefectsValue);
    public Task<TicketKpis> GetTicketKpisAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult(TicketsValue);
    public Task<IReadOnlyList<StatusDayCount>> GetExecutionStatusByDayAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult<IReadOnlyList<StatusDayCount>>(DayBuckets);
    public Task<IReadOnlyList<NamedCount>> GetFailureClassificationCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult<IReadOnlyList<NamedCount>>(Classifications);
    public Task<IReadOnlyList<NamedCount>> GetDefectSeverityCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult<IReadOnlyList<NamedCount>>(Severities);
    public Task<IReadOnlyList<NamedCount>> GetDefectClassificationCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult<IReadOnlyList<NamedCount>>(DefectClasses);
    public Task<IReadOnlyList<NamedCount>> GetTicketProviderCountsAsync(Guid p, ReportDateRange r, CancellationToken ct) => Task.FromResult<IReadOnlyList<NamedCount>>(Providers);
    public Task<IReadOnlyList<RecentExecutionItem>> GetRecentExecutionsAsync(Guid p, int t, CancellationToken ct) => Task.FromResult<IReadOnlyList<RecentExecutionItem>>(RecentExecutions.Take(t).ToList());
    public Task<IReadOnlyList<RecentDefectItem>> GetRecentDefectsAsync(Guid p, int t, CancellationToken ct) => Task.FromResult<IReadOnlyList<RecentDefectItem>>(RecentDefects.Take(t).ToList());
    public Task<IReadOnlyList<RecentTicketItem>> GetRecentTicketsAsync(Guid p, int t, CancellationToken ct) => Task.FromResult<IReadOnlyList<RecentTicketItem>>(RecentTickets.Take(t).ToList());
    public Task<IReadOnlyList<ActivityItem>> GetRecentActivityAsync(Guid p, ReportDateRange r, int t, CancellationToken ct) => Task.FromResult<IReadOnlyList<ActivityItem>>(Activity.Take(t).ToList());
    public Task<PagedResult<ExecutionReportItem>> QueryExecutionsAsync(Guid p, ReportDateRange r, ExecutionReportFilters f, int s, int t, CancellationToken ct)
        => ExecQuery is null ? Task.FromResult(new PagedResult<ExecutionReportItem>(Array.Empty<ExecutionReportItem>(), 0, 0, 0)) : ExecQuery(p, r, f, s, t, ct);
    public Task<PagedResult<DefectReportItem>> QueryDefectsAsync(Guid p, ReportDateRange r, DefectReportFilters f, int s, int t, CancellationToken ct)
        => DefectQuery is null ? Task.FromResult(new PagedResult<DefectReportItem>(Array.Empty<DefectReportItem>(), 0, 0, 0)) : DefectQuery(p, r, f, s, t, ct);
    public Task<PagedResult<TicketReportItem>> QueryTicketsAsync(Guid p, ReportDateRange r, TicketReportFilters f, int s, int t, CancellationToken ct)
        => TicketQuery is null ? Task.FromResult(new PagedResult<TicketReportItem>(Array.Empty<TicketReportItem>(), 0, 0, 0)) : TicketQuery(p, r, f, s, t, ct);
    public Task<IReadOnlyList<TestOutcomeRow>> GetTestOutcomeRowsAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestOutcomeRow>>(OutcomeRows);
    public Task<IReadOnlyList<TestDayOutcomeRow>> GetTestDayOutcomeRowsAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestDayOutcomeRow>>(DayOutcomeRows);
    public Task<IReadOnlyList<TestLastRunRow>> GetTestLastRunsAsync(Guid p, ReportDateRange r, IReadOnlyList<Guid> ids, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestLastRunRow>>(LastRuns.Where(x => ids.Contains(x.TestCaseId)).ToList());
    public Task<IReadOnlyList<TestHealingRow>> GetTestHealingRowsAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestHealingRow>>(HealingRows);
    public Task<CoverageCounts> GetCoverageCountsAsync(Guid p, CancellationToken ct)
        => Task.FromResult(CoverageValue);
    public Task<int> GetOpenCriticalHighDefectCountAsync(Guid p, CancellationToken ct)
        => Task.FromResult(OpenCritHighValue);
    public Task<int> GetDefectsCreatedCountAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult(DefectsCreatedValue);
    public Task<IReadOnlyList<NamedCount>> GetOpenDefectAgingAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<NamedCount>>(AgingBuckets);
    public Task<DurationStats> GetDurationStatsAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult(DurationStatsValue);
    public Task<IReadOnlyList<long>> GetDurationsCappedAsync(Guid p, ReportDateRange r, int cap, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<long>>(DurationsCapped.Take(cap).ToList());
    public Task<IReadOnlyList<DurationDayRow>> GetDurationByDayAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DurationDayRow>>(DurationDays);
    public Task<HealingStats> GetHealingStatsAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult(HealingStatsValue);
    public Task<IReadOnlyList<HealingDayRow>> GetHealingByDayAsync(Guid p, ReportDateRange r, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealingDayRow>>(HealingDays);
    public Task<IReadOnlyList<TestCaseMetaRow>> GetTestCaseMetaAsync(Guid p, IReadOnlyList<Guid> ids, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestCaseMetaRow>>(MetaRows.Where(m => ids.Contains(m.TestCaseId)).ToList());
}

internal sealed class FixedClock : IDateTimeProvider
{
    public DateTimeOffset Now { get; init; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow => Now;
}

public sealed class DashboardServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private static StubCurrentUser Member(string sub = "user-1") => new()
    {
        IsAuthenticated = true, ExternalIdentityId = sub,
        Roles = ["tester"], Permissions = RolePermissions.Resolve(["tester"]),
    };

    private static StubCurrentUser Viewer() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "viewer-1",
        Roles = ["viewer"], Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private static StubCurrentUser Admin() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "admin-1",
        Roles = ["admin"], Permissions = RolePermissions.Resolve(["admin"]),
    };

    private static (DashboardService Service, FakeReportQueryStore Store) CreateSimple(
        ICurrentUserService user, bool member = true)
    {
        var store = new FakeReportQueryStore();
        var memberships = new StubMembershipStore();
        if (member) memberships.Add(user.ExternalIdentityId!, ProjectA);
        return (new DashboardService(store, new AuthorizationService(user, memberships)), store);
    }

    private static ReportDateRange Range()
        => new(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Summary_Member_CanRead()
    {
        var (service, _) = CreateSimple(Member());
        var summary = await service.GetSummaryAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(ProjectA, summary.ProjectId);
    }

    [Fact]
    public async Task Summary_Anonymous_Throws401()
    {
        var (service, _) = CreateSimple(new StubCurrentUser { IsAuthenticated = false }, member: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetSummaryAsync(ProjectA, Range(), CancellationToken.None));
    }

    [Fact]
    public async Task Summary_NonMember_Throws403()
    {
        var (service, _) = CreateSimple(Member(), member: false);
        await Assert.ThrowsAsync<ForbiddenException>(
            () => service.GetSummaryAsync(ProjectA, Range(), CancellationToken.None));
    }

    [Fact]
    public async Task Summary_Viewer_CanRead_DashboardRead()
    {
        // Viewer holds dashboard.read: read-only analytics stay visible.
        var (service, _) = CreateSimple(Viewer());
        var summary = await service.GetSummaryAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(ProjectA, summary.ProjectId);
    }

    [Fact]
    public async Task Summary_AdminBypass_Works_WithoutMembership()
    {
        var (service, _) = CreateSimple(Admin(), member: false);
        var summary = await service.GetSummaryAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(ProjectA, summary.ProjectId);
    }

    [Fact]
    public async Task Summary_EmptyProject_ReturnsZeros_AndNullPassRate()
    {
        var (service, store) = CreateSimple(Member());
        store.ExecutionsValue = new ExecutionKpis(0, 0, 0, 0, 0, 0, 0, null);
        var summary = await service.GetSummaryAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(0, summary.TestCases.Total);
        Assert.Null(summary.Executions.PassRate);
        Assert.Empty(summary.RecentExecutions);
        Assert.Empty(summary.RecentActivity);
    }

    [Fact]
    public async Task Trend_BuildsDailyPoints_WithZerosForMissingDays()
    {
        var (service, store) = CreateSimple(Member());
        store.DayBuckets.Add(new StatusDayCount(2026, 9, 28, "Passed", 2));
        store.DayBuckets.Add(new StatusDayCount(2026, 9, 28, "Failed", 1));
        var from = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var trend = await service.GetExecutionTrendAsync(ProjectA, new ReportDateRange(from, to), null, CancellationToken.None);
        Assert.Equal("day", trend.Granularity);
        Assert.Equal(3, trend.Points.Count);
        Assert.Equal(0, trend.Points[0].Total);
        Assert.Equal(3, trend.Points[1].Total);
        Assert.Equal(2, trend.Points[1].Passed);
    }

    [Fact]
    public async Task Trend_InvalidGranularity_Throws400()
    {
        var (service, _) = CreateSimple(Member());
        await Assert.ThrowsAsync<ValidationException>(
            () => service.GetExecutionTrendAsync(ProjectA, Range(), "hourly", CancellationToken.None));
    }

    [Fact]
    public async Task Trend_AutoSelectsWeekly_ForLongRanges()
    {
        var (service, store) = CreateSimple(Member());
        store.DayBuckets.Add(new StatusDayCount(2026, 7, 1, "Passed", 5));
        var trend = await service.GetExecutionTrendAsync(ProjectA,
            new ReportDateRange(
                new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)),
            null, CancellationToken.None);
        Assert.Equal("week", trend.Granularity);
        Assert.Equal(5, trend.Points.Sum(p => p.Passed));
    }

    [Fact]
    public void DateRange_Default_IsThirtyDays()
    {
        var range = ReportDateRange.Default(new FixedClock());
        Assert.Equal(30, (range.To - range.From).TotalDays);
    }

    [Fact]
    public void DateRange_Rejects_FromAfterTo_And_Oversized()
    {
        var clock = new FixedClock();
        Assert.Throws<ValidationException>(() =>
            ReportDateRange.Parse("2026-09-29T00:00:00Z", "2026-09-01T00:00:00Z", clock));
        Assert.Throws<ValidationException>(() =>
            ReportDateRange.Parse("2024-01-01", "2026-09-29", clock));
        Assert.Throws<ValidationException>(() =>
            ReportDateRange.Parse("not-a-date", null, clock));
    }

    [Fact]
    public void DateRange_DateOnly_Means_UtcDay()
    {
        var range = ReportDateRange.Parse("2026-09-01", "2026-09-30", new FixedClock());
        Assert.Equal(TimeSpan.Zero, range.From.Offset);
        Assert.Equal(new DateTime(2026, 9, 1), range.From.UtcDateTime.Date);
    }

    [Fact]
    public async Task Breakdown_Totals_Match_Items()
    {
        var (service, store) = CreateSimple(Member());
        store.Classifications.Add(new NamedCount("ApplicationDefect", 3));
        store.Classifications.Add(new NamedCount("Unknown", 1));
        var breakdown = await service.GetFailureBreakdownAsync(ProjectA, Range(), CancellationToken.None);
        Assert.Equal(4, breakdown.Total);
        Assert.Equal(2, breakdown.Items.Count);
    }

    [Fact]
    public async Task CrossProject_Dashboard_Blocked()
    {
        var user = Member();
        var memberships = new StubMembershipStore();
        memberships.Add(user.ExternalIdentityId!, ProjectA);
        var svc = new DashboardService(new FakeReportQueryStore(), new AuthorizationService(user, memberships));
        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.GetSummaryAsync(ProjectB, Range(), CancellationToken.None));
    }
}
