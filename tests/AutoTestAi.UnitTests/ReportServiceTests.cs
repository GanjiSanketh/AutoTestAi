using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Reports;

namespace AutoTestAi.UnitTests;

public sealed class ReportServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    private static StubCurrentUser Member() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "user-1",
        Roles = ["tester"], Permissions = RolePermissions.Resolve(["tester"]),
    };

    private static (ReportService Service, FakeReportQueryStore Store) Create(
        ICurrentUserService? user = null, bool member = true)
    {
        user ??= Member();
        var store = new FakeReportQueryStore();
        var memberships = new StubMembershipStore();
        if (member && user.ExternalIdentityId is not null)
            memberships.Add(user.ExternalIdentityId, ProjectA);
        return (new ReportService(store, new AuthorizationService(user, memberships)), store);
    }

    private static ReportDateRange Range()
        => new(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Executions_Unauthenticated_Throws401()
    {
        var (service, _) = Create(new StubCurrentUser { IsAuthenticated = false }, member: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetExecutionsAsync(ProjectA, Range(),
                new ExecutionReportFilters(null, null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Executions_NonMember_Throws403()
    {
        var (service, _) = Create(Member(), member: false);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetExecutionsAsync(ProjectA, Range(),
                new ExecutionReportFilters(null, null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Executions_WithoutReportsRead_Throws403()
    {
        // A role holding only dashboard.read must not reach report tables.
        var user = new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "user-1",
            Roles = ["custom"], Permissions = ["dashboard.read"],
        };
        var (service, _) = Create(user);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetExecutionsAsync(ProjectA, Range(),
                new ExecutionReportFilters(null, null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Executions_InvalidStatus_Throws400()
    {
        var (service, _) = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetExecutionsAsync(ProjectA, Range(),
                new ExecutionReportFilters("Exploded", null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Executions_InvalidClassification_Throws400()
    {
        var (service, _) = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetExecutionsAsync(ProjectA, Range(),
                new ExecutionReportFilters(null, null, "AiGuessed"), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Executions_Pagination_Clamped_And_Forwarded()
    {
        var (service, store) = Create();
        int seenSkip = -1, seenTake = -1;
        store.ExecQuery = (p, r, f, s, t, ct) =>
        {
            seenSkip = s;
            seenTake = t;
            return Task.FromResult(new PagedResult<ExecutionReportItem>(
                Array.Empty<ExecutionReportItem>(), 95, 0, 0));
        };
        var page = await service.GetExecutionsAsync(ProjectA, Range(),
            new ExecutionReportFilters(null, null, null), 0, 500, CancellationToken.None);
        Assert.Equal(95, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(100, page.PageSize);
        Assert.Equal(0, seenSkip);
        Assert.Equal(100, seenTake);
    }

    [Fact]
    public async Task Defects_InvalidSeverity_Throws400()
    {
        var (service, _) = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetDefectsAsync(ProjectA, Range(),
                new DefectReportFilters(null, "Extreme", null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Tickets_InvalidSyncStatus_Throws400()
    {
        var (service, _) = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetTicketsAsync(ProjectA, Range(),
                new TicketReportFilters(null, "Sending"), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Tickets_OversizedProvider_Throws400()
    {
        var (service, _) = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetTicketsAsync(ProjectA, Range(),
                new TicketReportFilters(new string('x', 61), null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Tickets_ReadOnlyStore_HasNoMutationSurface()
    {
        // Compile-time guarantee: IReportQueryStore exposes Task queries only.
        // This test pins the interface shape so a mutating method cannot slip in.
        var mutating = typeof(IReportQueryStore).GetMethods()
            .Where(m => m.ReturnType == typeof(Task) ||
                (m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                 && m.ReturnType.GetGenericArguments()[0] == typeof(void)))
            .ToList();
        Assert.Empty(mutating);
        var (service, _) = Create();
        var result = await service.GetTicketsAsync(ProjectA, Range(),
            new TicketReportFilters(null, null), 1, 25, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    // ---------- Phase-4 Slice 1: flakiness risk forecast ----------

    private static DateTimeOffset Day(int day)
        => new(2026, 9, day, 12, 0, 0, TimeSpan.Zero);

    private static void SeedForecastFixture(
        FakeReportQueryStore store, Guid id, string key, int passed, int failed,
        IReadOnlyList<bool> newestFirst)
    {
        store.OutcomeRows.Add(new TestOutcomeRow(id, passed, failed, 0, Day(28)));
        store.MetaRows.Add(new TestCaseMetaRow(id, key, key, null, "High", "playwright", "web"));
        var at = Day(28);
        foreach (var pass in newestFirst)
        {
            store.VerdictRows.Add(new TestVerdictRow(id, pass, at));
            at = at.AddDays(-1);
        }
        store.LastRuns.Add(new TestLastRunRow(id, Guid.NewGuid(),
            newestFirst.Count > 0 && newestFirst[0] ? "Passed" : "Failed", Day(28)));
    }

    [Fact]
    public async Task FlakyReport_IncludesForecastFields()
    {
        var (service, store) = Create();
        var t1 = Guid.NewGuid();
        // Newest-first [F,F,P,F,P]: score 45 Medium, all factors (worked example).
        SeedForecastFixture(store, t1, "F-001", 2, 3,
            new[] { false, false, true, false, true });

        var page = await service.GetFlakyTestsAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            null, false, 1, 25, CancellationToken.None);
        var item = Assert.Single(page.Items);
        Assert.Equal(45, item.RiskScore);
        Assert.Equal("Medium", item.RiskBand);
        Assert.Equal(3, item.RiskFactors!.Count);
    }

    [Fact]
    public async Task FlakyReport_InsufficientHistory_NullForecast()
    {
        var (service, store) = Create();
        var t1 = Guid.NewGuid();
        SeedForecastFixture(store, t1, "S-001", 0, 1, new[] { false });

        var page = await service.GetFlakyTestsAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            null, false, 1, 25, CancellationToken.None);
        var item = Assert.Single(page.Items);
        Assert.Null(item.RiskScore);
        Assert.Null(item.RiskBand);
        Assert.Empty(item.RiskFactors ?? Array.Empty<string>());
    }

    [Fact]
    public async Task FlakyReport_SortByRiskScore_NullsLast()
    {
        var (service, store) = Create();
        var high = Guid.NewGuid();
        var low = Guid.NewGuid();
        var thin = Guid.NewGuid();
        SeedForecastFixture(store, high, "H-001", 0, 4,
            new[] { false, false, false, false });
        SeedForecastFixture(store, low, "L-001", 4, 0,
            new[] { true, true, true, true });
        SeedForecastFixture(store, thin, "T-001", 0, 1, new[] { false });

        var page = await service.GetFlakyTestsAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            "riskScore", true, 1, 25, CancellationToken.None);
        Assert.Equal(new[] { "H-001", "L-001", "T-001" },
            page.Items.Select(i => i.TestKey).ToArray());

        var ascending = await service.GetFlakyTestsAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            "riskScore", false, 1, 25, CancellationToken.None);
        Assert.Equal(new[] { "L-001", "H-001", "T-001" },
            ascending.Items.Select(i => i.TestKey).ToArray());
    }

    [Fact]
    public async Task FlakyReport_InvalidSort_Still400_WithRiskKeyListed()
    {
        var (service, _) = Create();
        var ex = await Assert.ThrowsAsync<ValidationException>(() => service.GetFlakyTestsAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            "drop-table", false, 1, 25, CancellationToken.None));
        Assert.Contains("riskScore", ex.Message);
    }

    [Fact]
    public async Task FlakyExport_ContainsForecastColumns()
    {
        var (service, store) = Create();
        var t1 = Guid.NewGuid();
        SeedForecastFixture(store, t1, "E-001", 2, 3,
            new[] { false, false, true, false, true });

        var export = await service.ExportFlakyTestsCsvAsync(ProjectA, Range(),
            new FlakyTestsFilters(null, false, 0, null, null, null, false),
            CancellationToken.None);
        var text = System.Text.Encoding.UTF8.GetString(export.Content);
        Assert.Contains("riskScore,riskBand,riskFactors", text);
        Assert.Contains(",45,Medium,", text);
        Assert.Contains("Recent failure rate is elevated", text);
    }
}
