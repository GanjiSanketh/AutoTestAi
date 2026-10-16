using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 13: project SLA targets and current open-defect breach
/// reporting — validation, authorization matrix, audit behavior, breach math
/// against the real store, and isolation. Boundary timing (strictly greater
/// than the threshold) is exercised with margins around the live clock.
/// </summary>
public sealed class SlaTargetApiTests : IClassFixture<Slice1ApiFactory>, IDisposable
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public SlaTargetApiTests(Slice1ApiFactory factory) => _factory = factory;

    public void Dispose() => _seedLock.Dispose();

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
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var admin = new User { ExternalIdentityId = "sla-admin", Email = "a@x", DisplayName = "A" };
                var viewer = new User { ExternalIdentityId = "sla-viewer", Email = "v@x", DisplayName = "V" };
                var outsider = new User { ExternalIdentityId = "sla-outsider", Email = "o@x", DisplayName = "O" };
                db.Users.AddRange(admin, viewer, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "SLA Alpha", Key = "SLA" },
                    new Project { Id = pb, Name = "SLA Beta", Key = "SLB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = admin.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });
                await db.SaveChangesAsync();
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

    private static async Task<Guid> SeedDefectAsync(
        AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext db,
        Guid projectId, Severity severity, DefectStatus status, DateTimeOffset createdAt)
    {
        var defect = new Defect
        {
            ProjectId = projectId,
            Title = $"{severity} defect",
            Severity = severity,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Defects.Add(defect);
        await db.SaveChangesAsync();
        return defect.Id;
    }

    private static JsonElement Prop(JsonDocument doc, params string[] path)
    {
        var current = doc.RootElement;
        foreach (var segment in path)
            current = current.GetProperty(segment);
        return current;
    }

    private static object ValidBody() => new { criticalDays = 1, highDays = 7, mediumDays = 14, lowDays = 30 };

    private async Task<HttpResponseMessage> PutTargetsAsync(HttpClient client, Guid projectId, object body)
        => await client.PutAsJsonAsync($"/api/v1/projects/{projectId}/sla-targets", body);

    private async Task<int> AuditCountAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        return await db.AuditEvents.CountAsync(a => a.Action == action && a.ProjectId == _projectA);
    }

    // ---------- reads ----------

    [Fact]
    public async Task Get_Unconfigured_ReturnsNullTargetsAndCompliance()
    {
        await SeedOnceAsync();
        var response = await Client("sla-viewer", ["viewer"])
            .GetAsync($"/api/v1/projects/{_projectA}/sla-targets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(JsonValueKind.Null, Prop(doc!, "targets").ValueKind);
        Assert.Equal(JsonValueKind.Null, Prop(doc!, "compliance").ValueKind);
    }

    [Fact]
    public async Task Get_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync($"/api/v1/projects/{_projectA}/sla-targets")).StatusCode);
    }

    [Fact]
    public async Task Get_Outsider_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("sla-outsider", ["viewer"])
                .GetAsync($"/api/v1/projects/{_projectA}/sla-targets")).StatusCode);
    }

    // ---------- upsert ----------

    [Fact]
    public async Task Put_Admin_Creates_ThenUpdates_Idempotently()
    {
        await SeedOnceAsync();
        var admin = Client("sla-admin", ["admin"]);

        var created = await PutTargetsAsync(admin, _projectA, ValidBody());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdDoc = await created.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(createdDoc!, "criticalDays").GetInt32());
        Assert.Equal(30, Prop(createdDoc!, "lowDays").GetInt32());
        Assert.Equal(1, await AuditCountAsync("sla.target_configured"));

        var updated = await PutTargetsAsync(admin, _projectA,
            new { criticalDays = 2, highDays = 7, mediumDays = 14, lowDays = 30 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedDoc = await updated.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, Prop(updatedDoc!, "criticalDays").GetInt32());
        Assert.Equal(1, await AuditCountAsync("sla.target_updated"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        Assert.Equal(1, await db.SlaTargetPolicies.CountAsync(p => p.ProjectId == _projectA));
    }

    [Fact]
    public async Task Put_RejectsInvalidThresholds_WithoutPersistingOrAuditing()
    {
        await SeedOnceAsync();
        var admin = Client("sla-admin", ["admin"]);

        foreach (var body in new object[]
        {
            new { criticalDays = 0, highDays = 7, mediumDays = 14, lowDays = 30 },
            new { criticalDays = 1, highDays = 366, mediumDays = 14, lowDays = 30 },
            new { criticalDays = 1, highDays = 7, mediumDays = 14 },
            new { },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await PutTargetsAsync(admin, _projectA, body)).StatusCode);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        Assert.Equal(0, await db.SlaTargetPolicies.CountAsync(p => p.ProjectId == _projectA));
        Assert.Equal(0, await AuditCountAsync("sla.target_configured"));
        Assert.Equal(0, await AuditCountAsync("sla.target_updated"));
    }

    [Fact]
    public async Task Put_ViewerForbidden_QaLeadForbidden()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutTargetsAsync(Client("sla-viewer", ["viewer"]), _projectA, ValidBody())).StatusCode);
        // qa-lead holds broad project permissions but not settings.manage.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("sla-admin", ["qa-lead"])
                .PutAsJsonAsync($"/api/v1/projects/{_projectA}/sla-targets", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Put_BodyCannotOverrideProject()
    {
        await SeedOnceAsync();
        var admin = Client("sla-admin", ["admin"]);
        // Unknown extra project field is ignored; the route project wins.
        var response = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/sla-targets",
            new { projectId = _projectB, criticalDays = 1, highDays = 7, mediumDays = 14, lowDays = 30 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        Assert.Equal(1, await db.SlaTargetPolicies.CountAsync(p => p.ProjectId == _projectA));
        Assert.Equal(0, await db.SlaTargetPolicies.CountAsync(p => p.ProjectId == _projectB));
    }

    [Fact]
    public async Task CrossProject_Isolated()
    {
        await SeedOnceAsync();
        var admin = Client("sla-admin", ["admin"]);
        // Admin bypasses membership; a non-member manager-style caller cannot read B.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("sla-viewer", ["viewer"])
                .GetAsync($"/api/v1/projects/{_projectB}/sla-targets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/v1/projects/{_projectB}/sla-targets")).StatusCode);
    }

    // ---------- breach evaluation ----------

    [Fact]
    public async Task Get_EvaluatesBreaches_WithStrictBoundary()
    {
        await SeedOnceAsync();
        var admin = Client("sla-admin", ["admin"]);
        Assert.Equal(HttpStatusCode.OK,
            (await PutTargetsAsync(admin, _projectA, ValidBody())).StatusCode);

        var now = DateTimeOffset.UtcNow;
        await _factory.SeedAsync(async db =>
        {
            // Critical (1d): one breached, one compliant with margin.
            await SeedDefectAsync(db, _projectA, Severity.Critical, DefectStatus.Open, now.AddDays(-2));
            await SeedDefectAsync(db, _projectA, Severity.Critical, DefectStatus.InProgress, now.AddHours(-12));
            // High (7d): just inside the boundary stays compliant; just outside breaches.
            await SeedDefectAsync(db, _projectA, Severity.High, DefectStatus.Open, now.AddDays(-7).AddMinutes(5));
            await SeedDefectAsync(db, _projectA, Severity.High, DefectStatus.Open, now.AddDays(-7).AddMinutes(-5));
            // Resolved defects never evaluate, however old.
            await SeedDefectAsync(db, _projectA, Severity.Low, DefectStatus.Resolved, now.AddDays(-100));
            // Medium/Low open rows keep every severity slice present.
            await SeedDefectAsync(db, _projectA, Severity.Medium, DefectStatus.Open, now.AddDays(-1));
            await SeedDefectAsync(db, _projectA, Severity.Low, DefectStatus.Open, now.AddDays(-1));
            await db.SaveChangesAsync();
        });

        var response = await Client("sla-viewer", ["viewer"])
            .GetAsync($"/api/v1/projects/{_projectA}/sla-targets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();

        var compliance = Prop(doc!, "compliance");
        Assert.Equal(6, compliance.GetProperty("eligible").GetInt32());
        Assert.Equal(2, compliance.GetProperty("breached").GetInt32());
        Assert.Equal(4, compliance.GetProperty("compliant").GetInt32());
        Assert.Equal(66.7, compliance.GetProperty("compliancePercent").GetDouble(), precision: 1);
        Assert.Equal(0, compliance.GetProperty("unconfiguredSeverityCount").GetInt32());
        Assert.Equal(4, compliance.GetProperty("bySeverity").GetArrayLength());
        var critical = compliance.GetProperty("bySeverity").EnumerateArray()
            .First(e => e.GetProperty("severity").GetString() == "Critical");
        Assert.Equal(2, critical.GetProperty("eligible").GetInt32());
        Assert.Equal(1, critical.GetProperty("breached").GetInt32());
        Assert.Equal(1, critical.GetProperty("thresholdDays").GetInt32());
    }

    [Fact]
    public async Task Get_ZeroEligible_ReturnsNullPercent()
    {
        await SeedOnceAsync();
        var admin = Client("sla-admin", ["admin"]);
        Assert.Equal(HttpStatusCode.OK,
            (await PutTargetsAsync(admin, _projectA, ValidBody())).StatusCode);

        var response = await Client("sla-viewer", ["viewer"])
            .GetAsync($"/api/v1/projects/{_projectA}/sla-targets");
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var compliance = Prop(doc!, "compliance");
        Assert.Equal(0, compliance.GetProperty("eligible").GetInt32());
        Assert.Equal(JsonValueKind.Null, compliance.GetProperty("compliancePercent").ValueKind);
    }
}
