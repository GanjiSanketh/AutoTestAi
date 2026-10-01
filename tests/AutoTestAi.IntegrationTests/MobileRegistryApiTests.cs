using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 3C-1/3C-2: mobile registry API — auth matrix, project isolation,
/// validation, atomic default-slot creation, concurrency, audit hygiene.
/// No Appium, devices, or leasing involved.
/// </summary>
public sealed class MobileRegistryApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid AdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    public MobileRegistryApiTests(Slice1ApiFactory factory) => _factory = factory;

    private static HttpClient ClientFor(Slice1ApiFactory factory, string sub, string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = AdminRoleId, Name = "admin" },
                        new Role { Id = TesterRoleId, Name = "tester" });
                }
                var admin = new User { ExternalIdentityId = "ex-mob-admin", Email = "admin@x", DisplayName = "Admin" };
                var tester = new User { ExternalIdentityId = "ex-mob-tester", Email = "tester@x", DisplayName = "Tester" };
                db.Users.AddRange(admin, tester);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Mobile Alpha", Key = "MBA" },
                    new Project { Id = pb, Name = "Mobile Beta", Key = "MBB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = admin.Id, RoleId = AdminRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId });
                return Task.CompletedTask;
            });
            _seeded = true;
        }
        finally { _seedLock.Release(); }
    }

    private async Task<Guid> CreatePoolAsync(HttpClient admin, string name = "android-smoke", string platform = "android")
    {
        var response = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/pools", new
        {
            name,
            platform,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Pool_CRUD_RoundTrips()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-mob-admin", ["admin"]);
        var tester = ClientFor(_factory, "ex-mob-tester", ["tester"]);

        var poolId = await CreatePoolAsync(admin);

        var get = await tester.GetAsync($"/api/v1/projects/{_projectA}/mobile/pools/{poolId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var list = await tester.GetAsync($"/api/v1/projects/{_projectA}/mobile/pools");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var duplicate = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/pools", new
        {
            name = "android-smoke",
            platform = "android",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var badPlatform = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/pools", new
        {
            name = "other",
            platform = "windows-phone",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badPlatform.StatusCode);

        var forbidden = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/pools", new
        {
            name = "tester-pool",
            platform = "ios",
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var disable = await admin.PostAsync($"/api/v1/projects/{_projectA}/mobile/pools/{poolId}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        using var disabledDoc = JsonDocument.Parse(await disable.Content.ReadAsStringAsync());
        Assert.False(disabledDoc.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Pool_CrossProject_IsNotFound()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-mob-admin", ["admin"]);
        var poolId = await CreatePoolAsync(admin, "xpool");

        var crossGet = await admin.GetAsync($"/api/v1/projects/{_projectB}/mobile/pools/{poolId}");
        Assert.Equal(HttpStatusCode.NotFound, crossGet.StatusCode);

        var crossList = await admin.GetAsync($"/api/v1/projects/{_projectB}/mobile/pools");
        Assert.Equal(HttpStatusCode.OK, crossList.StatusCode);
        var body = await crossList.Content.ReadAsStringAsync();
        Assert.DoesNotContain("xpool", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_Register_CreatesDefaultSlot()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-mob-admin", ["admin"]);
        var poolId = await CreatePoolAsync(admin, "slot-pool");

        var register = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/devices", new
        {
            poolId,
            platform = "android",
            platformVersion = "14",
            manufacturer = "Google",
            model = "Pixel 8",
            udid = "emulator-5554",
            automationName = "UiAutomator2",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var registerBody = await register.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(registerBody);
        Assert.Equal(1, document.RootElement.GetProperty("slotCount").GetInt32());
        Assert.DoesNotContain("claimToken", registerBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", registerBody, StringComparison.OrdinalIgnoreCase);
        var deviceId = document.RootElement.GetProperty("id").GetGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var slots = await db.MobileDeviceSlots.Where(s => s.DeviceId == deviceId).ToListAsync();
            var slot = Assert.Single(slots);
            Assert.Equal(1, slot.SlotNumber);
            Assert.Equal(MobileSlotStatus.Free, slot.Status);
            Assert.Null(slot.ClaimToken);
            Assert.Null(slot.AssignmentId);
        }

        var duplicate = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/devices", new
        {
            poolId,
            platform = "android",
            udid = "emulator-5554",
            automationName = "UiAutomator2",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var mismatch = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/devices", new
        {
            poolId,
            platform = "ios",
            automationName = "XCUITest",
        });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    [Fact]
    public async Task Device_Disable_And_StaleUpdate()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-mob-admin", ["admin"]);
        var poolId = await CreatePoolAsync(admin, "disable-pool");
        var register = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/devices", new
        {
            poolId,
            platform = "android",
            model = "Pixel",
            automationName = "UiAutomator2",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var deviceId = JsonDocument.Parse(await register.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var disable = await admin.PostAsync($"/api/v1/projects/{_projectA}/mobile/devices/{deviceId}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Contains("Disabled", await disable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Give the row a version so the stale check has something to compare
        // (InMemory EF does not generate row versions by itself).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var row = await db.MobileDevices.FirstAsync(d => d.Id == deviceId);
            row.RowVersion = new byte[] { 5 };
            await db.SaveChangesAsync();
        }

        var stale = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/devices/{deviceId}", new
        {
            model = "Pixel 9",
            enabled = false,
            rowVersion = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task App_CRUD_ValidatesPlatformIdentity()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-mob-admin", ["admin"]);
        var tester = ClientFor(_factory, "ex-mob-tester", ["tester"]);

        var create = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/apps", new
        {
            platform = "android",
            name = "Shop",
            packageId = "com.example.shop",
            version = "1.2.3",
            storageKey = "mobile-apps/shop.apk",
            installPolicy = "Install",
            launchActivity = "com.example.shop.MainActivity",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var appId = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var missingPackage = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/apps", new
        {
            platform = "android",
            name = "Bad",
            installPolicy = "Preinstalled",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingPackage.StatusCode);

        var badUrl = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/apps", new
        {
            platform = "android",
            name = "Bad",
            packageId = "com.example.bad",
            storageKey = "https://evil.test/a.apk",
            installPolicy = "Install",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);

        var forbidden = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/mobile/apps", new
        {
            platform = "ios",
            name = "Nope",
            bundleId = "com.example.nope",
            installPolicy = "Preinstalled",
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var get = await tester.GetAsync($"/api/v1/projects/{_projectA}/mobile/apps/{appId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var crossGet = await admin.GetAsync($"/api/v1/projects/{_projectB}/mobile/apps/{appId}");
        Assert.Equal(HttpStatusCode.NotFound, crossGet.StatusCode);
    }

    [Fact]
    public async Task Registry_EmitsSafeAuditEvents()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-mob-admin", ["admin"]);
        var poolId = await CreatePoolAsync(admin, "audit-pool");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
        var events = await db.AuditEvents
            .Where(e => e.ProjectId == _projectA && e.Action.StartsWith("mobile.", StringComparison.Ordinal))
            .ToListAsync();
        Assert.Contains(events, e => e.Action == "mobile.pool_created");
        foreach (var audit in events)
        {
            Assert.DoesNotContain("secret", audit.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", audit.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("claimToken", audit.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
