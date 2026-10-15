using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Domain.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 11: application-observable authentication/authorization
/// event auditing — first-seen provisioning and bounded project-denial rows
/// through the real middleware/decorator chain, with isolation, safety, and
/// failure-containment coverage.
/// </summary>
public sealed class AuthEventAuditApiTests : IClassFixture<Slice1ApiFactory>, IDisposable
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly string _managerSub = $"authz-manager-{Guid.NewGuid():N}";
    private readonly string _viewerSub = $"authz-viewer-{Guid.NewGuid():N}";
    private readonly string _outsiderSub = $"authz-outsider-{Guid.NewGuid():N}";
    private readonly string _betaSub = $"authz-beta-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public AuthEventAuditApiTests(Slice1ApiFactory factory) => _factory = factory;

    public void Dispose() => _seedLock.Dispose();

    private sealed class ThrowingAudit : IAuditService
    {
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
            => throw new InvalidOperationException("audit store down");
    }

    private sealed class ThrowingDirectory : AutoTestAi.Application.Identity.IUserDirectory
    {
        public Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken ct)
            => Task.FromResult<Guid?>(null);
        public Task<Guid> EnsureProvisionedAsync(string externalIdentityId, string? email, string? displayName, CancellationToken ct)
            => throw new InvalidOperationException("directory down");
        public Task<(Guid UserId, bool Created)> EnsureProvisionedWithOutcomeAsync(string externalIdentityId, string? email, string? displayName, CancellationToken ct)
            => throw new InvalidOperationException("directory down");
    }

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
                var manager = new User { ExternalIdentityId = _managerSub, Email = "m@x", DisplayName = "M" };
                var viewer = new User { ExternalIdentityId = _viewerSub, Email = "v@x", DisplayName = "V" };
                var outsider = new User { ExternalIdentityId = _outsiderSub, Email = "o@x", DisplayName = "O" };
                var beta = new User { ExternalIdentityId = _betaSub, Email = "b@x", DisplayName = "B" };
                db.Users.AddRange(manager, viewer, outsider, beta);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "AuthZ Alpha", Key = "AZA" },
                    new Project { Id = pb, Name = "AuthZ Beta", Key = "AZB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = manager.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId },
                    new ProjectMember { ProjectId = pb, UserId = beta.Id, RoleId = QaLeadRoleId });
                await db.SaveChangesAsync();
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string sub, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, [role]));
        return client;
    }

    private async Task<List<AuditEvent>> AuditRowsAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        return await db.AuditEvents.Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync();
    }

    private async Task<int> CountRowsAsync(string action, Guid? projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        return await db.AuditEvents.CountAsync(a => a.Action == action && a.ProjectId == projectId);
    }

    private async Task<Guid> FindUserIdAsync(string externalIdentityId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        return await db.Users.Where(u => u.ExternalIdentityId == externalIdentityId)
            .Select(u => u.Id).SingleAsync();
    }

    // ---------- provisioning ----------

    [Fact]
    public async Task Provision_FirstSeen_EmitsExactlyOneEvent()
    {
        await SeedOnceAsync();
        var sub = $"authz-fresh-{Guid.NewGuid():N}";
        var client = Client(_factory, sub, "viewer");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        var userId = await FindUserIdAsync(sub);
        var rows = await AuditRowsAsync("auth.provisioned");
        var row = Assert.Single(rows.Where(r => r.ActorUserId == userId));
        Assert.Equal("user", row.EntityType);
        Assert.Null(row.ProjectId);
        Assert.Null(row.MetadataJson);
        Assert.Equal(userId.ToString(), row.EntityId);
        Assert.Equal(userId, row.ActorUserId);

        // Repeat access emits nothing further.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(1, (await AuditRowsAsync("auth.provisioned")).Count(r => r.ActorUserId == userId));
    }

    [Fact]
    public async Task Provision_ExistingUser_EmitsNone()
    {
        await SeedOnceAsync();
        var userId = await FindUserIdAsync(_managerSub);
        var before = (await AuditRowsAsync("auth.provisioned")).Count(r => r.ActorUserId == userId);
        var client = Client(_factory, _managerSub, "qa-lead");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(before, (await AuditRowsAsync("auth.provisioned")).Count(r => r.ActorUserId == userId));
    }

    // ---------- denials ----------

    [Fact]
    public async Task Denial_MembershipDenied_EmitsBoundedEvent()
    {
        await SeedOnceAsync();
        // Tester holds report permission but belongs to no project here.
        var client = Client(_factory, _outsiderSub, "tester");
        var response = await client.GetAsync($"/api/v1/projects/{_projectA}/audit/events");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var rows = await AuditRowsAsync("authz.project_denied");
        var row = Assert.Single(rows.Where(r => r.ProjectId == _projectA));
        Assert.Equal("project", row.EntityType);
        Assert.Equal(_projectA.ToString(), row.EntityId);
        Assert.NotNull(row.ActorUserId);
        Assert.Contains("membership", row.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", row.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Denial_PermissionDenied_EmitsEvent()
    {
        await SeedOnceAsync();
        // Viewer is a member but lacks testcases.manage.
        var client = Client(_factory, _viewerSub, "viewer");
        var response = await client.PostAsync($"/api/v1/projects/{_projectA}/maintenance/scan", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var rows = await AuditRowsAsync("authz.project_denied");
        var row = Assert.Single(rows.Where(r => r.ProjectId == _projectA && r.EntityType == "project"));
        Assert.Contains("permission", row.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("testcases.manage", row.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denial_UnknownProject_EmitsNone_ButStillForbidden()
    {
        await SeedOnceAsync();
        var unknown = Guid.NewGuid();
        var client = Client(_factory, _managerSub, "qa-lead");
        var response = await client.GetAsync($"/api/v1/projects/{unknown}/audit/events");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountRowsAsync("authz.project_denied", unknown));
    }

    [Fact]
    public async Task Denial_Anonymous_EmitsNone()
    {
        await SeedOnceAsync();
        var before = await CountRowsAsync("authz.project_denied", _projectA);
        var response = await _factory.CreateClient().GetAsync($"/api/v1/projects/{_projectA}/audit/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await CountRowsAsync("authz.project_denied", _projectA));
    }

    [Fact]
    public async Task Denial_Burst_IsDeduplicated()
    {
        await SeedOnceAsync();
        var client = Client(_factory, _outsiderSub, "tester");
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);
        Assert.Equal(1, await CountRowsAsync("authz.project_denied", _projectA));
    }

    [Fact]
    public async Task Denial_IsolatedPerProject_InExplorer()
    {
        await SeedOnceAsync();
        var outsider = Client(_factory, _outsiderSub, "tester");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);

        // Beta member browses Beta: A's denial never surfaces there.
        var beta = Client(_factory, _betaSub, "qa-lead");
        var list = await beta.GetAsync(
            $"/api/v1/projects/{_projectB}/audit/events?action=authz.project_denied&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        Assert.Equal(0, doc!.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Denial_VisibleInOwningProjectExplorer_ExportUnchanged()
    {
        await SeedOnceAsync();
        var outsider = Client(_factory, _outsiderSub, "tester");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);

        var manager = Client(_factory, _managerSub, "qa-lead");
        var list = await manager.GetAsync(
            $"/api/v1/projects/{_projectA}/audit/events?action=authz.project_denied&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        var items = doc!.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("authz.project_denied", item.GetProperty("action").GetString());
        Assert.False(item.TryGetProperty("metadata", out _));
        Assert.False(item.TryGetProperty("metadataJson", out _));

        var export = await manager.GetAsync(
            $"/api/v1/projects/{_projectA}/audit/export?action=authz.project_denied");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    [Fact]
    public async Task Provision_Failure_EmitsNone_AndRequestSucceeds()
    {
        await SeedOnceAsync();
        var before = (await AuditRowsAsync("auth.provisioned")).Count;
        using var failing = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AutoTestAi.Application.Identity.IUserDirectory>();
            services.AddSingleton<AutoTestAi.Application.Identity.IUserDirectory>(new ThrowingDirectory());
        }));
        try
        {
            // Provisioning failure is swallowed by the existing best-effort
            // middleware handling: the request still succeeds, with no event.
            var fresh = Client(failing, $"authz-fail-{Guid.NewGuid():N}", "viewer");
            Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/v1/auth/me")).StatusCode);
            Assert.Equal(before, (await AuditRowsAsync("auth.provisioned")).Count);
        }
        finally
        {
            failing.Dispose();
        }
    }

    [Fact]
    public async Task AuditFailure_PreservesResponses()
    {
        await SeedOnceAsync();
        using var throwing = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuditService>();
            services.AddSingleton<IAuditService>(new ThrowingAudit());
        }));
        try
        {
            // Denied stays denied.
            var outsider = Client(throwing, _outsiderSub, "tester");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await outsider.GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);
            // Allowed stays allowed (read path writes no audit rows).
            var manager = Client(throwing, _managerSub, "qa-lead");
            Assert.Equal(HttpStatusCode.OK,
                (await manager.GetAsync($"/api/v1/projects/{_projectA}/audit/events")).StatusCode);
            // First-seen provisioning still succeeds end to end.
            var fresh = Client(throwing, $"authz-fresh-{Guid.NewGuid():N}", "viewer");
            Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/v1/auth/me")).StatusCode);
        }
        finally
        {
            throwing.Dispose();
        }
    }
}
