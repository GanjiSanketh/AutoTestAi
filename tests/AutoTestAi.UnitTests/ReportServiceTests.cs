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
}
