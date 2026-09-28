using AutoTestAi.Application.Projects;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.Projects;

/// <summary>
/// Fail-closed store used when no database is configured: every operation
/// surfaces as a dependency failure (503) rather than inventing data.
/// </summary>
public sealed class UnavailableProjectStore : IProjectStore
{
    private static Task<T> Unavailable<T>() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    private static Task Unavailable() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    public Task<int> CountAccessibleAsync(string? externalIdentityId, bool isAdmin, string? search, CancellationToken ct)
        => Unavailable<int>();
    public Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(
        string? externalIdentityId, bool isAdmin, string? search, int skip, int take, CancellationToken ct)
        => Unavailable<IReadOnlyList<ProjectListRow>>();
    public Task<Project?> GetByIdAsync(Guid projectId, CancellationToken ct) => Unavailable<Project?>();
    public Task<Project?> GetByKeyAsync(string normalizedKey, CancellationToken ct) => Unavailable<Project?>();
    public Task AddProjectAsync(Project project, CancellationToken ct) => Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => Unavailable();
    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct) => Unavailable<User?>();
    public Task<User?> GetUserByEmailAsync(string email, CancellationToken ct) => Unavailable<User?>();
    public Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct) => Unavailable<IReadOnlyList<Role>>();
    public Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken ct) => Unavailable<Role?>();
    public Task<Role> GetOrCreateRoleAsync(string name, string? description, CancellationToken ct) => Unavailable<Role>();
    public Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct) => Unavailable<bool>();
    public Task<IReadOnlyList<MemberRow>> ListMembersAsync(Guid projectId, CancellationToken ct)
        => Unavailable<IReadOnlyList<MemberRow>>();
    public Task<ProjectMember?> FindMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
        => Unavailable<ProjectMember?>();
    public Task AddMemberAsync(ProjectMember member, CancellationToken ct) => Unavailable();
    public Task RemoveMemberAsync(ProjectMember member, CancellationToken ct) => Unavailable();
    public Task<IReadOnlyList<TestEnvironment>> ListEnvironmentsAsync(Guid projectId, CancellationToken ct)
        => Unavailable<IReadOnlyList<TestEnvironment>>();
    public Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid environmentId, CancellationToken ct)
        => Unavailable<TestEnvironment?>();
    public Task AddEnvironmentAsync(TestEnvironment environment, CancellationToken ct) => Unavailable();
    public Task RecordAuditAsync(AuditEvent auditEvent, CancellationToken ct) => Unavailable();
}
