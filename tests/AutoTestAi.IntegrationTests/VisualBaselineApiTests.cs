using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 3C-4D-1: visual baseline lifecycle API — auth matrix, project
/// isolation, validation, idempotent propose, atomic approve/supersede,
/// rejection rules, download path. No comparison logic exists in this slice.
/// </summary>
public sealed class VisualBaselineApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid AdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly Guid _versionA = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    public VisualBaselineApiTests(Slice1ApiFactory factory) => _factory = factory;

    private static HttpClient ClientFor(Slice1ApiFactory factory, string sub, string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private sealed class FakeArtifactStorage : AutoTestAi.Application.Storage.IArtifactStorage
    {
        public bool IsConfigured => true;
        public readonly List<string> UploadedKeys = new();
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct)
        { UploadedKeys.Add(key); return Task.CompletedTask; }
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}?exp={expirySeconds}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private static HttpClient ClientWithStorage(Slice1ApiFactory factory, string sub, string[] roles)
    {
        var custom = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<AutoTestAi.Application.Storage.IArtifactStorage>(new FakeArtifactStorage());
            }));
        var client = custom.CreateClient();
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
            var (pa, pb, va) = (_projectA, _projectB, _versionA);
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = AdminRoleId, Name = "admin" },
                        new Role { Id = TesterRoleId, Name = "tester" });
                }
                var admin = new User { ExternalIdentityId = "ex-vis-admin", Email = "admin@x", DisplayName = "Admin" };
                var tester = new User { ExternalIdentityId = "ex-vis-tester", Email = "tester@x", DisplayName = "Tester" };
                db.Users.AddRange(admin, tester);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Visual Alpha", Key = "VSA" },
                    new Project { Id = pb, Name = "Visual Beta", Key = "VSB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = admin.Id, RoleId = AdminRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId });
                var testCase = new TestCase
                {
                    ProjectId = pa, TestKey = "VIS-001", Title = "Visual",
                    Framework = "appium", Platform = "android",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(testCase);
                db.TestCaseVersions.Add(new TestCaseVersion
                {
                    Id = va,
                    TestCaseId = testCase.Id,
                    VersionNumber = 1,
                    StructuredSteps = JsonDocument.Parse("""[{"order":1,"action":"verifyScreenshot"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                });
                return Task.CompletedTask;
            });
            _seeded = true;
        }
        finally { _seedLock.Release(); }
    }

    private async Task<Guid> ProposeAsync(HttpClient admin, Guid? version = null, int step = 1, string? image = null)
    {
        var response = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/visual-baselines", new
        {
            testCaseVersionId = version ?? _versionA,
            stepOrder = step,
            imageBase64 = image ?? PngBase64,
            width = 100,
            height = 200,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Candidate", document.RootElement.GetProperty("status").GetString());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Lifecycle_ProposeApproveReject_RoundTrips()
    {
        await SeedOnceAsync();
        var admin = ClientWithStorage(_factory, "ex-vis-admin", ["admin"]);
        var tester = ClientWithStorage(_factory, "ex-vis-tester", ["tester"]);

        var candidateId = await ProposeAsync(admin);

        // Duplicate bytes converge idempotently onto the same candidate.
        var duplicateId = await ProposeAsync(admin);
        Assert.Equal(candidateId, duplicateId);

        // Readers can list and fetch; writers cannot via the tester role.
        var list = await tester.GetAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines?testCaseVersionId={_versionA}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var forbiddenPropose = await tester.PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines",
            new { testCaseVersionId = _versionA, stepOrder = 1, imageBase64 = PngBase64, width = 100, height = 200 });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPropose.StatusCode);

        // Approve transitions Candidate → Active (audited); second approval conflicts.
        var approve = await admin.PostAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines/{candidateId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var approved = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
        Assert.Equal("Active", approved.RootElement.GetProperty("status").GetString());

        var reapprove = await admin.PostAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines/{candidateId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, reapprove.StatusCode);

        // Active baselines can never be deleted.
        var deleteActive = await admin.DeleteAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines/{candidateId}");
        Assert.Equal(HttpStatusCode.Conflict, deleteActive.StatusCode);

        // A fresh candidate on another step can still be rejected.
        var other = await ProposeAsync(admin, step: 2);
        var reject = await admin.DeleteAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines/{other}");
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);

        // Download issues a short-lived presigned URL for review.
        var download = await tester.GetAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines/{candidateId}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using var downloadDoc = JsonDocument.Parse(await download.Content.ReadAsStringAsync());
        Assert.True(downloadDoc.RootElement.TryGetProperty("downloadUrl", out _));
    }

    [Fact]
    public async Task Validation_RejectsBadInput()
    {
        await SeedOnceAsync();
        var admin = ClientWithStorage(_factory, "ex-vis-admin", ["admin"]);

        var notPng = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/visual-baselines", new
        {
            testCaseVersionId = _versionA,
            stepOrder = 1,
            imageBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            width = 100,
            height = 200,
        });
        Assert.Equal(HttpStatusCode.BadRequest, notPng.StatusCode);

        var unknownVersion = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectA}/visual-baselines", new
        {
            testCaseVersionId = Guid.NewGuid(),
            stepOrder = 1,
            imageBase64 = PngBase64,
            width = 100,
            height = 200,
        });
        Assert.Equal(HttpStatusCode.NotFound, unknownVersion.StatusCode);

        var badStatus = await admin.GetAsync(
            $"/api/v1/projects/{_projectA}/visual-baselines?status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    [Fact]
    public async Task ProjectIsolation_Enforced()
    {
        await SeedOnceAsync();
        // Platform admins bypass membership, so isolation is enforced by
        // project-scoped rows: cross-project reads 404/empty, writes 404.
        var admin = ClientWithStorage(_factory, "ex-vis-admin", ["admin"]);

        var baselineId = await ProposeAsync(admin);

        var crossGet = await admin.GetAsync(
            $"/api/v1/projects/{_projectB}/visual-baselines/{baselineId}");
        Assert.Equal(HttpStatusCode.NotFound, crossGet.StatusCode);

        var crossList = await admin.GetAsync($"/api/v1/projects/{_projectB}/visual-baselines");
        Assert.Equal(HttpStatusCode.OK, crossList.StatusCode);
        var body = await crossList.Content.ReadAsStringAsync();
        Assert.DoesNotContain(baselineId.ToString(), body, StringComparison.Ordinal);

        var crossApprove = await admin.PostAsync(
            $"/api/v1/projects/{_projectB}/visual-baselines/{baselineId}/approve", null);
        Assert.Equal(HttpStatusCode.NotFound, crossApprove.StatusCode);

        var crossReject = await admin.DeleteAsync(
            $"/api/v1/projects/{_projectB}/visual-baselines/{baselineId}");
        Assert.Equal(HttpStatusCode.NotFound, crossReject.StatusCode);

        var crossDownload = await admin.GetAsync(
            $"/api/v1/projects/{_projectB}/visual-baselines/{baselineId}/download");
        Assert.Equal(HttpStatusCode.NotFound, crossDownload.StatusCode);

        // A version from another project cannot seed a baseline here.
        var crossPropose = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectB}/visual-baselines", new
        {
            testCaseVersionId = _versionA,
            stepOrder = 1,
            imageBase64 = PngBase64,
            width = 100,
            height = 200,
        });
        Assert.Equal(HttpStatusCode.NotFound, crossPropose.StatusCode);

        // A non-member gets Forbidden (not NotFound) on project paths.
        var outsider = ClientFor(_factory, "ex-vis-outsider", ["tester"]);
        var denied = await outsider.GetAsync($"/api/v1/projects/{_projectA}/visual-baselines");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Anonymous_CannotAccess()
    {
        await SeedOnceAsync();
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/v1/projects/{_projectA}/visual-baselines");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
