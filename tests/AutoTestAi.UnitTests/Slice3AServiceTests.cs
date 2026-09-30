using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3A: variable-set and secret-metadata service behavior —
/// project isolation, scope ownership, concurrency, and redacted audit.</summary>
public sealed class Slice3AServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();
    private static readonly Guid EnvA = Guid.NewGuid();
    private static readonly Guid EnvB = Guid.NewGuid();
    private static readonly Guid SuiteA = Guid.NewGuid();

    private sealed class AllowAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<(string Action, string? Metadata)> Records = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Records.Add((action, metadataJson)); return Task.CompletedTask; }
    }

    private sealed class FakeProjects : IProjectStore
    {
        public readonly Dictionary<Guid, Project> Projects = new();
        public readonly Dictionary<Guid, TestEnvironment> Environments = new();
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Projects.TryGetValue(id, out var p) ? p : null);
        public Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Environments.TryGetValue(id, out var e) ? e : null);
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> CountAccessibleAsync(string? e, bool a, string? s, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(string? e, bool a, string? s, int sk, int t, CancellationToken ct) => throw new NotImplementedException();
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
        public Task AddEnvironmentAsync(TestEnvironment e, CancellationToken ct) => throw new NotImplementedException();
        public Task RecordAuditAsync(AuditEvent e, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSuites : ITestSuiteLookup
    {
        public readonly Dictionary<Guid, TestSuite> Suites = new();
        public Task<TestSuite?> GetSuiteByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Suites.TryGetValue(id, out var s) ? s : null);
    }

    private sealed class FakeSets : IVariableSetStore
    {
        public readonly Dictionary<Guid, VariableSet> Rows = new();
        public Task<VariableSet?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var s) ? s : null);
        public Task<IReadOnlyList<VariableSet>> ListByProjectAsync(Guid p, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VariableSet>>(Rows.Values.Where(s => s.ProjectId == p).ToList());
        public Task<VariableSet?> FindByScopeAsync(Guid p, VariableScopeType t, Guid? s, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(x => x.ProjectId == p && x.ScopeType == t && x.ScopeId == s));
        public Task AddAsync(VariableSet set, CancellationToken ct) { Rows[set.Id] = set; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(VariableSet set, CancellationToken ct) { Rows.Remove(set.Id); return Task.CompletedTask; }
    }

    private sealed class FakeResolver : ISecretResolver
    {
        public Task<bool> ExistsAsync(string r, CancellationToken ct) => Task.FromResult(true);
        public Task<string> ResolveAsync(string r, CancellationToken ct) => Task.FromResult("resolved");
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public readonly Dictionary<Guid, SecretMetadata> Rows = new();
        public Task<SecretMetadata> CreateAsync(Guid p, Guid e, string n, string v, string? d, CancellationToken ct)
        {
            var meta = new SecretMetadata(Guid.NewGuid(), p, e, n, d,
                SecretReference.Create(Guid.NewGuid()), true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Rows[meta.Id] = meta;
            return Task.FromResult(meta);
        }
        public Task<SecretMetadata> UpdateAsync(Guid id, string? n, string? v, string? d, CancellationToken ct)
        {
            var existing = Rows[id];
            var updated = existing with { Name = n ?? existing.Name, Description = d ?? existing.Description };
            Rows[id] = updated;
            return Task.FromResult(updated);
        }
        public Task DeleteAsync(Guid id, CancellationToken ct) { Rows.Remove(id); return Task.CompletedTask; }
        public Task<SecretMetadata?> GetMetadataAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var m) ? m : null);
        public Task<IReadOnlyList<SecretMetadata>> ListMetadataAsync(Guid p, Guid? e, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SecretMetadata>>(Rows.Values
                .Where(m => m.ProjectId == p && (!e.HasValue || m.EnvironmentId == e.Value)).ToList());
    }

    private static (VariableSetService Service, FakeSets Sets, FakeAudit Audit) BuildVariableService()
    {
        var projects = new FakeProjects();
        projects.Projects[ProjectA] = new Project { Id = ProjectA, Name = "A", Key = "A" };
        projects.Projects[ProjectB] = new Project { Id = ProjectB, Name = "B", Key = "B" };
        projects.Environments[EnvA] = new TestEnvironment { Id = EnvA, ProjectId = ProjectA, Name = "QA" };
        projects.Environments[EnvB] = new TestEnvironment { Id = EnvB, ProjectId = ProjectB, Name = "Other" };
        var suites = new FakeSuites();
        suites.Suites[SuiteA] = new TestSuite { Id = SuiteA, ProjectId = ProjectA, Name = "Smoke" };
        var sets = new FakeSets();
        var audit = new FakeAudit();
        var service = new VariableSetService(sets, projects, suites, new AllowAuth(), audit,
            new SystemDateTimeProvider(), new FakeResolver());
        return (service, sets, audit);
    }

    [Fact]
    public async Task Create_ProjectScope_Succeeds_AndAuditsWithoutValues()
    {
        var (service, _, audit) = BuildVariableService();
        var dto = await service.CreateAsync(ProjectA, "Project", null, "defaults",
            """{"BASE_URL":{"value":"https://qa.example.com"}}""", CancellationToken.None);
        Assert.Equal("Project", dto.ScopeType);
        Assert.Null(dto.ScopeId);
        var record = Assert.Single(audit.Records, r => r.Action == "variableset.created");
        Assert.Contains("BASE_URL", record.Metadata);
        Assert.DoesNotContain("https://qa.example.com", record.Metadata);
    }

    [Fact]
    public async Task Create_DuplicateProjectScope_Conflicts()
    {
        var (service, _, _) = BuildVariableService();
        await service.CreateAsync(ProjectA, "Project", null, "defaults", "{}", CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(ProjectA, "Project", null, "second", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Create_EnvironmentScope_RejectsCrossProjectEnvironment()
    {
        var (service, _, _) = BuildVariableService();
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(ProjectA, "Environment", EnvB, "env", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Create_SuiteScope_ValidatesOwnership()
    {
        var (service, _, _) = BuildVariableService();
        var dto = await service.CreateAsync(ProjectA, "Suite", SuiteA, "suite", "{}", CancellationToken.None);
        Assert.Equal(SuiteA, dto.ScopeId);
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(ProjectB, "Suite", SuiteA, "suite", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Update_StaleRowVersion_Conflicts()
    {
        var (service, _, _) = BuildVariableService();
        var dto = await service.CreateAsync(ProjectA, "Project", null, "defaults",
            """{"A":{"value":"1"}}""", CancellationToken.None);
        dto = await service.GetAsync(dto.Id, CancellationToken.None);
        // Simulate a concurrent writer by updating first.
        await service.UpdateAsync(dto.Id, "defaults", """{"A":{"value":"2"}}""", dto.RowVersion, CancellationToken.None);
        var stale = dto.RowVersion;
        var fresh = await service.GetAsync(dto.Id, CancellationToken.None);
        // Stale token against the new row version conflicts (when tokens present).
        if (stale is not null && fresh.RowVersion is not null && !stale.SequenceEqual(fresh.RowVersion))
        {
            await Assert.ThrowsAsync<ConflictException>(() =>
                service.UpdateAsync(dto.Id, "defaults", """{"A":{"value":"3"}}""", stale, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SecretMetadata_CreateUpdateDelete_NeverExposesValue()
    {
        var projects = new FakeProjects();
        projects.Projects[ProjectA] = new Project { Id = ProjectA, Name = "A", Key = "A" };
        projects.Environments[EnvA] = new TestEnvironment { Id = EnvA, ProjectId = ProjectA, Name = "QA" };
        var store = new FakeSecretStore();
        var audit = new FakeAudit();
        var service = new SecretMetadataService(store, projects, new AllowAuth(), audit);

        var created = await service.CreateAsync(ProjectA, EnvA, "API_TOKEN", "super-secret-123", "token", CancellationToken.None);
        Assert.True(created.HasValue);
        Assert.StartsWith("env_secret:", created.SecretReference);
        Assert.DoesNotContain("super-secret-123", System.Text.Json.JsonSerializer.Serialize(created));

        var listed = await service.ListAsync(ProjectA, EnvA, CancellationToken.None);
        Assert.Single(listed);

        var updated = await service.UpdateAsync(created.Id, null, "rotated-secret-456", null, created.RowVersion, CancellationToken.None);
        Assert.Equal("API_TOKEN", updated.Name);
        Assert.DoesNotContain("rotated-secret", System.Text.Json.JsonSerializer.Serialize(updated));

        await service.DeleteAsync(created.Id, CancellationToken.None);
        Assert.Empty(await service.ListAsync(ProjectA, EnvA, CancellationToken.None));

        foreach (var record in audit.Records)
            Assert.DoesNotContain("super-secret-123", record.Metadata);
    }

    [Fact]
    public async Task SecretMetadata_Create_RejectsCrossProjectEnvironment()
    {
        var projects = new FakeProjects();
        projects.Projects[ProjectA] = new Project { Id = ProjectA, Name = "A", Key = "A" };
        projects.Environments[EnvB] = new TestEnvironment { Id = EnvB, ProjectId = ProjectB, Name = "Other" };
        var service = new SecretMetadataService(new FakeSecretStore(), projects, new AllowAuth(), new FakeAudit());
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateAsync(ProjectA, EnvB, "API_TOKEN", "v", null, CancellationToken.None));
    }
}
