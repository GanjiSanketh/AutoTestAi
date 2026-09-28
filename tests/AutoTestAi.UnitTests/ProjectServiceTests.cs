using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Application.Projects;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>In-memory IProjectStore fake: no EF, no database.</summary>
internal sealed class FakeProjectStore : IProjectStore
{
    public readonly List<Project> Projects = new();
    public readonly List<User> Users = new();
    public readonly List<Role> Roles = new();
    public readonly List<ProjectMember> Members = new();
    public readonly List<TestEnvironment> Environments = new();
    public readonly List<AuditEvent> Audits = new();

    private IQueryable<Project> Accessible(string? externalIdentityId, bool isAdmin, string? search)
    {
        IEnumerable<Project> query = Projects;
        if (!isAdmin)
        {
            if (string.IsNullOrWhiteSpace(externalIdentityId)) return Enumerable.Empty<Project>().AsQueryable();
            var ids = (from m in Members
                       join u in Users on m.UserId equals u.Id
                       where u.ExternalIdentityId == externalIdentityId
                       select m.ProjectId).ToHashSet();
            query = query.Where(p => ids.Contains(p.Id));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Key.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        return query.AsQueryable();
    }

    public Task<int> CountAccessibleAsync(string? externalIdentityId, bool isAdmin, string? search, CancellationToken ct)
        => Task.FromResult(Accessible(externalIdentityId, isAdmin, search).Count());

    public Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(
        string? externalIdentityId, bool isAdmin, string? search, int skip, int take, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProjectListRow>>(Accessible(externalIdentityId, isAdmin, search)
            .OrderByDescending(p => p.UpdatedAt)
            .Skip(skip).Take(take)
            .Select(p => new ProjectListRow(p, Members.Count(m => m.ProjectId == p.Id)))
            .ToList());

    public Task<Project?> GetByIdAsync(Guid projectId, CancellationToken ct)
        => Task.FromResult(Projects.FirstOrDefault(p => p.Id == projectId));

    public Task<Project?> GetByKeyAsync(string normalizedKey, CancellationToken ct)
        => Task.FromResult(Projects.FirstOrDefault(p => p.Key == normalizedKey));

    public Task AddProjectAsync(Project project, CancellationToken ct)
    {
        Projects.Add(project);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct)
        => Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));

