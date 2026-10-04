using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-4A: mobile start-path validation, execution target
/// population, and server-side capability construction.</summary>
public sealed class MobileStartPathTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    // ---------- fakes (mirror TestExecutionServiceTests conventions) ----------

    private sealed class FakeExecutionStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.Id == id));
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Tests.FirstOrDefault(t => t.Id == id));
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Tests.Where(t => t.ExecutionId == id).ToList());
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid projectId, string key, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.ProjectId == projectId && e.IdempotencyKey == key));
        public Task AddExecutionAsync(Execution e, CancellationToken ct) { Executions.Add(e); return Task.CompletedTask; }
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) { Tests.Add(t); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int skip, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionListRow>>(Array.Empty<ExecutionListRow>());
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionStepResult>>(Array.Empty<ExecutionStepResult>());
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? afterId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionLog>>(Array.Empty<ExecutionLog>());
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteLogsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Array.Empty<ExecutionArtifact>());
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<ExecutionArtifact?>(null);
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FailureAnalysis>>(Array.Empty<FailureAnalysis>());
        public Task<FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<FailureAnalysis?>(null);
        public Task AddAnalysisAsync(FailureAnalysis a, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCases : ITestCaseStore
    {
        public readonly List<TestCase> Cases = new();
        public readonly List<TestCaseVersion> Versions = new();
        public Task<TestCase?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Cases.FirstOrDefault(c => c.Id == id));
        public Task<TestCaseVersion?> GetVersionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Versions.FirstOrDefault(v => v.Id == id));
        public Task<int> CountAsync(Guid p, string? s, TestCaseStatusFilter f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TestCase>> ListAsync(Guid p, string? s, TestCaseStatusFilter f, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCase?> GetByKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddTestCaseAsync(TestCase c, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseVersion> AddNextVersionAsync(Guid id, Func<int, TestCaseVersion> f, CancellationToken ct) => throw new NotImplementedException();
        public Task AddVersionAsync(TestCaseVersion v, CancellationToken ct) => throw new NotImplementedException();
    }

    // NOTE: project storage uses the shared internal FakeProjectStore
    // (ProjectServiceTests.cs); no duplicate fake is defined here.

    private sealed class FakeMobileRegistry : IMobileRegistryStore
    {
        public readonly Dictionary<Guid, MobileDevicePool> Pools = new();
        public readonly Dictionary<Guid, MobileApp> Apps = new();
        public Task<MobileDevicePool?> GetPoolByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Pools.TryGetValue(id, out var p) ? p : null);
        public Task<MobileApp?> GetAppByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Apps.TryGetValue(id, out var a) ? a : null);
        public Task<MobileDevicePool?> FindPoolByNameAsync(Guid p, string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDevicePool>> ListPoolsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddPoolAsync(MobileDevicePool pool, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDevice?> GetDeviceByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDevice?> FindDeviceByUdidAsync(Guid p, string u, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDevice>> ListDevicesAsync(Guid p, Guid? pool, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountDevicesInPoolAsync(Guid pool, CancellationToken ct) => throw new NotImplementedException();
        public Task AddDeviceAsync(MobileDevice d, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsByDeviceAsync(Guid d, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDeviceSlot?> GetSlotByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListSlotsForClaimAsync(Guid p, Guid pool, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDeviceSlot?> FindSlotByAssignmentAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListExpiredSlotsAsync(DateTimeOffset now, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSlot>> ListReleasedSlotsAsync(int take, CancellationToken ct) => throw new NotImplementedException();
        public Task AddSlotAsync(MobileDeviceSlot s, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileApp>> ListAppsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAppAsync(MobileApp a, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDeviceSession?> GetSessionByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<MobileDeviceSession?> FindSessionByAssignmentAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MobileDeviceSession>> ListStaleSessionsAsync(DateTimeOffset s, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task AddSessionAsync(MobileDeviceSession session, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AllowAllAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAudit : IAuditService
    {
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeCoordinator : IExecutionWorkflowCoordinator
    {
        public bool IsConfigured { get; set; } = true;
        public Task<string> StartAsync(Guid e, Guid p, CancellationToken ct) => Task.FromResult($"wf-test-{e:N}");
        public Task<bool> CancelAsync(string workflowId, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeEvents : IExecutionEventPublisher
    {
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured => true;
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<byte[]> DownloadAsync(string key, int maxBytes, CancellationToken ct)
            => throw new NotFoundException("Stored object not found.");
    }

    private sealed class FakeUser : ICurrentUserService
    {
        public bool IsAuthenticated => true;
        public string? ExternalIdentityId => "user-1";
        public string? Email => "u@x";
        public string? DisplayName => "U";
        public IReadOnlyCollection<string> Roles => ["tester"];
        public IReadOnlyCollection<string> Permissions => RolePermissions.Resolve(["tester"]);
    }

    private sealed class FakeUsers : IUserDirectory
    {
        public Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken ct)
            => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<Guid> EnsureProvisionedAsync(string e, string? email, string? d, CancellationToken ct)
            => Task.FromResult(Guid.NewGuid());
    }

    private sealed record Fixture(
        TestExecutionService Service,
        FakeExecutionStore Store,
        FakeMobileRegistry Mobile,
        Guid WebVersionId,
        Guid AppiumVersionId,
        Guid PoolId,
        Guid AppId);

    private static Fixture Create(bool withMobileRegistry = true)
    {
        var store = new FakeExecutionStore();
        var cases = new FakeCases();
        var projects = new FakeProjectStore();
        projects.Projects.Add(new Project { Id = ProjectA, Name = "A", Key = "A" });
        projects.Projects.Add(new Project { Id = ProjectB, Name = "B", Key = "B" });
        var mobile = new FakeMobileRegistry();

        var webCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "WEB-001", Title = "Web",
            Framework = "playwright", Platform = "web",
            Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        var appiumCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "MOB-001", Title = "Mobile",
            Framework = "appium", Platform = "android",
            Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.AddRange(new[] { webCase, appiumCase });
        var webVersion = new TestCaseVersion
        {
            TestCaseId = webCase.Id, VersionNumber = 1,
            StructuredSteps = JsonDocument.Parse("""[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
            ReviewStatus = ReviewStatus.Approved,
        };
        var appiumVersion = new TestCaseVersion
        {
            TestCaseId = appiumCase.Id, VersionNumber = 1,
            StructuredSteps = JsonDocument.Parse("""[{"order":1,"action":"launchApp"}]"""),
            ReviewStatus = ReviewStatus.Approved,
        };
        cases.Versions.AddRange(new[] { webVersion, appiumVersion });

        var poolId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        mobile.Pools[poolId] = new MobileDevicePool
        {
            Id = poolId, ProjectId = ProjectA, Name = "android-smoke",
            Platform = MobilePlatform.Android, Status = MobilePoolStatus.Active,
        };
        mobile.Apps[appId] = new MobileApp
        {
            Id = appId, ProjectId = ProjectA, Platform = MobilePlatform.Android,
            Name = "Shop", PackageId = "com.example.shop", InstallPolicy = MobileInstallPolicy.Preinstalled,
        };

        var service = new TestExecutionService(
            store, cases, projects, new FakeUser(), new AllowAllAuth(), new FakeUsers(),
            new SystemDateTimeProvider(), new FakeAudit(), new FakeEvents(), new FakeCoordinator(),
            new FakeArtifacts(), mobileRegistry: withMobileRegistry ? mobile : null);
        return new Fixture(service, store, mobile, webVersion.Id, appiumVersion.Id, poolId, appId);
    }

    private static StartExecutionCommand AppiumStart(
        Guid versionId, Guid? poolId = null, Guid? appId = null, string? browser = null) =>
        new(ProjectA, versionId, null, browser, null, null, null, null, null, poolId, appId);

    // ---------- start-path validation ----------

    [Fact]
    public async Task WebExecution_BehaviorUnchanged_DefaultsChromium()
    {
        var f = Create();
        var result = await f.Service.StartAsync(
            new StartExecutionCommand(ProjectA, f.WebVersionId), CancellationToken.None);
        Assert.False(result.Duplicated);
        var test = Assert.Single(f.Store.Tests);
        Assert.Equal("chromium", test.Browser);
        var execution = Assert.Single(f.Store.Executions);
        Assert.Null(execution.MobileDevicePoolId);
        Assert.Null(execution.MobileAppId);
        Assert.Null(execution.MobileDeviceSessionId);
    }

    [Fact]
    public async Task Appium_WithoutPool_Fails()
    {
        var f = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.StartAsync(AppiumStart(f.AppiumVersionId, appId: f.AppId), CancellationToken.None));
        Assert.Empty(f.Store.Executions);
    }

    [Fact]
    public async Task Appium_WithoutApp_Fails()
    {
        var f = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.StartAsync(AppiumStart(f.AppiumVersionId, poolId: f.PoolId), CancellationToken.None));
        Assert.Empty(f.Store.Executions);
    }

    [Fact]
    public async Task Appium_CrossProjectPool_Fails()
    {
        var f = Create();
        f.Mobile.Pools[f.PoolId].ProjectId = ProjectB;
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.StartAsync(AppiumStart(f.AppiumVersionId, f.PoolId, f.AppId), CancellationToken.None));
        Assert.Empty(f.Store.Executions);
    }

    [Fact]
    public async Task Appium_CrossProjectApp_Fails()
    {
        var f = Create();
        f.Mobile.Apps[f.AppId].ProjectId = ProjectB;
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.StartAsync(AppiumStart(f.AppiumVersionId, f.PoolId, f.AppId), CancellationToken.None));
        Assert.Empty(f.Store.Executions);
    }

    [Fact]
    public async Task Appium_DisabledPool_Fails()
    {
        var f = Create();
        f.Mobile.Pools[f.PoolId].Status = MobilePoolStatus.Disabled;
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.StartAsync(AppiumStart(f.AppiumVersionId, f.PoolId, f.AppId), CancellationToken.None));
        Assert.Empty(f.Store.Executions);
    }

    [Fact]
    public async Task Appium_WithoutRegistry_Fails()
    {
        var f = Create(withMobileRegistry: false);
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.StartAsync(AppiumStart(f.AppiumVersionId, f.PoolId, f.AppId), CancellationToken.None));
        Assert.Empty(f.Store.Executions);
    }

    [Fact]
    public async Task ValidAppiumStart_PopulatesRefs_AndNullsBrowser()
    {
        var f = Create();
        var result = await f.Service.StartAsync(
            AppiumStart(f.AppiumVersionId, f.PoolId, f.AppId, browser: "chromium"), CancellationToken.None);
        Assert.False(result.Duplicated);
        var execution = Assert.Single(f.Store.Executions);
        Assert.Equal(f.PoolId, execution.MobileDevicePoolId);
        Assert.Equal(f.AppId, execution.MobileAppId);
        Assert.Null(execution.MobileDeviceSessionId);
        var test = Assert.Single(f.Store.Tests);
        Assert.Equal("appium", test.Framework);
        Assert.Null(test.Browser);
    }

    [Fact]
    public async Task WebExecution_WithMobileRefs_Fails()
    {
        var f = Create();
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.StartAsync(
                new StartExecutionCommand(ProjectA, f.WebVersionId, null, null, null, null, null, null, null, f.PoolId, f.AppId),
                CancellationToken.None));
        Assert.Empty(f.Store.Executions);
    }

    // ---------- capability builder ----------

    private static (MobileDevice Device, MobileApp App) AndroidPair(MobileInstallPolicy policy = MobileInstallPolicy.Preinstalled)
    {
        var device = new MobileDevice
        {
            ProjectId = ProjectA, PoolId = Guid.NewGuid(), Platform = MobilePlatform.Android,
            PlatformVersion = "14", Manufacturer = "Google", Model = "Pixel 8",
            Udid = "emulator-5554", AutomationName = MobileAutomationNames.UiAutomator2,
            Status = MobileDeviceStatus.Available,
        };
        var app = new MobileApp
        {
            ProjectId = ProjectA, Platform = MobilePlatform.Android, Name = "Shop",
            PackageId = "com.example.shop", Version = "1.2.3",
            StorageKey = policy == MobileInstallPolicy.Preinstalled ? null : "mobile-apps/shop.apk",
            InstallPolicy = policy, LaunchActivity = "com.example.shop.MainActivity",
        };
        return (device, app);
    }

    private static MobileCapabilityBuilder Builder()
        => new(Options.Create(new MobileOptions()));

    [Fact]
    public void Builder_AndroidPreinstalled_MapsPlatformAutomationAndReset()
    {
        var (device, app) = AndroidPair();
        var caps = Builder().Build(device, app);
        Assert.Equal("Android", caps.PlatformName);
        Assert.Equal("UiAutomator2", caps.AutomationName);
        Assert.Equal("Pixel 8", caps.DeviceName);
        Assert.Equal("emulator-5554", caps.Udid);
        Assert.Equal("com.example.shop", caps.AppPackage);
        Assert.Equal("com.example.shop.MainActivity", caps.AppActivity);
        Assert.Null(caps.BundleId);
        Assert.Null(caps.App);
        Assert.True(caps.NoReset);
        Assert.False(caps.FullReset);
        Assert.Equal(120, caps.NewCommandTimeout);
    }

    [Fact]
    public void Builder_Install_RequiresDownloadUrl()
    {
        var (device, app) = AndroidPair(MobileInstallPolicy.Install);
        var ex = Assert.Throws<ValidationException>(() => Builder().Build(device, app));
        Assert.Contains("app", ex.Message, StringComparison.OrdinalIgnoreCase);
        var caps = Builder().Build(device, app, "https://artifacts.example/mobile-apps/shop.apk?exp=900");
        Assert.False(caps.NoReset);
        Assert.False(caps.FullReset);
        Assert.Equal("https://artifacts.example/mobile-apps/shop.apk?exp=900", caps.App);
    }

    [Fact]
    public void Builder_Reinstall_SetsFullReset()
    {
        var (device, app) = AndroidPair(MobileInstallPolicy.Reinstall);
        var caps = Builder().Build(device, app, "https://artifacts.example/mobile-apps/shop.apk?exp=900");
        Assert.False(caps.NoReset);
        Assert.True(caps.FullReset);
    }

    [Fact]
    public void Builder_IosStructured_MapsBundleId()
    {
        var device = new MobileDevice
        {
            ProjectId = ProjectA, PoolId = Guid.NewGuid(), Platform = MobilePlatform.Ios,
            Model = "iPhone 15", Udid = "00008110-0000000000000000",
            AutomationName = MobileAutomationNames.XcUiTest, Status = MobileDeviceStatus.Available,
        };
        var app = new MobileApp
        {
            ProjectId = ProjectA, Platform = MobilePlatform.Ios, Name = "Shop",
            BundleId = "com.example.shop", InstallPolicy = MobileInstallPolicy.Preinstalled,
        };
        var caps = Builder().Build(device, app);
        Assert.Equal("iOS", caps.PlatformName);
        Assert.Equal("XCUITest", caps.AutomationName);
        Assert.Equal("com.example.shop", caps.BundleId);
        Assert.Null(caps.AppPackage);
        Assert.Null(caps.AppActivity);
    }

    [Fact]
    public void Builder_RejectsMismatch_UnsupportedAutomation_BadUrl()
    {
        var (device, app) = AndroidPair();
        app.Platform = MobilePlatform.Ios;
        Assert.Throws<ValidationException>(() => Builder().Build(device, app));

        var badAutomation = new MobileDevice
        {
            ProjectId = ProjectA, PoolId = device.PoolId, Platform = MobilePlatform.Android,
            AutomationName = "Espresso", Status = MobileDeviceStatus.Available,
        };
        var (_, goodApp) = AndroidPair();
        Assert.Throws<ValidationException>(() => Builder().Build(badAutomation, goodApp));

        var (device2, app2) = AndroidPair(MobileInstallPolicy.Install);
        Assert.Throws<ValidationException>(() => Builder().Build(device2, app2, "http://insecure.example/app.apk"));
    }

    [Fact]
    public void MobileAssignmentDto_CarriesAssignmentToken_NotClaimToken()
    {
        Assert.Null(typeof(MobileAssignmentDto).GetProperty("ClaimToken"));
        Assert.NotNull(typeof(MobileAssignmentDto).GetProperty("AssignmentToken"));
        var assignment = new MobileAssignmentDto(
            "a1", "e1", "appium", "android",
            new MobileDeviceTargetDto("emulator-5554", "Pixel 8", "14"),
            new MobileAppTargetDto("com.example.shop", null, "1.2.3", "Preinstalled", null, null, null),
            new MobileCapabilitiesDto("Android", "UiAutomator2", "Pixel 8", "emulator-5554",
                "com.example.shop", null, null, null, true, false, 120),
            Array.Empty<MobileStepDto>(),
            new MobileTimeoutsDto(300000, 30000),
            true,
            Guid.NewGuid());
        Assert.NotEqual(Guid.Empty, assignment.AssignmentToken);
    }
}
