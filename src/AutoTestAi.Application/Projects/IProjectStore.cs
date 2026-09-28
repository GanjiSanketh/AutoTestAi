using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Projects;

/// <summary>
/// Persistence seam for the Projects module. Implemented in Infrastructure
/// with EF Core; throws when no database is configured (fail closed at the API).
/// Methods do NOT authorize — <see cref="IProjectService"/> enforces that.
/// </summary>
public interface IProjectStore
{
    /// <summary>
    /// Membership is resolved by stable external identity id (consistent with
    /// IProjectMembershipStore), so listing works even before JIT provisioning.
    /// </summary>
    Task<int> CountAccessibleAsync(string? externalIdentityId, bool isAdmin, string? search, CancellationToken ct);
    Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(
        string? externalIdentityId, bool isAdmin, string? search, int skip, int take, CancellationToken ct);

    Task<Project?> GetByIdAsync(Guid projectId, CancellationToken ct);
    Task<Project?> GetByKeyAsync(string normalizedKey, CancellationToken ct);
    Task AddProjectAsync(Project project, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);

    Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct);
    Task<User?> GetUserByEmailAsync(string email, CancellationToken ct);
    Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct);
    Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken ct);
    Task<Role> GetOrCreateRoleAsync(string name, string? description, CancellationToken ct);

    Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<MemberRow>> ListMembersAsync(Guid projectId, CancellationToken ct);
    Task<ProjectMember?> FindMemberAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task AddMemberAsync(ProjectMember member, CancellationToken ct);
    Task RemoveMemberAsync(ProjectMember member, CancellationToken ct);

    Task<IReadOnlyList<TestEnvironment>> ListEnvironmentsAsync(Guid projectId, CancellationToken ct);
    Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid environmentId, CancellationToken ct);
    Task AddEnvironmentAsync(TestEnvironment environment, CancellationToken ct);

    Task RecordAuditAsync(AuditEvent auditEvent, CancellationToken ct);
}

/// <summary>Project row with member count, avoiding N+1 queries.</summary>
public sealed record ProjectListRow(Project Project, int MemberCount);

/// <summary>Member row with joined user/role display data.</summary>
public sealed record MemberRow(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid RoleId,
    string RoleName,
    DateTimeOffset CreatedAt);
