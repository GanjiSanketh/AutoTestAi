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
/// Slice 3C-4A: mobile execution start through the public endpoint — target
/// validation, reference population, and web-path preservation. No worker,
/// session, or Appium runtime involved.
/// </summary>
public sealed class MobileExecutionStartApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid AdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    private Guid _appiumVersionId = Guid.Empty;
    private Guid _webVersionId = Guid.Empty;
    private Guid _poolId = Guid.Empty;
    private Guid _appId = Guid.Empty;

    public MobileExecutionStartApiTests(Slice1ApiFactory factory) => _factory = factory;

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
            var pa = _projectA;
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = AdminRoleId, Name = "admin" },
                        new Role { Id = TesterRoleId, Name = "tester" });
                }
                var admin = new User { ExternalIdentityId = "ex-mobexec-admin", Email = "admin@x", DisplayName = "Admin" };
                var tester = new User { ExternalIdentityId = "ex-mobexec-tester", Email = "tester@x", DisplayName = "Tester" };
                db.Users.AddRange(admin, tester);
                db.Projects.Add(new Project { Id = pa, Name = "Mobile Exec", Key = "MEX" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = admin.Id, RoleId = AdminRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId });

                var webCase = new TestCase
                {
                    ProjectId = pa, TestKey = "WEB-001", Title = "Web",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var appiumCase = new TestCase
                {
                    ProjectId = pa, TestKey = "MOB-001", Title = "Mobile",
                    Framework = "appium", Platform = "android",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.AddRange(webCase, appiumCase);
                var webVersion = new TestCaseVersion
                {
                    TestCaseId = webCase.Id, VersionNumber = 1,
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                };
                var appiumVersion = new TestCaseVersion
                {
                    TestCaseId = appiumCase.Id, VersionNumber = 1,
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"launchApp"},{"order":2,"action":"tap","target":"accessibilityId=login"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                };
                db.TestCaseVersions.AddRange(webVersion, appiumVersion);

                var pool = new MobileDevicePool
                {
                    ProjectId = pa, Name = "android-smoke",
                    Platform = MobilePlatform.Android, Status = MobilePoolStatus.Active,
                };
                db.MobileDevicePools.Add(pool);
                var app = new MobileApp
                {
                    ProjectId = pa, Platform = MobilePlatform.Android, Name = "Shop",
                    PackageId = "com.example.shop", InstallPolicy = MobileInstallPolicy.Preinstalled,
                };
                db.MobileApps.Add(app);

                _webVersionId = webVersion.Id;
                _appiumVersionId = appiumVersion.Id;
                _poolId = pool.Id;
                _appId = app.Id;
                return Task.CompletedTask;
            });
            _seeded = true;
        }
        finally { _seedLock.Release(); }
    }

    [Fact]
    public async Task MobileStart_PersistsTargetRefs_AndLeavesSessionNull()
    {
        await SeedOnceAsync();
        var tester = ClientFor(_factory, "ex-mobexec-tester", ["tester"]);

        var response = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/executions", new
        {
            testCaseVersionId = _appiumVersionId,
            mobileDevicePoolId = _poolId,
            mobileAppId = _appId,
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var executionId = document.RootElement.GetProperty("executionId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
        var execution = await db.Executions.FirstAsync(e => e.Id == executionId);
        Assert.Equal(_poolId, execution.MobileDevicePoolId);
        Assert.Equal(_appId, execution.MobileAppId);
        Assert.Null(execution.MobileDeviceSessionId);
        var test = await db.ExecutionTests.FirstAsync(t => t.ExecutionId == executionId);
        Assert.Equal("appium", test.Framework);
        Assert.Null(test.Browser);
    }

    [Fact]
    public async Task MobileStart_MissingTarget_ReturnsBadRequest()
    {
        await SeedOnceAsync();
        var tester = ClientFor(_factory, "ex-mobexec-tester", ["tester"]);

        var noPool = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/executions", new
        {
            testCaseVersionId = _appiumVersionId,
            mobileAppId = _appId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, noPool.StatusCode);

        var noApp = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/executions", new
        {
            testCaseVersionId = _appiumVersionId,
            mobileDevicePoolId = _poolId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, noApp.StatusCode);
    }

    [Fact]
    public async Task WebStart_WithMobileRefs_ReturnsBadRequest()
    {
        await SeedOnceAsync();
        var tester = ClientFor(_factory, "ex-mobexec-tester", ["tester"]);

        var response = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/executions", new
        {
            testCaseVersionId = _webVersionId,
            browser = "chromium",
            mobileDevicePoolId = _poolId,
            mobileAppId = _appId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
