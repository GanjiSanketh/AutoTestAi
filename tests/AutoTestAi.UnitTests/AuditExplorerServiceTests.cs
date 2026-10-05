using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Reports;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Phase 4 Slice 2: project-scoped read-only Audit Explorer — auth matrix,
/// filter forwarding, pagination caps, export bounds, safe projection.
/// </summary>
public sealed class AuditExplorerServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ActorA = Guid.NewGuid();

    private static StubCurrentUser Member() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "user-1",
        Roles = ["tester"], Permissions = RolePermissions.Resolve(["tester"]),
    };

    private static StubCurrentUser Viewer() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "viewer-1",
        Roles = ["viewer"], Permissions = RolePermissions.Resolve(["viewer"]),
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

    private static AuditEventFilters Filters(string? action = null, Guid? actor = null, string? entityType = null)
        => new(action, actor, entityType);

    [Fact]
    public async Task Audit_Unauthenticated_Throws401()
    {
        var (service, _) = Create(new StubCurrentUser { IsAuthenticated = false }, member: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetAuditEventsAsync(ProjectA, Range(), Filters(), 1, 25, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ExportAuditEventsCsvAsync(ProjectA, Range(), Filters(), CancellationToken.None));
    }

    [Fact]
    public async Task Audit_NonMember_Throws403()
    {
        var (service, _) = Create(Member(), member: false);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetAuditEventsAsync(ProjectA, Range(), Filters(), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Audit_WithoutReportsRead_Throws403()
    {
        // A role holding only dashboard.read must not reach the audit explorer.
        var user = new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "user-1",
            Roles = ["custom"], Permissions = ["dashboard.read"],
        };
        var (service, _) = Create(user);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetAuditEventsAsync(ProjectA, Range(), Filters(), 1, 25, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.ExportAuditEventsCsvAsync(ProjectA, Range(), Filters(), CancellationToken.None));
    }

    [Fact]
    public async Task Audit_Viewer_CanRead_ReportsRead()
    {
        var (service, _) = Create(Viewer());
        var page = await service.GetAuditEventsAsync(ProjectA, Range(), Filters(), 1, 25, CancellationToken.None);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Audit_Forwards_Project_Range_Filters_And_Pagination()
    {
        var (service, store) = Create();
        Guid seenProject = Guid.Empty;
        ReportDateRange? seenRange = null;
        AuditEventFilters? seenFilters = null;
        int seenSkip = -1, seenTake = -1;
        store.AuditQuery = (p, r, f, s, t, ct) =>
        {
            seenProject = p;
            seenRange = r;
            seenFilters = f;
            seenSkip = s;
            seenTake = t;
            return Task.FromResult(new PagedResult<AuditEventItem>(
                Array.Empty<AuditEventItem>(), 95, 0, 0));
        };
        var range = Range();
        var page = await service.GetAuditEventsAsync(ProjectA, range,
            Filters("defect.created", ActorA, "defect"), 2, 10, CancellationToken.None);
        Assert.Equal(ProjectA, seenProject);
        Assert.Same(range, seenRange);
        Assert.Equal("defect.created", seenFilters!.Action);
        Assert.Equal(ActorA, seenFilters.ActorUserId);
        Assert.Equal("defect", seenFilters.EntityType);
        Assert.Equal(10, seenSkip);
        Assert.Equal(10, seenTake);
        Assert.Equal(95, page.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.PageSize);
    }

    [Fact]
    public async Task Audit_Pagination_Clamped_PageSize100()
    {
        var (service, store) = Create();
        int seenSkip = -1, seenTake = -1;
        store.AuditQuery = (p, r, f, s, t, ct) =>
        {
            seenSkip = s;
            seenTake = t;
            return Task.FromResult(new PagedResult<AuditEventItem>(
                Array.Empty<AuditEventItem>(), 0, 0, 0));
        };
        var page = await service.GetAuditEventsAsync(ProjectA, Range(), Filters(), 0, 500, CancellationToken.None);
        Assert.Equal(0, seenSkip);
        Assert.Equal(100, seenTake);
        Assert.Equal(1, page.Page);
        Assert.Equal(100, page.PageSize);
    }

    [Fact]
    public async Task Audit_Overlong_Filters_Throw400()
    {
        var (service, _) = Create();
        var longValue = new string('x', 101);
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetAuditEventsAsync(ProjectA, Range(), Filters(longValue), 1, 25, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetAuditEventsAsync(ProjectA, Range(), Filters(entityType: longValue), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Audit_NullActor_Preserved()
    {
        var (service, store) = Create();
        var at = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        store.AuditQuery = (p, r, f, s, t, ct) => Task.FromResult(new PagedResult<AuditEventItem>(
            new[] { new AuditEventItem(7, at, "project.created", "project", "p", null) }, 1, 0, 0));
        var page = await service.GetAuditEventsAsync(ProjectA, Range(), Filters(), 1, 25, CancellationToken.None);
        Assert.Null(page.Items[0].ActorUserId);
        Assert.Equal(7, page.Items[0].Id);
        Assert.Equal(at, page.Items[0].Timestamp);
    }

    [Fact]
    public async Task Audit_Cancellation_Threaded()
    {
        var (service, store) = Create();
        CancellationToken seen = default;
        store.AuditQuery = (p, r, f, s, t, ct) =>
        {
            seen = ct;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PagedResult<AuditEventItem>(
                Array.Empty<AuditEventItem>(), 0, 0, 0));
        };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetAuditEventsAsync(ProjectA, Range(), Filters(), 1, 25, cts.Token));
        Assert.True(seen.CanBeCanceled);
    }

    [Fact]
    public async Task Audit_Export_Caps_At_5000_Rows()
    {
        var (service, store) = Create();
        int seenSkip = -1, seenTake = -1;
        AuditEventFilters? seenFilters = null;
        store.AuditQuery = (p, r, f, s, t, ct) =>
        {
            seenSkip = s;
            seenTake = t;
            seenFilters = f;
            return Task.FromResult(new PagedResult<AuditEventItem>(
                Array.Empty<AuditEventItem>(), 0, 0, 0));
        };
        var export = await service.ExportAuditEventsCsvAsync(
            ProjectA, Range(), Filters("ticket.created"), CancellationToken.None);
        Assert.Equal(0, seenSkip);
        Assert.Equal(5000, seenTake);
        Assert.Equal("ticket.created", seenFilters!.Action);
        Assert.Equal("text/csv", export.ContentType);
        Assert.StartsWith($"audit-{ProjectA:N}-", export.FileName);
        Assert.EndsWith(".csv", export.FileName);
    }

    [Fact]
    public void Audit_ExportCsv_Header_And_Safe_Fields_Only()
    {
        var at = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var rows = new[]
        {
            new AuditEventItem(1, at, "defect.created", "defect", "d-1", ActorA),
            new AuditEventItem(2, at, "project.created", "project", null, null),
        };
        var text = System.Text.Encoding.UTF8.GetString(CsvExporter.ExportAuditEvents(rows));
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("timestamp,action,entityType,entityId,actorUserId", lines[0]);
        Assert.StartsWith("2026-09-28T12:00:00.0000000Z,defect.created,defect,d-1,", lines[1]);
        Assert.Contains(ActorA.ToString(), lines[1]);
        Assert.EndsWith("project.created,project,,", lines[2]);
        Assert.DoesNotContain("metadata", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ipAddress", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_ExportCsv_Escapes_Cells()
    {
        var at = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var rows = new[]
        {
            new AuditEventItem(1, at, "weird,\"action", "ent\nity", "a,b", ActorA),
        };
        var text = System.Text.Encoding.UTF8.GetString(CsvExporter.ExportAuditEvents(rows));
        Assert.Contains("\"weird,\"\"action\"", text);
        Assert.Contains("\"ent\nity\"", text);
        Assert.Contains("\"a,b\"", text);
    }
}
