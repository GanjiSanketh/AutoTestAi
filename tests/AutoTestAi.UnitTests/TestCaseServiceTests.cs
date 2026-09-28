using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>In-memory ITestCaseStore fake: no EF, no database.</summary>
internal sealed class FakeTestCaseStore : ITestCaseStore
{
    public readonly List<TestCase> Cases = new();
    public readonly List<TestCaseVersion> Versions = new();

    public Task<int> CountAsync(Guid projectId, string? search, TestCaseStatusFilter filter, CancellationToken ct)
        => Task.FromResult(Apply(projectId, search, filter).Count);

    public Task<IReadOnlyList<TestCase>> ListAsync(
        Guid projectId, string? search, TestCaseStatusFilter filter, int skip, int take, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestCase>>(Apply(projectId, search, filter)
            .OrderByDescending(t => t.UpdatedAt).Skip(skip).Take(take).ToList());

    private List<TestCase> Apply(Guid projectId, string? search, TestCaseStatusFilter filter)
    {
        IEnumerable<TestCase> query = Cases.Where(t => t.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(t =>
                t.TestKey.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                t.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (t.Module != null && t.Module.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        if (filter.Status is not null) query = query.Where(t => t.Status.ToString() == filter.Status);
        if (filter.Priority is not null) query = query.Where(t => t.Priority.ToString() == filter.Priority);
        if (filter.Framework is not null)
            query = query.Where(t => t.Framework != null && t.Framework.Contains(filter.Framework, StringComparison.OrdinalIgnoreCase));
        if (filter.Platform is not null)
            query = query.Where(t => t.Platform != null && t.Platform.Contains(filter.Platform, StringComparison.OrdinalIgnoreCase));
        if (filter.ReviewStatus is not null)
            query = query.Where(t => Latest(t.Id)?.ReviewStatus.ToString() == filter.ReviewStatus);
        return query.ToList();
    }

    private TestCaseVersion? Latest(Guid testCaseId)
        => Versions.Where(v => v.TestCaseId == testCaseId).MaxBy(v => v.VersionNumber);

    public Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(
        IReadOnlyList<Guid> testCaseIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<Guid, TestCaseVersion>>(testCaseIds
            .Select(Latest).Where(v => v is not null)
            .ToDictionary(v => v!.TestCaseId, v => v!));

    public Task<TestCase?> GetByIdAsync(Guid testCaseId, CancellationToken ct)
        => Task.FromResult(Cases.FirstOrDefault(t => t.Id == testCaseId));

    public Task<TestCase?> GetByKeyAsync(Guid projectId, string normalizedKey, CancellationToken ct)
        => Task.FromResult(Cases.FirstOrDefault(t => t.ProjectId == projectId && t.TestKey == normalizedKey));

    public Task AddTestCaseAsync(TestCase testCase, CancellationToken ct)
    {
        Cases.Add(testCase);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<TestCaseVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken ct)
        => Task.FromResult(Versions.FirstOrDefault(v => v.Id == versionId));

    public Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid testCaseId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestCaseVersion>>(Versions
            .Where(v => v.TestCaseId == testCaseId).OrderByDescending(v => v.VersionNumber).ToList());

    public Task<TestCaseVersion> AddNextVersionAsync(Guid testCaseId, Func<int, TestCaseVersion> factory, CancellationToken ct)
    {
        var max = Versions.Where(v => v.TestCaseId == testCaseId)
            .Select(v => (int?)v.VersionNumber).Max() ?? 0;
        var version = factory(max + 1);
        Versions.Add(version);
        return Task.FromResult(version);
    }

    public Task AddVersionAsync(TestCaseVersion version, CancellationToken ct)
    {
        Versions.Add(version);
        return Task.CompletedTask;
    }
}

/// <summary>Audit backing store for service tests (records events, never fails).</summary>
internal sealed class StubAuditProjectStore : IProjectStore
{
    public readonly List<AuditEvent> Audits = new();
    public Task RecordAuditAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        Audits.Add(auditEvent);
        return Task.CompletedTask;
    }
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<int> CountAccessibleAsync(string? e, bool a, string? s, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(string? e, bool a, string? s, int sk, int t, CancellationToken ct) => throw new NotImplementedException();
    public Task<Project?> GetByIdAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
    public Task<Project?> GetByKeyAsync(string k, CancellationToken ct) => throw new NotImplementedException();
    public Task AddProjectAsync(Project p, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> GetUserByIdAsync(Guid u, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> GetUserByEmailAsync(string e, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task<Role?> GetRoleByIdAsync(Guid r, CancellationToken ct) => throw new NotImplementedException();
    public Task<Role> GetOrCreateRoleAsync(string n, string? d, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> IsMemberAsync(Guid p, Guid u, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<MemberRow>> ListMembersAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
    public Task<ProjectMember?> FindMemberAsync(Guid p, Guid u, CancellationToken ct) => throw new NotImplementedException();
    public Task AddMemberAsync(ProjectMember m, CancellationToken ct) => throw new NotImplementedException();
    public Task RemoveMemberAsync(ProjectMember m, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<TestEnvironment>> ListEnvironmentsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
    public Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
    public Task AddEnvironmentAsync(TestEnvironment e, CancellationToken ct) => throw new NotImplementedException();
}

public sealed class TestCaseServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private static (TestCaseService Service, FakeTestCaseStore Store, StubAuditProjectStore Audits) Create(
        ICurrentUserService user,
        Action<FakeTestCaseStore, StubMembershipStore>? seed = null,
        string userSub = "user-1",
        Guid? userAppId = null)
    {
        var store = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        seed?.Invoke(store, memberships);
        var directory = new FakeUserDirectory();
        if (userAppId.HasValue) directory.Add(userSub, userAppId.Value);
        var authorization = new AuthorizationService(user, memberships);
        var audits = new StubAuditProjectStore();
        var service = new TestCaseService(
            store, user, authorization, directory,
            new SystemDateTimeProvider(),
            new AuditService(audits, user, directory, NullLogger<AuditService>.Instance));
        return (service, store, audits);
    }

    private static StubCurrentUser Manager(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["qa-lead"],
        Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static StubCurrentUser Tester(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["tester"],
        Permissions = RolePermissions.Resolve(["tester"]),
    };

    private static StubCurrentUser Viewer(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["viewer"],
        Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private static JsonElement Steps(string json) => JsonDocument.Parse(json).RootElement;

    private static CreateTestCaseCommand ValidCreate(Guid projectId, string key = "LOGIN-001") => new(
        projectId, key, "Successful user login", "Verify login.", "Authentication",
        "playwright", "web", "High", "Draft", "manual",
        "test('login', async () => {});",
        Steps("""[{"order":1,"action":"navigate","target":"https://example.test"}]"""));

    private static void SeedCase(
        FakeTestCaseStore store, StubMembershipStore memberships,
        string sub, Guid projectId, string key,
        int versions = 1, ReviewStatus review = ReviewStatus.Pending)
    {
        var testCase = new TestCase
        {
            ProjectId = projectId, TestKey = key, Title = $"Title {key}",
            Priority = Priority.High, Status = TestCaseStatus.Draft, SourceType = "manual",
        };
        store.Cases.Add(testCase);
        for (var i = 1; i <= versions; i++)
            store.Versions.Add(new TestCaseVersion
            {
                TestCaseId = testCase.Id, VersionNumber = i,
                SourceCode = $"// v{i}", ReviewStatus = i == versions ? review : ReviewStatus.Approved,
            });
        memberships.Add(sub, projectId);
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_Succeeds_WithVersion1_AndAudits()
    {
        var appId = Guid.NewGuid();
        var (service, store, audits) = Create(
            Manager(),
            (s, m) => m.Add("user-1", ProjectA),
            userAppId: appId);

        var created = await service.CreateAsync(ValidCreate(ProjectA), CancellationToken.None);

        Assert.Equal("LOGIN-001", created.TestKey);
        Assert.Equal(appId, created.CreatedBy);
        Assert.Equal(1, created.LatestVersionNumber);
        Assert.Equal("Pending", created.LatestReviewStatus);
        Assert.Contains(audits.Audits, a => a.Action == "testcase.created" && a.ProjectId == ProjectA);
    }

    [Fact]
    public async Task Create_DuplicateKeySameProject_Conflict_DifferentProject_Allowed()
    {
        var (service, _, _) = Create(Manager(), (store, memberships) =>
        {
            memberships.Add("user-1", ProjectA);
            memberships.Add("user-1", ProjectB);
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001");
        });

        await Assert.ThrowsAsync<ConflictException>(
            () => service.CreateAsync(ValidCreate(ProjectA), CancellationToken.None));

        var other = await service.CreateAsync(ValidCreate(ProjectB), CancellationToken.None);
        Assert.Equal("LOGIN-001", other.TestKey);
        Assert.Equal(ProjectB, other.ProjectId);
    }

    [Fact]
    public async Task Create_ValidationErrors_IncludeFieldDetails()
    {
        var (service, _, _) = Create(Manager(), (store, memberships) =>
            memberships.Add("user-1", ProjectA));

        var ex = await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new CreateTestCaseCommand(ProjectA, "bad key!", "", null, null, null, null,
                "Bogus", "Bogus", "generated", null, null),
            CancellationToken.None));

        Assert.Contains(ex.Errors, e => e.Field == "title");
        Assert.Contains(ex.Errors, e => e.Field == "testKey");
        Assert.Contains(ex.Errors, e => e.Field == "priority");
        Assert.Contains(ex.Errors, e => e.Field == "status");
        Assert.Contains(ex.Errors, e => e.Field == "sourceType");
    }

    [Fact]
    public async Task Create_InvalidSteps_Rejected()
    {
        var (service, _, _) = Create(Manager(), (store, memberships) =>
            memberships.Add("user-1", ProjectA));

        var command = ValidCreate(ProjectA, "STEPS-001") with
        {
            StructuredSteps = Steps("""[{"target":"#x"}]"""),
        };
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateAsync(command, CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "structuredSteps");
    }

    [Fact]
    public async Task Create_WithoutManagePermission_Forbidden_Anonymous_Unauthorized()
    {
        var (service, _, _) = Create(Viewer(), (store, memberships) =>
            memberships.Add("user-1", ProjectA));
        await Assert.ThrowsAsync<ForbiddenException>(
            () => service.CreateAsync(ValidCreate(ProjectA), CancellationToken.None));

        var (anon, _, _) = Create(new StubCurrentUser { IsAuthenticated = false });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => anon.CreateAsync(ValidCreate(ProjectA), CancellationToken.None));
    }

    // ---------- update / versioning ----------

    [Fact]
    public async Task Update_MetadataOnly_DoesNotCreateVersion()
    {
        var (service, store, _) = Create(Manager(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001"));

        var updated = await service.UpdateAsync(
            store.Cases[0].Id,
            new UpdateTestCaseCommand("Renamed title", null, "Auth", null, null, null, null, null,
                null, null, HasSourceCode: false, HasStructuredSteps: false),
            CancellationToken.None);

        Assert.Equal("Renamed title", updated.Title);
        Assert.Equal(1, updated.LatestVersionNumber);
        Assert.Single(store.Versions);
    }

    [Fact]
    public async Task Update_ContentChange_CreatesSequentialVersion_AndPreservesOld()
    {
        var (service, store, audits) = Create(Manager(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001"));

        var id = store.Cases[0].Id;
        var v2 = await service.UpdateAsync(id,
            new UpdateTestCaseCommand("Successful user login", null, null, null, null, null, null, null,
                "test('login', async () => { /* v2 */ });", null, HasSourceCode: true, HasStructuredSteps: false),
            CancellationToken.None);

        Assert.Equal(2, v2.LatestVersionNumber);
        var v1 = store.Versions.Single(v => v.VersionNumber == 1);
        Assert.Equal("// v1", v1.SourceCode); // immutable
        Assert.Contains(audits.Audits, a => a.Action == "testcase.version_created");
    }

    [Fact]
    public async Task Update_KeyIsImmutable_IgnoredByContract()
    {
        var (service, store, _) = Create(Manager(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001"));

        // UpdateTestCaseCommand carries no key: identity can never change.
        var updated = await service.UpdateAsync(store.Cases[0].Id,
            new UpdateTestCaseCommand("T", null, null, null, null, null, null, null,
                null, null, HasSourceCode: false, HasStructuredSteps: false),
            CancellationToken.None);
        Assert.Equal("LOGIN-001", updated.TestKey);
    }

    [Fact]
    public async Task Update_WithoutManagePermission_Forbidden()
    {
        var (service, store, _) = Create(Viewer(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001"));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpdateAsync(store.Cases[0].Id,
            new UpdateTestCaseCommand("T", null, null, null, null, null, null, null,
                null, null, HasSourceCode: false, HasStructuredSteps: false),
            CancellationToken.None));
    }

    // ---------- archive ----------

    [Fact]
    public async Task Archive_SoftDeletes_PreservingVersions()
    {
        var (service, store, audits) = Create(Manager(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001", versions: 2));

        await service.ArchiveAsync(store.Cases[0].Id, CancellationToken.None);
        await service.ArchiveAsync(store.Cases[0].Id, CancellationToken.None); // idempotent

        Assert.Equal(TestCaseStatus.Archived, store.Cases[0].Status);
        Assert.Equal(2, store.Versions.Count); // history preserved
        Assert.Contains(audits.Audits, a => a.Action == "testcase.archived");
    }

    // ---------- versions ----------

    [Fact]
    public async Task ListVersions_ReturnsNewestFirst()
    {
        var (service, store, _) = Create(Tester(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001", versions: 3));

        var versions = await service.ListVersionsAsync(store.Cases[0].Id, CancellationToken.None);
        Assert.Equal([3, 2, 1], versions.Select(v => v.VersionNumber).ToList());
    }

    [Fact]
    public async Task GetVersion_WrongTestCase_NotFound()
    {
        var (service, store, _) = Create(Tester(), (store, memberships) =>
        {
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001");
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-002");
        });

        var foreign = store.Versions.First(v => v.TestCaseId == store.Cases[1].Id);
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetVersionAsync(store.Cases[0].Id, foreign.Id, CancellationToken.None));
    }

    // ---------- review ----------

    [Fact]
    public async Task Review_ValidTransition_Updates_AndAudits()
    {
        var (service, store, audits) = Create(Manager(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001"));

        var version = store.Versions[0];
        var reviewed = await service.ReviewAsync(store.Cases[0].Id,
            new ReviewTestCaseCommand(version.Id, "Approved"), CancellationToken.None);

        Assert.Equal("Approved", reviewed.ReviewStatus);
        Assert.Equal(ReviewStatus.Approved, version.ReviewStatus);
        Assert.Contains(audits.Audits, a => a.Action == "testcase.review_changed");
    }

    [Fact]
    public async Task Review_InvalidTransition_Rejected()
    {
        var (service, store, _) = Create(Manager(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001", review: ReviewStatus.Approved));

        await Assert.ThrowsAsync<ValidationException>(() => service.ReviewAsync(store.Cases[0].Id,
            new ReviewTestCaseCommand(store.Versions[0].Id, "Pending"), CancellationToken.None));
        Assert.Equal(ReviewStatus.Approved, store.Versions[0].ReviewStatus); // unchanged
    }

    [Fact]
    public async Task Review_ForeignVersion_NotFound_And_WithoutManage_Forbidden()
    {
        var (service, store, _) = Create(Manager(), (store, memberships) =>
        {
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001");
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-002");
        });
        var foreign = store.Versions.First(v => v.TestCaseId == store.Cases[1].Id);
        await Assert.ThrowsAsync<NotFoundException>(() => service.ReviewAsync(store.Cases[0].Id,
            new ReviewTestCaseCommand(foreign.Id, "Approved"), CancellationToken.None));

        var (viewerService, viewerStore, _) = Create(Viewer(), (store, memberships) =>
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001"));
        await Assert.ThrowsAsync<ForbiddenException>(() => viewerService.ReviewAsync(viewerStore.Cases[0].Id,
            new ReviewTestCaseCommand(viewerStore.Versions[0].Id, "Approved"), CancellationToken.None));
    }

    // ---------- list / scoping ----------

    [Fact]
    public async Task List_AppliesFilters_AndExcludesSourceCode()
    {
        var (service, store, _) = Create(Tester(), (store, memberships) =>
        {
            memberships.Add("user-1", ProjectA);
            memberships.Add("user-1", ProjectB);
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-001");
            SeedCase(store, memberships, "user-1", ProjectA, "LOGIN-002");
            SeedCase(store, memberships, "user-1", ProjectB, "LOGIN-001");
            store.Cases[1].Framework = "selenium";
            store.Cases[1].Priority = Priority.Low;
        });

        var all = await service.ListAsync(ProjectA, 1, 25,
            new TestCaseFilters(null, null, null, null, null, null), CancellationToken.None);
        Assert.Equal(2, all.TotalCount);
        Assert.All(all.Items, i => Assert.Equal(ProjectA, i.ProjectId));

        var filtered = await service.ListAsync(ProjectA, 1, 25,
            new TestCaseFilters("LOGIN-002", null, null, null, null, null), CancellationToken.None);
        Assert.Single(filtered.Items);

        var byFramework = await service.ListAsync(ProjectA, 1, 25,
            new TestCaseFilters(null, null, null, "selenium", null, null), CancellationToken.None);
        Assert.Single(byFramework.Items);

        var byPriority = await service.ListAsync(ProjectA, 1, 25,
            new TestCaseFilters(null, null, "Low", null, null, null), CancellationToken.None);
        Assert.Single(byPriority.Items);
    }

    [Fact]
    public async Task List_InvalidFilter_Rejected()
    {
        var (service, _, _) = Create(Tester(), (store, memberships) =>
            memberships.Add("user-1", ProjectA));
        var ex = await Assert.ThrowsAsync<ValidationException>(() => service.ListAsync(ProjectA, 1, 25,
            new TestCaseFilters(null, "Bogus", null, null, null, null), CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "status");
    }
}