    public Task<User?> GetUserByEmailAsync(string email, CancellationToken ct)
        => Task.FromResult(Users.FirstOrDefault(
            u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Role>>(Roles.OrderBy(r => r.Name).ToList());

    public Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken ct)
        => Task.FromResult(Roles.FirstOrDefault(r => r.Id == roleId));

    public Task<Role> GetOrCreateRoleAsync(string name, string? description, CancellationToken ct)
    {
        var role = Roles.FirstOrDefault(r => r.Name == name);
        if (role is null)
        {
            role = new Role { Name = name, Description = description };
            Roles.Add(role);
        }
        return Task.FromResult(role);
    }

    public Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
        => Task.FromResult(Members.Any(m => m.ProjectId == projectId && m.UserId == userId));

    public Task<IReadOnlyList<MemberRow>> ListMembersAsync(Guid projectId, CancellationToken ct)
    {
        var rows = from m in Members
                   join u in Users on m.UserId equals u.Id
                   join r in Roles on m.RoleId equals r.Id
                   where m.ProjectId == projectId
                   orderby u.DisplayName
                   select new MemberRow(u.Id, u.Email, u.DisplayName, r.Id, r.Name, m.CreatedAt);
        return Task.FromResult<IReadOnlyList<MemberRow>>(rows.ToList());
    }

    public Task<ProjectMember?> FindMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
        => Task.FromResult(Members.FirstOrDefault(m => m.ProjectId == projectId && m.UserId == userId));

    public Task AddMemberAsync(ProjectMember member, CancellationToken ct)
    {
        Members.Add(member);
        return Task.CompletedTask;
    }

    public Task RemoveMemberAsync(ProjectMember member, CancellationToken ct)
    {
        Members.Remove(member);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TestEnvironment>> ListEnvironmentsAsync(Guid projectId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestEnvironment>>(
            Environments.Where(e => e.ProjectId == projectId).OrderBy(e => e.Name).ToList());

    public Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid environmentId, CancellationToken ct)
        => Task.FromResult(Environments.FirstOrDefault(e => e.Id == environmentId));

    public Task AddEnvironmentAsync(TestEnvironment environment, CancellationToken ct)
    {
        Environments.Add(environment);
        return Task.CompletedTask;
    }

    public Task RecordAuditAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        Audits.Add(auditEvent);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUserDirectory : IUserDirectory
{
    private readonly Dictionary<string, Guid> _ids = new();
    public void Add(string externalId, Guid appUserId) => _ids[externalId] = appUserId;
    public Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken ct)
        => Task.FromResult(_ids.TryGetValue(externalIdentityId, out var id) ? (Guid?)id : null);
    public Task<Guid> EnsureProvisionedAsync(string externalIdentityId, string? email, string? displayName, CancellationToken ct)
        => Task.FromResult(Guid.NewGuid());
}

public sealed class ProjectServiceTests
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (ProjectService Service, FakeProjectStore Store) Create(
        ICurrentUserService user,
        Action<FakeProjectStore>? seed = null,
        string userSub = "user-1",
        Guid? userAppId = null)
    {
        var store = new FakeProjectStore();
        seed?.Invoke(store);
        var memberships = new StubMembershipStore();
        foreach (var m in store.Members)
        {
            var u = store.Users.First(x => x.Id == m.UserId);
            memberships.Add(u.ExternalIdentityId, m.ProjectId);
        }
        var directory = new FakeUserDirectory();
        if (userAppId.HasValue) directory.Add(userSub, userAppId.Value);
        var authorization = new AuthorizationService(user, memberships);
        var service = new ProjectService(
            store, user, authorization, directory,
            new SystemDateTimeProvider(), new AuditService(store, user, directory, NullLogger<AuditService>.Instance));
        return (service, store);
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

    private static void SeedStandardRoles(FakeProjectStore store)
    {
        store.Roles.Add(new Role
        {
            Id = QaLeadRoleId, Name = "qa-lead", Description = "QA lead",
        });
        store.Roles.Add(new Role { Name = "tester", Description = "Tester" });
    }

    private static CreateProjectCommand ValidCreate(string key = "SHOP")
        => new("Shop", key, "desc", "https://git.example/shop", "https://shop.example",
            "playwright", "web", "Active");

    // ---------- create ----------

    [Fact]
    public async Task Create_Succeeds_SetsCreatorAndMembership_AndAudits()
    {
        var appId = Guid.NewGuid();
        var (service, store) = Create(Manager(), SeedStandardRoles, userAppId: appId);

        var created = await service.CreateAsync(ValidCreate(), CancellationToken.None);

        Assert.Equal("SHOP", created.Key);
        Assert.Equal(appId, created.CreatedBy);
        Assert.Equal(1, created.MemberCount);
        Assert.Single(store.Members.Where(m => m.ProjectId == created.Id && m.UserId == appId));
        Assert.Contains(store.Audits, a => a.Action == "project.created" && a.ProjectId == created.Id);
        Assert.Contains(store.Roles, r => r.Name == "qa-lead");
    }

    [Fact]
    public async Task Create_DuplicateKey_Conflict()
    {
        var (service, _) = Create(Manager(), store =>
        {
            SeedStandardRoles(store);
            store.Projects.Add(new Project { Name = "Old", Key = "SHOP" });
        });

        await Assert.ThrowsAsync<ConflictException>(
            () => service.CreateAsync(ValidCreate(), CancellationToken.None));
    }

    [Fact]
    public async Task Create_ValidationErrors_IncludeFieldDetails()
    {
        var (service, _) = Create(Manager(), SeedStandardRoles);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new CreateProjectCommand("", "1bad key!", null, "not-a-url", null, null, null, "Bogus"),
            CancellationToken.None));

        Assert.Contains(ex.Errors, e => e.Field == "name");
        Assert.Contains(ex.Errors, e => e.Field == "key");
        Assert.Contains(ex.Errors, e => e.Field == "repositoryUrl");
    }

    [Fact]
    public async Task Create_WithoutManagePermission_Forbidden()
    {
        var (service, _) = Create(Tester(), SeedStandardRoles);
        await Assert.ThrowsAsync<ForbiddenException>(
            () => service.CreateAsync(ValidCreate(), CancellationToken.None));
    }

