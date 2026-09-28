using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.Projects;

// ---------- DTOs (safe fields only; never secrets) ----------

public sealed record ProjectListItemDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    string? RepositoryUrl,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string Status,
    int MemberCount,
    DateTimeOffset UpdatedAt);

public sealed record EnvironmentSummaryDto(
    Guid Id,
    string Name,
    string? BaseUrl,
    string Status);

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    string? RepositoryUrl,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string Status,
    Guid? DefaultEnvironmentId,
    EnvironmentSummaryDto? DefaultEnvironment,
    int MemberCount,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectMemberDto(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid RoleId,
    string RoleName,
    DateTimeOffset CreatedAt);

public sealed record EnvironmentDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? BaseUrl,
    string Status,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ---------- Commands ----------

public sealed record CreateProjectCommand(
    string Name,
    string Key,
    string? Description,
    string? RepositoryUrl,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string? Status);

public sealed record UpdateProjectCommand(
    string Name,
    string? Description,
    string? RepositoryUrl,
    string? TargetUrl,
    string? Framework,
    string? Platform,
    string? Status,
    Guid? DefaultEnvironmentId);

/// <summary>Identify the user by id or email (email is a lookup convenience only).</summary>
public sealed record AddMemberCommand(Guid? UserId, string? Email, Guid RoleId);

public sealed record RoleDto(Guid Id, string Name, string? Description);

public sealed record UpdateMemberRoleCommand(Guid RoleId);

public sealed record CreateEnvironmentCommand(string Name, string? BaseUrl, string? Status);

public sealed record UpdateEnvironmentCommand(
    string? Name,
    string? BaseUrl,
    string? Status,
    bool? SetAsDefault);

// ---------- Service ----------

/// <summary>
/// Project management use cases (docs/06 §5). All methods enforce
/// server-side authorization via <see cref="Authorization.IAuthorizationService"/>.
/// </summary>
public interface IProjectService
{
    Task<PagedResult<ProjectListItemDto>> ListAsync(
        int page, int pageSize, string? search, CancellationToken cancellationToken);

    Task<ProjectDto> GetByIdAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectDto> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken);

    Task<ProjectDto> UpdateAsync(Guid projectId, UpdateProjectCommand command, CancellationToken cancellationToken);

    /// <summary>Soft delete: sets status to Archived, preserving history.</summary>
    Task ArchiveAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectMemberDto>> GetMembersAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectMemberDto> AddMemberAsync(Guid projectId, AddMemberCommand command, CancellationToken cancellationToken);

    Task<ProjectMemberDto> UpdateMemberRoleAsync(
        Guid projectId, Guid userId, UpdateMemberRoleCommand command, CancellationToken cancellationToken);

    Task RemoveMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Reference data for member role assignment. Authenticated callers only.</summary>
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<EnvironmentDto>> ListEnvironmentsAsync(Guid projectId, CancellationToken cancellationToken);

    Task<EnvironmentDto> CreateEnvironmentAsync(
        Guid projectId, CreateEnvironmentCommand command, CancellationToken cancellationToken);

    Task<EnvironmentDto> UpdateEnvironmentAsync(
        Guid environmentId, UpdateEnvironmentCommand command, CancellationToken cancellationToken);

    /// <summary>Soft delete: sets status to Archived and clears default if set.</summary>
    Task DeleteEnvironmentAsync(Guid environmentId, CancellationToken cancellationToken);
}
