using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 2: project-scoped read-only Audit Explorer — auth matrix,
/// isolation, filters, pagination, deterministic ordering, safe CSV export,
/// no mutation, no metadata exposure.
/// </summary>
public sealed class AuditExplorerApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private Guid _managerId;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public AuditExplorerApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(async db =>
            {
                if (await db.Projects.AnyAsync(p => p.Id == pa)) return;
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var mgr = new User { ExternalIdentityId = "audit-manager", Email = "am@x", DisplayName = "Audit Manager" };
                var viewer = new User { ExternalIdentityId = "audit-viewer", Email = "av@x", DisplayName = "Audit Viewer" };
                var outsider = new User { ExternalIdentityId = "audit-outsider", Email = "ao@x", DisplayName = "Audit Outsider" };
                var admin = new User { ExternalIdentityId = "audit-admin", Email = "aa@x", DisplayName = "Audit Admin" };
                db.Users.AddRange(mgr, viewer, outsider, admin);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Audit Alpha", Key = "ADA" },
                    new Project { Id = pb, Name = "Audit Beta", Key = "ADB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });
                await db.SaveChangesAsync();

                var now = DateTimeOffset.UtcNow;
                var tie = now.AddHours(-1);
                // e4/e5 share an identical timestamp: Id descending must break the tie.
                db.AuditEvents.AddRange(
                    new AuditEvent { Action = "defect.created", EntityType = "defect", EntityId = "d-1", ProjectId = pa, ActorUserId = mgr.Id, CreatedAt = now.AddDays(-3), IpAddress = "10.0.0.1", UserAgent = "ua-test", MetadataJson = "{\"apiKey\":\"sk-live-123\",\"note\":\"hi\"}" },
                    new AuditEvent { Action = "ticket.created", EntityType = "ticket", EntityId = "t-1", ProjectId = pa, ActorUserId = mgr.Id, CreatedAt = now.AddDays(-2) },
                    new AuditEvent { Action = "defect.created", EntityType = "defect", EntityId = "d-2", ProjectId = pa, ActorUserId = null, CreatedAt = now.AddDays(-1) },
                    new AuditEvent { Action = "project.updated", EntityType = "project", EntityId = pa.ToString(), ProjectId = pa, ActorUserId = mgr.Id, CreatedAt = tie },
                    new AuditEvent { Action = "environment.created", EntityType = "environment", EntityId = "env-1", ProjectId = pa, ActorUserId = mgr.Id, CreatedAt = tie },
                    // Outside the default 30-day window; reachable only with an explicit range.
                    new AuditEvent { Action = "defect.created", EntityType = "defect", EntityId = "d-old", ProjectId = pa, ActorUserId = mgr.Id, CreatedAt = now.AddDays(-60) },
                    // Project B isolation probe.
                    new AuditEvent { Action = "defect.created", EntityType = "defect", EntityId = "db-1", ProjectId = pb, ActorUserId = null, CreatedAt = now.AddDays(-1) });
                _managerId = mgr.Id;
                await db.SaveChangesAsync();
            });
            if (_managerId == Guid.Empty)
            {
                await _factory.SeedAsync(async db =>
                {
                    _managerId = await db.Users
                        .Where(u => u.ExternalIdentityId == "audit-manager")
                        .Select(u => u.Id)
                        .SingleAsync();
                });
            }
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

    private static JsonElement Prop(JsonDocument doc, params string[] path)
    {
        var current = doc.RootElement;
        foreach (var segment in path)
            current = current.GetProperty(segment);
        return current;
    }

    [Fact]
    public async Task List_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);
    }

    [Fact]
    public async Task List_Outsider_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("audit-outsider", ["tester"]).GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);
    }

    [Fact]
    public async Task List_CrossProject_Returns403()
    {
        await SeedOnceAsync();
        // Manager belongs to A only; B must deny (existing project-access behavior).
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("audit-manager", ["qa-lead"]).GetAsync($"/api/v1/projects/{_projectB}/audit/events")).StatusCode);
    }

    [Fact]
    public async Task List_WithoutReportsRead_Returns403()
    {
        await SeedOnceAsync();
        var limited = _factory.CreateClient();
        limited.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create("audit-limited", ["custom-limited"]));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await limited.GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);
    }

    [Fact]
    public async Task List_ReturnsSafeShape_And_DeterministicOrder()
    {
        await SeedOnceAsync();
        var response = await Client("audit-manager", ["qa-lead"])
            .GetAsync($"/api/v1/projects/{_projectA}/audit/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);
        // Default 30-day window excludes the 60-day-old row.
        Assert.Equal(5, Prop(doc!, "totalCount").GetInt32());
        Assert.Equal(1, Prop(doc!, "page").GetInt32());
        var items = Prop(doc!, "items").EnumerateArray().ToList();
        Assert.Equal(5, items.Count);
        // Tie-break: identical timestamps order by Id descending.
        Assert.Equal("environment.created", items[0].GetProperty("action").GetString());
        Assert.Equal("project.updated", items[1].GetProperty("action").GetString());
        Assert.True(items[0].GetProperty("id").GetInt64() > items[1].GetProperty("id").GetInt64());
        // Safe fields only; null actor preserved.
        var system = items.Single(i => i.GetProperty("entityId").GetString() == "d-2");
        Assert.Equal(JsonValueKind.Null, system.GetProperty("actorUserId").ValueKind);
        var body = doc!.RootElement.GetRawText();
        Assert.DoesNotContain("metadata", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ipAddress", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userAgent", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-live-123", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_Admin_Reads_ProjectB_Isolated()
    {
        await SeedOnceAsync();
        using var doc = await (await Client("audit-admin", ["admin"])
                .GetAsync($"/api/v1/projects/{_projectB}/audit/events"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(doc!, "totalCount").GetInt32());
        Assert.Equal("db-1", Prop(doc!, "items").EnumerateArray().Single().GetProperty("entityId").GetString());
    }

    [Fact]
    public async Task List_Filters_Work()
    {
        await SeedOnceAsync();
        var client = Client("audit-viewer", ["viewer"]);

        using var byAction = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?action=defect.created"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, Prop(byAction!, "totalCount").GetInt32());
        Assert.All(Prop(byAction!, "items").EnumerateArray(),
            i => Assert.Equal("defect.created", i.GetProperty("action").GetString()));

        using var byEntity = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?entityType=ticket"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(byEntity!, "totalCount").GetInt32());

        using var byActor = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?actor={_managerId}"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(4, Prop(byActor!, "totalCount").GetInt32());

        var from = DateTimeOffset.UtcNow.AddDays(-4).ToString("yyyy-MM-dd");
        var to = DateTimeOffset.UtcNow.AddDays(-2).ToString("yyyy-MM-dd");
        using var byRange = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?from={from}&to={to}"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        // Date-only `to` denotes start-of-day per existing ReportDateRange
        // semantics, so only e1 (-3d) matches; e2 (later on the `to` day) is excluded.
        Assert.Equal(1, Prop(byRange!, "totalCount").GetInt32());
    }

    [Fact]
    public async Task List_ExplicitRange_Reaches_OldEvents()
    {
        await SeedOnceAsync();
        var from = DateTimeOffset.UtcNow.AddDays(-90).ToString("yyyy-MM-dd");
        using var doc = await (await Client("audit-manager", ["qa-lead"]).GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?from={from}"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(6, Prop(doc!, "totalCount").GetInt32());
    }

    [Fact]
    public async Task List_Paginates_And_Caps_PageSize()
    {
        await SeedOnceAsync();
        var client = Client("audit-manager", ["qa-lead"]);
        using var first = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?page=1&pageSize=2"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(5, Prop(first!, "totalCount").GetInt32());
        Assert.Equal(2, Prop(first!, "items").GetArrayLength());

        using var second = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?page=2&pageSize=2"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, Prop(second!, "items").GetArrayLength());
        Assert.NotEqual(
            Prop(first!, "items").EnumerateArray().First().GetProperty("id").GetInt64(),
            Prop(second!, "items").EnumerateArray().First().GetProperty("id").GetInt64());

        // Existing convention: oversized pageSize normalizes to 100.
        using var capped = await (await client.GetAsync(
                $"/api/v1/projects/{_projectA}/audit/events?pageSize=500"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(100, Prop(capped!, "pageSize").GetInt32());
    }

    [Fact]
    public async Task List_InvalidInput_Returns400()
    {
        await SeedOnceAsync();
        var client = Client("audit-manager", ["qa-lead"]);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/projects/{_projectA}/audit/events?from=2026-09-29&to=2026-09-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/projects/{_projectA}/audit/events?actor=not-a-guid")).StatusCode);
        var tooLong = new string('x', 101);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/projects/{_projectA}/audit/events?action={tooLong}")).StatusCode);
    }

    [Fact]
    public async Task Export_Returns_SafeCsv_With_Filters()
    {
        await SeedOnceAsync();
        var client = Client("audit-manager", ["qa-lead"]);
        var response = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/audit/events".Replace("/audit/events", "/audit/export") + "?action=defect.created");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("timestamp,action,entityType,entityId,actorUserId", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.Contains("defect.created", l, StringComparison.Ordinal));
        Assert.DoesNotContain("metadata", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-live-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_Is_ProjectScoped_And_Guarded()
    {
        await SeedOnceAsync();
        var adminExport = await Client("audit-admin", ["admin"])
            .GetAsync($"/api/v1/projects/{_projectB}/audit/export");
        Assert.Equal(HttpStatusCode.OK, adminExport.StatusCode);
        var adminText = await adminExport.Content.ReadAsStringAsync();
        Assert.Equal(2, adminText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync($"/api/v1/projects/{_projectA}/audit/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("audit-outsider", ["tester"]).GetAsync($"/api/v1/projects/{_projectA}/audit/export")).StatusCode);
    }

    [Fact]
    public async Task Reads_DoNotMutate_AuditEvents()
    {
        await SeedOnceAsync();
        var client = Client("audit-manager", ["qa-lead"]);
        var before = 0;
        await _factory.SeedAsync(db =>
        {
            before = db.AuditEvents.Count(a => a.ProjectId == _projectA);
            return Task.CompletedTask;
        });
        await client.GetAsync($"/api/v1/projects/{_projectA}/audit/events");
        await client.GetAsync($"/api/v1/projects/{_projectA}/audit/export");
        await _factory.SeedAsync(db =>
        {
            Assert.Equal(before, db.AuditEvents.Count(a => a.ProjectId == _projectA));
            return Task.CompletedTask;
        });
    }
}
