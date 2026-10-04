using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-4D-1: visual baseline lifecycle — validation, idempotency,
/// project isolation, approval/supersede transitions, rejection rules,
/// authorization matrix, audit hygiene, storage paths. No comparison.</summary>
public sealed class VisualBaselineTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private sealed class AllowAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DenyAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => false;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(false);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct)
            => throw new ForbiddenException("Denied.");
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeUser : ICurrentUserService
    {
        public bool IsAuthenticated => true;
        public string? ExternalIdentityId => "ex-visual-admin";
        public string? Email => "admin@x";
        public string? DisplayName => "Admin";
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
    }

    private sealed class FakeUsers : IUserDirectory
    {
        public Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken cancellationToken)
            => Task.FromResult<Guid?>(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        public Task<Guid> EnsureProvisionedAsync(string externalIdentityId, string? email, string? displayName, CancellationToken cancellationToken)
            => Task.FromResult(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    }

    private sealed class FakeStore : IVisualBaselineStore
    {
        public readonly Dictionary<Guid, VisualBaseline> Rows = new();
        public Task<VisualBaseline?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var b) ? b : null);
        public Task<VisualBaseline?> FindActiveAsync(Guid testCaseVersionId, int stepOrder, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(b =>
                b.TestCaseVersionId == testCaseVersionId && b.StepOrder == stepOrder &&
                b.Status == VisualBaselineStatus.Active));
        public Task<VisualBaseline?> FindCandidateAsync(Guid testCaseVersionId, int stepOrder, string sha256, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(b =>
                b.TestCaseVersionId == testCaseVersionId && b.StepOrder == stepOrder &&
                b.Status == VisualBaselineStatus.Candidate && b.Sha256 == sha256));
        public Task<IReadOnlyList<VisualBaseline>> ListAsync(Guid projectId, Guid? testCaseVersionId, VisualBaselineStatus? status, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VisualBaseline>>(Rows.Values
                .Where(b => b.ProjectId == projectId &&
                    (testCaseVersionId == null || b.TestCaseVersionId == testCaseVersionId) &&
                    (status == null || b.Status == status))
                .ToList());
        public Task AddAsync(VisualBaseline baseline, CancellationToken ct) { Rows[baseline.Id] = baseline; return Task.CompletedTask; }
        public Task DeleteAsync(VisualBaseline baseline, CancellationToken ct) { Rows.Remove(baseline.Id); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCases : ITestCaseStore
    {
        public readonly Dictionary<Guid, TestCase> Cases = new();
        public readonly Dictionary<Guid, TestCaseVersion> Versions = new();
        public Task<TestCase?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Cases.TryGetValue(id, out var c) ? c : null);
        public Task<TestCaseVersion?> GetVersionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Versions.TryGetValue(id, out var v) ? v : null);
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

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured { get; set; } = true;
        public readonly List<string> UploadedKeys = new();
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct)
        { UploadedKeys.Add(key); return Task.CompletedTask; }
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}?exp={expirySeconds}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class Fixture
    {
        public FakeStore Store = new();
        public FakeCases Cases = new();
        public FakeArtifacts Artifacts = new();
        public FakeAudit Audit = new();
        public Guid VersionA;
        public Guid VersionB;

        public Fixture(IAuthorizationService? auth = null)
        {
            var testCase = new TestCase
            {
                ProjectId = ProjectA, TestKey = "VIS-001", Title = "Visual",
                Framework = "appium", Platform = "android",
                Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
            };
            Cases.Cases.Add(testCase.Id, testCase);
            var version = new TestCaseVersion
            {
                TestCaseId = testCase.Id, VersionNumber = 1,
                StructuredSteps = System.Text.Json.JsonDocument.Parse("""[{"order":1,"action":"verifyScreenshot"}]"""),
                ReviewStatus = ReviewStatus.Approved,
            };
            Cases.Versions.Add(version.Id, version);
            VersionA = version.Id;

            var other = new TestCase
            {
                ProjectId = ProjectB, TestKey = "VIS-001", Title = "Other",
                Framework = "appium", Platform = "android",
                Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
            };
            Cases.Cases.Add(other.Id, other);
            var otherVersion = new TestCaseVersion
            {
                TestCaseId = other.Id, VersionNumber = 1,
                StructuredSteps = System.Text.Json.JsonDocument.Parse("""[{"order":1,"action":"verifyScreenshot"}]"""),
                ReviewStatus = ReviewStatus.Approved,
            };
            Cases.Versions.Add(otherVersion.Id, otherVersion);
            VersionB = otherVersion.Id;

            Service = new VisualBaselineService(
                Store, Cases, Artifacts, auth ?? new AllowAuth(),
                new FakeUser(), new FakeUsers(), new FixedClock(), Audit);
        }

        public VisualBaselineService Service { get; }

        public Task<VisualBaselineDto> ProposeAsync(Guid? version = null, int step = 1, byte[]? png = null)
            => Service.ProposeAsync(new ProposeBaselineCommand(
                ProjectA, version ?? VersionA, step, png ?? PngBytes, 100, 200),
                CancellationToken.None);
    }

    [Fact]
    public async Task Propose_CreatesCandidate_WithServerGeneratedPath()
    {
        var f = new Fixture();
        var dto = await f.ProposeAsync();

        Assert.Equal("Candidate", dto.Status);
        Assert.Equal(ProjectA, dto.ProjectId);
        Assert.Equal(1, dto.StepOrder);
        Assert.Equal("image/png", dto.ContentType);
        Assert.Equal(64, dto.Sha256.Length);
        Assert.StartsWith($"projects/{ProjectA}/visual-baselines/", dto.StorageKey);
        Assert.EndsWith(".png", dto.StorageKey);
        Assert.Single(f.Artifacts.UploadedKeys);
        Assert.Contains(f.Audit.Actions, a => a == "visual.baseline_proposed");
    }

    [Fact]
    public async Task Propose_DuplicateBytes_ConvergeIdempotently()
    {
        var f = new Fixture();
        var first = await f.ProposeAsync();
        var second = await f.ProposeAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Single(f.Store.Rows);
        Assert.Single(f.Artifacts.UploadedKeys);
    }

    [Fact]
    public async Task Propose_RejectsInvalidInput()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ValidationException>(() => f.ProposeAsync(step: 0));
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.ProposeAsync(new ProposeBaselineCommand(
                ProjectA, f.VersionA, 1, Array.Empty<byte>(), 100, 200), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.ProposeAsync(new ProposeBaselineCommand(
                ProjectA, f.VersionA, 1, new byte[] { 1, 2, 3, 4 }, 100, 200), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.ProposeAsync(new ProposeBaselineCommand(
                ProjectA, f.VersionA, 1, PngBytes, 0, 200), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            f.Service.ProposeAsync(new ProposeBaselineCommand(
                ProjectA, Guid.NewGuid(), 1, PngBytes, 100, 200), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            f.Service.ProposeAsync(new ProposeBaselineCommand(
                ProjectA, f.VersionB, 1, PngBytes, 100, 200), CancellationToken.None));
    }

    [Fact]
    public async Task Propose_WithoutStorage_FailsClosed()
    {
        var f = new Fixture();
        f.Artifacts.IsConfigured = false;
        await Assert.ThrowsAsync<ConflictException>(() => f.ProposeAsync());
        Assert.Empty(f.Store.Rows);
    }

    [Fact]
    public async Task Approve_Activates_And_SupersedesPrevious()
    {
        var f = new Fixture();
        var first = await f.ProposeAsync();
        var approved = await f.Service.ApproveAsync(
            new ApproveBaselineCommand(ProjectA, first.Id), CancellationToken.None);
        Assert.Equal("Active", approved.Status);
        Assert.NotNull(approved.ApprovedAt);
        Assert.Contains(f.Audit.Actions, a => a == "visual.baseline_approved");

        var second = await f.Service.ProposeAsync(new ProposeBaselineCommand(
            ProjectA, f.VersionA, 2, PngBytes, 100, 200), CancellationToken.None);
        var active = await f.Service.ApproveAsync(
            new ApproveBaselineCommand(ProjectA, second.Id), CancellationToken.None);
        Assert.Equal("Active", active.Status);
        // Different step: the first baseline stays Active.
        Assert.Equal("Active", (await f.Service.GetAsync(ProjectA, first.Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Approve_SameStep_SupersedesPreviousActive()
    {
        var f = new Fixture();
        var first = await f.ProposeAsync();
        await f.Service.ApproveAsync(new ApproveBaselineCommand(ProjectA, first.Id), CancellationToken.None);
        // Same version and step, different bytes: a distinct candidate.
        var altered = PngBytes.Concat(new byte[] { 9 }).ToArray();
        var second = await f.Service.ProposeAsync(new ProposeBaselineCommand(
            ProjectA, f.VersionA, 1, altered, 100, 200), CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        var active = await f.Service.ApproveAsync(
            new ApproveBaselineCommand(ProjectA, second.Id), CancellationToken.None);

        Assert.Equal("Active", active.Status);
        Assert.Equal("Superseded",
            (await f.Service.GetAsync(ProjectA, first.Id, CancellationToken.None)).Status);
        Assert.Single(await f.Service.ListAsync(ProjectA, null, "Active", CancellationToken.None));
        Assert.Single(await f.Service.ListAsync(ProjectA, null, "Superseded", CancellationToken.None));
    }

    [Fact]
    public async Task Approve_NonCandidate_Fails()
    {
        var f = new Fixture();
        var candidate = await f.ProposeAsync();
        await f.Service.ApproveAsync(new ApproveBaselineCommand(ProjectA, candidate.Id), CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.ApproveAsync(
            new ApproveBaselineCommand(ProjectA, candidate.Id), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.ApproveAsync(
            new ApproveBaselineCommand(ProjectA, Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task Reject_RemovesCandidate_ButNeverActive()
    {
        var f = new Fixture();
        var candidate = await f.ProposeAsync();
        await f.Service.RejectAsync(ProjectA, candidate.Id, CancellationToken.None);
        Assert.Empty(f.Store.Rows);
        Assert.Contains(f.Audit.Actions, a => a == "visual.baseline_rejected");

        var second = await f.ProposeAsync();
        await f.Service.ApproveAsync(new ApproveBaselineCommand(ProjectA, second.Id), CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.RejectAsync(ProjectA, second.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.RejectAsync(ProjectA, Guid.NewGuid(), CancellationToken.None));
        Assert.Single(f.Store.Rows);
    }

    [Fact]
    public async Task ProjectIsolation_Enforced()
    {
        var f = new Fixture();
        var dto = await f.ProposeAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.GetAsync(ProjectB, dto.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.ApproveAsync(
            new ApproveBaselineCommand(ProjectB, dto.Id), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.RejectAsync(ProjectB, dto.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.GetDownloadUrlAsync(ProjectB, dto.Id, CancellationToken.None));
        Assert.Empty(await f.Service.ListAsync(ProjectB, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizationMatrix_Enforced()
    {
        var f = new Fixture(auth: new DenyAuth());
        await Assert.ThrowsAsync<ForbiddenException>(() => f.ProposeAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Service.ListAsync(ProjectA, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Service.ApproveAsync(new ApproveBaselineCommand(ProjectA, Guid.NewGuid()), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            f.Service.RejectAsync(ProjectA, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task List_Filters_And_ValidatesStatus()
    {
        var f = new Fixture();
        var candidate = await f.ProposeAsync();
        await f.Service.ApproveAsync(new ApproveBaselineCommand(ProjectA, candidate.Id), CancellationToken.None);

        Assert.Single(await f.Service.ListAsync(ProjectA, null, "Active", CancellationToken.None));
        Assert.Empty(await f.Service.ListAsync(ProjectA, null, "Candidate", CancellationToken.None));
        Assert.Single(await f.Service.ListAsync(ProjectA, f.VersionA, null, CancellationToken.None));
        // A version filter from another project 404s, mirroring the device
        // pool filter convention (no cross-project filter reads).
        await Assert.ThrowsAsync<NotFoundException>(() =>
            f.Service.ListAsync(ProjectA, f.VersionB, null, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            f.Service.ListAsync(ProjectA, null, "Bogus", CancellationToken.None));
    }

    [Fact]
    public async Task DownloadUrl_IsPresigned_And_ProjectScoped()
    {
        var f = new Fixture();
        var dto = await f.ProposeAsync();
        var url = await f.Service.GetDownloadUrlAsync(ProjectA, dto.Id, CancellationToken.None);
        Assert.StartsWith("https://artifacts.example/", url);
        Assert.Contains("exp=900", url);
        Assert.DoesNotContain("ClaimToken", url);
    }
}