    [Fact]
    public async Task Create_Anonymous_Unauthorized()
    {
        var (service, _) = Create(new StubCurrentUser { IsAuthenticated = false }, SeedStandardRoles);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateAsync(ValidCreate(), CancellationToken.None));
    }

    // ---------- update / archive ----------

    [Fact]
    public async Task Update_PreservesKey_AndValidatesDefaultEnvironment()
    {
        var projectId = Guid.NewGuid();
        var foreignEnvId = Guid.NewGuid();
        var (service, store) = Create(Manager("mgr"), s =>
        {
            SeedStandardRoles(s);
            var appId = Guid.NewGuid();
            s.Users.Add(new User { Id = appId, ExternalIdentityId = "mgr", Email = "m@x", DisplayName = "M" });
            s.Projects.Add(new Project { Id = projectId, Name = "Old", Key = "OLD" });
            s.Members.Add(new ProjectMember { ProjectId = projectId, UserId = appId, RoleId = QaLeadRoleId });
            s.Environments.Add(new TestEnvironment { Id = foreignEnvId, ProjectId = Guid.NewGuid(), Name = "Other" });
        }, userSub: "mgr");

        // Cross-project default environment is rejected.
        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(projectId,
            new UpdateProjectCommand("New", null, null, null, null, null, null, foreignEnvId),
            CancellationToken.None));

        var updated = await service.UpdateAsync(projectId,
            new UpdateProjectCommand("New", "d", null, null, null, null, "Archived", null),
            CancellationToken.None);

        Assert.Equal("OLD", updated.Key); // key immutable
        Assert.Equal("New", updated.Name);
        Assert.Equal("Archived", updated.Status);
        Assert.Null(updated.DefaultEnvironmentId);
    }

    [Fact]
    public async Task Archive_SoftDeletes_PreservingHistory()
    {
        var projectId = Guid.NewGuid();
        var (service, store) = Create(Manager("mgr"), s =>
        {
            SeedStandardRoles(s);
            var appId = Guid.NewGuid();
            s.Users.Add(new User { Id = appId, ExternalIdentityId = "mgr", Email = "m@x", DisplayName = "M" });
            s.Projects.Add(new Project { Id = projectId, Name = "P", Key = "P1" });
            s.Members.Add(new ProjectMember { ProjectId = projectId, UserId = appId, RoleId = QaLeadRoleId });
        }, userSub: "mgr");

        await service.ArchiveAsync(projectId, CancellationToken.None);
        await service.ArchiveAsync(projectId, CancellationToken.None); // idempotent

        var project = await store.GetByIdAsync(projectId, CancellationToken.None);
        Assert.NotNull(project);
        Assert.Equal(ProjectStatus.Archived, project!.Status); // row preserved, not deleted
        Assert.Contains(store.Audits, a => a.Action == "project.archived");
    }

    // ---------- members ----------

    [Fact]
    public async Task AddMember_Duplicate_Conflict_UnknownUserOrRole_NotFound()
    {
        var projectId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var (service, _) = Create(Manager("mgr"), s =>
        {
            SeedStandardRoles(s);
            var appId = Guid.NewGuid();
            s.Users.Add(new User { Id = appId, ExternalIdentityId = "mgr", Email = "m@x", DisplayName = "M" });
            s.Users.Add(new User { Id = userId, ExternalIdentityId = "new", Email = "n@x", DisplayName = "N" });
            s.Projects.Add(new Project { Id = projectId, Name = "P", Key = "P1" });
            s.Members.Add(new ProjectMember { ProjectId = projectId, UserId = appId, RoleId = QaLeadRoleId });
        }, userSub: "mgr");

        var added = await service.AddMemberAsync(projectId,
            new AddMemberCommand(userId, null, QaLeadRoleId), CancellationToken.None);
        Assert.Equal("qa-lead", added.RoleName);

        await Assert.ThrowsAsync<ConflictException>(() => service.AddMemberAsync(projectId,
            new AddMemberCommand(userId, null, QaLeadRoleId), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.AddMemberAsync(projectId,
            new AddMemberCommand(Guid.NewGuid(), null, QaLeadRoleId), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.AddMemberAsync(projectId,
            new AddMemberCommand(userId, null, Guid.NewGuid()), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.AddMemberAsync(projectId,
            new AddMemberCommand(null, "missing@example.com", QaLeadRoleId), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateMemberRoleAsync(
            projectId, Guid.NewGuid(), new UpdateMemberRoleCommand(QaLeadRoleId), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveMemberAsync(
            projectId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task AddMember_ByEmail_ResolvesUser()
    {
        var projectId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var (service, _) = Create(Manager("mgr"), s =>
        {
            SeedStandardRoles(s);
            var appId = Guid.NewGuid();
            s.Users.Add(new User { Id = appId, ExternalIdentityId = "mgr", Email = "m@x", DisplayName = "M" });
            s.Users.Add(new User { Id = userId, ExternalIdentityId = "by-email", Email = "ByEmail@Example.com", DisplayName = "E" });
            s.Projects.Add(new Project { Id = projectId, Name = "P", Key = "P1" });
            s.Members.Add(new ProjectMember { ProjectId = projectId, UserId = appId, RoleId = QaLeadRoleId });
        }, userSub: "mgr");

        var added = await service.AddMemberAsync(projectId,
            new AddMemberCommand(null, "byemail@example.COM", QaLeadRoleId), CancellationToken.None);
        Assert.Equal(userId, added.UserId);
    }

    [Fact]
    public async Task ListRoles_ReturnsReferenceData()
    {
        var (service, _) = Create(Tester(), SeedStandardRoles);
        var roles = await service.ListRolesAsync(CancellationToken.None);
        Assert.Contains(roles, r => r.Name == "qa-lead");

        var (anon, _) = Create(new StubCurrentUser { IsAuthenticated = false }, SeedStandardRoles);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => anon.ListRolesAsync(CancellationToken.None));
    }

    // ---------- environments ----------

    [Fact]
    public async Task DeleteDefaultEnvironment_ClearsDefault_AndArchives()
    {
        var projectId = Guid.NewGuid();
        var envId = Guid.NewGuid();
        var (service, store) = Create(Manager("mgr"), s =>
        {
            SeedStandardRoles(s);
            var appId = Guid.NewGuid();
            s.Users.Add(new User { Id = appId, ExternalIdentityId = "mgr", Email = "m@x", DisplayName = "M" });
            s.Projects.Add(new Project { Id = projectId, Name = "P", Key = "P1", DefaultEnvironmentId = envId });
            s.Members.Add(new ProjectMember { ProjectId = projectId, UserId = appId, RoleId = QaLeadRoleId });
            s.Environments.Add(new TestEnvironment { Id = envId, ProjectId = projectId, Name = "QA" });
        }, userSub: "mgr");

        await service.DeleteEnvironmentAsync(envId, CancellationToken.None);

        var project = await store.GetByIdAsync(projectId, CancellationToken.None);
        Assert.Null(project!.DefaultEnvironmentId);
        var env = await store.GetEnvironmentByIdAsync(envId, CancellationToken.None);
        Assert.Equal(ProjectStatus.Archived, env!.Status);
    }

    [Fact]
    public async Task UpdateEnvironment_SetAsDefault_LinksProject()
    {
        var projectId = Guid.NewGuid();
        var envId = Guid.NewGuid();
        var (service, store) = Create(Manager("mgr"), s =>
        {
            SeedStandardRoles(s);
            var appId = Guid.NewGuid();
            s.Users.Add(new User { Id = appId, ExternalIdentityId = "mgr", Email = "m@x", DisplayName = "M" });
            s.Projects.Add(new Project { Id = projectId, Name = "P", Key = "P1" });
            s.Members.Add(new ProjectMember { ProjectId = projectId, UserId = appId, RoleId = QaLeadRoleId });
            s.Environments.Add(new TestEnvironment { Id = envId, ProjectId = projectId, Name = "QA" });
        }, userSub: "mgr");

        var updated = await service.UpdateEnvironmentAsync(envId,
            new UpdateEnvironmentCommand(null, "https://qa.example", null, true),
            CancellationToken.None);

        Assert.True(updated.IsDefault);
        Assert.Equal("https://qa.example", updated.BaseUrl);
        var project = await store.GetByIdAsync(projectId, CancellationToken.None);
        Assert.Equal(envId, project!.DefaultEnvironmentId);
    }

    [Fact]
    public async Task List_OnlyAccessibleProjects_ForNonAdmin()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var (service, _) = Create(Tester("member"), s =>
        {
            s.Users.Add(new User { Id = appId, ExternalIdentityId = "member", Email = "m@x", DisplayName = "M" });
            s.Projects.Add(new Project { Id = projectA, Name = "Alpha", Key = "ALPHA" });
            s.Projects.Add(new Project { Id = projectB, Name = "Beta", Key = "BETA" });
            s.Members.Add(new ProjectMember { ProjectId = projectA, UserId = appId, RoleId = Guid.NewGuid() });
        }, userSub: "member", userAppId: appId);

        var result = await service.ListAsync(1, 25, null, CancellationToken.None);
        Assert.Single(result.Items);
        Assert.Equal("ALPHA", result.Items[0].Key);
        Assert.Equal(1, result.TotalCount);
    }
}
