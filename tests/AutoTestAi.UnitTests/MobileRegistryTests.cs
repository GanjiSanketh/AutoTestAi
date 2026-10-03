using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.Projects;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-1/3C-2: mobile pool/device/app validation, isolation,
/// defaults, and concurrency. No database, no Appium.</summary>
public sealed class MobileRegistryTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private sealed class AllowAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeStore : IMobileRegistryStore
    {
        public readonly Dictionary<Guid, MobileDevicePool> Pools = new();
        public readonly Dictionary<Guid, MobileDevice> Devices = new();
        public readonly List<MobileDeviceSlot> Slots = new();
        public readonly Dictionary<Guid, MobileApp> Apps = new();
        public bool ThrowDuplicateOnSave { get; set; }

        public Task<MobileDevicePool?> GetPoolByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Pools.TryGetValue(id, out var p) ? p : null);
        public Task<MobileDevicePool?> FindPoolByNameAsync(Guid projectId, string name, CancellationToken ct)
            => Task.FromResult(Pools.Values.FirstOrDefault(p => p.ProjectId == projectId && p.Name == name));
        public Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDevicePool>>(Pools.Values.Where(p => p.ProjectId == projectId).ToList());
        public Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct) { Pools[pool.Id] = pool; return Task.CompletedTask; }
        public Task<MobileDevice?> GetDeviceByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Devices.TryGetValue(id, out var d) ? d : null);
        public Task<MobileDevice?> FindDeviceByUdidAsync(Guid projectId, string udid, CancellationToken ct)
            => Task.FromResult(Devices.Values.FirstOrDefault(d => d.ProjectId == projectId && d.Udid == udid));
        public Task<IReadOnlyList<MobileDevice>> ListDevicesAsync(Guid projectId, Guid? poolId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDevice>>(Devices.Values.Where(d => d.ProjectId == projectId && (poolId == null || d.PoolId == poolId)).ToList());
        public Task<int> CountDevicesInPoolAsync(Guid poolId, CancellationToken ct)
            => Task.FromResult(Devices.Values.Count(d => d.PoolId == poolId));
        public Task AddDeviceAsync(MobileDevice device, CancellationToken ct) { Devices[device.Id] = device; return Task.CompletedTask; }
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsByDeviceAsync(Guid deviceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(Slots.Where(s => s.DeviceId == deviceId).ToList());
        public Task AddSlotAsync(MobileDeviceSlot slot, CancellationToken ct) { Slots.Add(slot); return Task.CompletedTask; }
        public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid slotId, CancellationToken ct)
            => Task.FromResult(Slots.FirstOrDefault(s => s.Id == slotId));
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsForClaimAsync(Guid projectId, Guid poolId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(Slots
                .Where(s => s.ProjectId == projectId && s.PoolId == poolId)
                .OrderBy(s => s.SlotNumber).ThenBy(s => s.Id).Take(take).ToList());
        public Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid assignmentId, CancellationToken ct)
            => Task.FromResult(Slots.FirstOrDefault(s => s.AssignmentId == assignmentId));
        public Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(Slots
                .Where(s => s.Status != MobileSlotStatus.Free && s.Status != MobileSlotStatus.Released &&
                            s.ClaimExpiresAt != null && s.ClaimExpiresAt <= now)
                .OrderBy(s => s.ClaimExpiresAt).Take(take).ToList());
        public Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileDeviceSlot>>(Slots
                .Where(s => s.Status == MobileSlotStatus.Released)
                .OrderBy(s => s.UpdatedAt).Take(take).ToList());
        public Task<MobileApp?> GetAppByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Apps.TryGetValue(id, out var a) ? a : null);
        public Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MobileApp>>(Apps.Values.Where(a => a.ProjectId == projectId).ToList());
        public Task AddAppAsync(MobileApp app, CancellationToken ct) { Apps[app.Id] = app; return Task.CompletedTask; }
        public Task<MobileDeviceSession?> GetSessionByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDeviceSession?> FindSessionByAssignmentAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSession>> ListStaleSessionsAsync(DateTimeOffset s, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task AddSessionAsync(MobileDeviceSession session, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct)
        {
            if (ThrowDuplicateOnSave)
                throw new InvalidOperationException("duplicate key value violates unique constraint");
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public FakeStore Store = new();
        public FakeAudit Audit = new();
        public MobilePoolService Pools = null!;
        public MobileDeviceService Devices = null!;
        public MobileAppService Apps = null!;

        public Fixture()
        {
            Pools = new MobilePoolService(Store, new AllowAuth(), new FixedClock(), Audit);
            Devices = new MobileDeviceService(Store, new AllowAuth(), new FixedClock(), Audit);
            Apps = new MobileAppService(Store, new AllowAuth(), new FixedClock(), Audit);
        }

        public async Task<Guid> AddPoolAsync(string name = "android-smoke", string platform = "android")
            => (await Pools.CreateAsync(new CreateMobilePoolCommand(ProjectA, name, platform), CancellationToken.None)).Id;
    }

    // ---------- pools ----------

    [Fact]
    public async Task Pool_Create_Valid()
    {
        var f = new Fixture();
        var pool = await f.Pools.CreateAsync(new CreateMobilePoolCommand(ProjectA, "android-smoke", "Android"), CancellationToken.None);
        Assert.Equal("Android", pool.Platform);
        Assert.True(pool.Enabled);
        Assert.Equal(0, pool.DeviceCount);
        Assert.Contains(f.Audit.Actions, a => a == "mobile.pool_created");
    }

    [Fact]
    public async Task Pool_RejectsInvalidPlatform()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Pools.CreateAsync(new CreateMobilePoolCommand(ProjectA, "x", "windows-phone"), CancellationToken.None));
    }

    [Fact]
    public async Task Pool_DuplicateName_Conflicts()
    {
        var f = new Fixture();
        await f.AddPoolAsync("dup");
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Pools.CreateAsync(new CreateMobilePoolCommand(ProjectA, "dup", "android"), CancellationToken.None));
    }

    [Fact]
    public async Task Pool_DuplicateRace_ConvergesToConflict()
    {
        var f = new Fixture();
        f.Store.ThrowDuplicateOnSave = true;
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Pools.CreateAsync(new CreateMobilePoolCommand(ProjectA, "race", "ios"), CancellationToken.None));
    }

    [Fact]
    public async Task Pool_EnableDisable_Audited()
    {
        var f = new Fixture();
        var id = await f.AddPoolAsync();
        var disabled = await f.Pools.UpdateAsync(ProjectA, new UpdateMobilePoolCommand(id, "android-smoke", false, null), CancellationToken.None);
        Assert.False(disabled.Enabled);
        var enabled = await f.Pools.UpdateAsync(ProjectA, new UpdateMobilePoolCommand(id, "android-smoke", true, null), CancellationToken.None);
        Assert.True(enabled.Enabled);
        Assert.Contains(f.Audit.Actions, a => a == "mobile.pool_disabled");
        Assert.Contains(f.Audit.Actions, a => a == "mobile.pool_enabled");
    }

    [Fact]
    public async Task Pool_StaleRowVersion_Rejected()
    {
        var f = new Fixture();
        var id = await f.AddPoolAsync();
        f.Store.Pools[id].RowVersion = new byte[] { 1, 2, 3 };
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Pools.UpdateAsync(ProjectA, new UpdateMobilePoolCommand(id, "android-smoke", true, new byte[] { 9 }), CancellationToken.None));
    }

    // ---------- devices ----------

    private async Task<Guid> AddDeviceAsync(Fixture f, Guid pool, string udid = "emulator-5554")
    {
        var device = await f.Devices.RegisterAsync(new RegisterMobileDeviceCommand(
            ProjectA, pool, "android", "14", "Google", "Pixel 8", udid, "UiAutomator2"), CancellationToken.None);
        return device.Id;
    }

    [Fact]
    public async Task Device_Register_CreatesExactlyOneFreeSlot()
    {
        var f = new Fixture();
        var pool = await f.AddPoolAsync();
        var device = await f.Devices.RegisterAsync(new RegisterMobileDeviceCommand(
            ProjectA, pool, "android", "14", "Google", "Pixel 8", "emulator-5554", "UiAutomator2"), CancellationToken.None);
        Assert.Equal("Available", device.Status);
        Assert.True(device.Enabled);
        Assert.Equal(1, device.SlotCount);
        var slots = await f.Store.ListSlotsByDeviceAsync(device.Id, CancellationToken.None);
        var slot = Assert.Single(slots);
        Assert.Equal(1, slot.SlotNumber);
        Assert.Equal(MobileSlotStatus.Free, slot.Status);
        Assert.Null(slot.ClaimToken);
        Assert.Null(slot.AssignmentId);
        Assert.Equal(ProjectA, slot.ProjectId);
        Assert.Equal(pool, slot.PoolId);
        Assert.Equal(device.Id, slot.DeviceId);
        Assert.Contains(f.Audit.Actions, a => a == "mobile.device_registered");
    }

    [Fact]
    public async Task Device_RegisterDuplicateRace_ConvergesToConflict()
    {
        var f = new Fixture();
        var pool = await f.AddPoolAsync();
        f.Store.ThrowDuplicateOnSave = true;
        await Assert.ThrowsAsync<ConflictException>(() => AddDeviceAsync(f, pool));
        // Rollback itself is EF-transactional (single SaveChanges unit);
        // the fake store cannot roll back, so only convergence is asserted here.
    }

    [Fact]
    public async Task Device_RejectsCrossProjectPool()
    {
        var f = new Fixture();
        var otherPool = await f.Pools.CreateAsync(new CreateMobilePoolCommand(ProjectB, "other", "android"), CancellationToken.None);
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Devices.RegisterAsync(new RegisterMobileDeviceCommand(
                ProjectA, otherPool.Id, "android", null, null, null, null, "UiAutomator2"), CancellationToken.None));
    }

    [Fact]
    public async Task Device_RejectsPlatformMismatch()
    {
        var f = new Fixture();
        var pool = await f.AddPoolAsync("ios-pool", "ios");
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Devices.RegisterAsync(new RegisterMobileDeviceCommand(
                ProjectA, pool, "android", null, null, null, null, "UiAutomator2"), CancellationToken.None));
    }

    [Fact]
    public async Task Device_RejectsAutomationMismatch()
    {
        var f = new Fixture();
        var pool = await f.AddPoolAsync();
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Devices.RegisterAsync(new RegisterMobileDeviceCommand(
                ProjectA, pool, "android", null, null, null, null, "XCUITest"), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Devices.RegisterAsync(new RegisterMobileDeviceCommand(
                ProjectA, pool, "android", null, null, null, null, "Espresso"), CancellationToken.None));
    }

    [Fact]
    public async Task Device_RejectsDuplicateUdid()
    {
        var f = new Fixture();
        var pool = await f.AddPoolAsync();
        await AddDeviceAsync(f, pool, "dup-udid");
        await Assert.ThrowsAsync<ConflictException>(() => AddDeviceAsync(f, pool, "dup-udid"));
    }

    [Fact]
    public async Task Device_DisableEnable_TogglesAvailable()
    {
        var f = new Fixture();
        var pool = await f.AddPoolAsync();
        var id = await AddDeviceAsync(f, pool);
        var disabled = await f.Devices.UpdateAsync(ProjectA, new UpdateMobileDeviceCommand(
            id, null, null, null, null, null, false, null), CancellationToken.None);
        Assert.Equal("Disabled", disabled.Status);
        var enabled = await f.Devices.UpdateAsync(ProjectA, new UpdateMobileDeviceCommand(
            id, null, null, null, null, null, true, null), CancellationToken.None);
        Assert.Equal("Available", enabled.Status);
    }

    [Fact]
    public async Task Device_RejectsDisabledPool()
    {
        var f = new Fixture();
        var pool = await f.AddPoolAsync();
        await f.Pools.UpdateAsync(ProjectA, new UpdateMobilePoolCommand(pool, "android-smoke", false, null), CancellationToken.None);
        await Assert.ThrowsAsync<ValidationException>(() => AddDeviceAsync(f, pool));
    }

    // ---------- apps ----------

    [Fact]
    public async Task App_ValidAndroid()
    {
        var f = new Fixture();
        var app = await f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "android", "Shop", "com.example.shop", null, "1.2.3",
            "mobile-apps/shop.apk", "Install", "com.example.shop.MainActivity", null), CancellationToken.None);
        Assert.Equal("com.example.shop", app.PackageId);
        Assert.Null(app.BundleId);
        Assert.True(app.HasBinary);
        Assert.Contains(f.Audit.Actions, a => a == "mobile.app_created");
    }

    [Fact]
    public async Task App_ValidIos_Preinstalled()
    {
        var f = new Fixture();
        var app = await f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "ios", "Shop", null, "com.example.shop", null,
            null, "Preinstalled", null, null), CancellationToken.None);
        Assert.Equal("com.example.shop", app.BundleId);
        Assert.False(app.HasBinary);
    }

    [Fact]
    public async Task App_RequiresPlatformIdentity()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "android", "Bad", null, null, null, null, "Preinstalled", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "ios", "Bad", null, null, null, null, "Preinstalled", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task App_RejectsCrossPlatformIdentity()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "android", "Bad", "com.x", "com.x", null, null, "Preinstalled", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "ios", "Bad", "com.x", "com.x", null, null, "Preinstalled", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task App_RejectsLaunchActivityOnIos()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "ios", "Bad", null, "com.x", null, null, "Preinstalled", "Main", null), CancellationToken.None));
    }

    [Fact]
    public async Task App_RejectsBadPolicyStorageAndDeepLink()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "android", "Bad", "com.x", null, null, null, "SideLoad", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "android", "Bad", "com.x", null, null, "https://evil.test/a.apk?token=1", "Install", null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "android", "Bad", "com.x", null, null, null, "Install", null, "javascript:alert(1)"), CancellationToken.None));
    }

    [Fact]
    public async Task App_StaleRowVersion_Rejected()
    {
        var f = new Fixture();
        var app = await f.Apps.CreateAsync(new CreateMobileAppCommand(
            ProjectA, "android", "Shop", "com.example.shop", null, null, null, "Preinstalled", null, null), CancellationToken.None);
        f.Store.Apps[app.Id].RowVersion = new byte[] { 7 };
        await Assert.ThrowsAsync<ConflictException>(() => f.Apps.UpdateAsync(ProjectA, new UpdateMobileAppCommand(
            app.Id, "Shop", "com.example.shop", null, null, null, "Preinstalled", null, null, new byte[] { 8 }), CancellationToken.None));
    }
}
