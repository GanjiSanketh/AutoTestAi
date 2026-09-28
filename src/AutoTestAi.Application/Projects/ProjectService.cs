using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.Projects;

namespace AutoTestAi.Application.Projects;

/// <summary>
/// Project management use cases. Every method enforces server-side authorization;
/// the store performs no authorization checks.
/// </summary>
public sealed class ProjectService : IProjectService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int MaxNameLength = 200;
    private const int MaxUrlLength = 2000;

    private readonly IProjectStore _store;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public ProjectService(
        IProjectStore store,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _store = store;
        _currentUser = currentUser;
        _authorization = authorization;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<PagedResult<ProjectListItemDto>> ListAsync(
        int page, int pageSize, string? search, CancellationToken cancellationToken)
    {
        RequireAuthenticated();
        RequirePermission(Permissions.ProjectsRead);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var totalCount = await _store.CountAccessibleAsync(
            _currentUser.ExternalIdentityId, _authorization.IsAdmin(), search, cancellationToken);
        var rows = await _store.ListAccessibleAsync(
            _currentUser.ExternalIdentityId, _authorization.IsAdmin(), search,
            (page - 1) * pageSize, pageSize, cancellationToken);

        return new PagedResult<ProjectListItemDto>(
            rows.Select(MapListItem).ToList(), totalCount, page, pageSize);
    }

    public async Task<ProjectDto> GetByIdAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsRead, cancellationToken);
        var project = await _store.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException("Project not found.");
        var members = await _store.ListMembersAsync(projectId, cancellationToken);
        return MapDetails(project, members.Count, await DefaultEnvironmentAsync(project, cancellationToken));
    }

    public async Task<ProjectDto> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        RequireAuthenticated();
        RequirePermission(Permissions.ProjectsManage);
        ArgumentNullException.ThrowIfNull(command);

        var errors = ValidateProjectFields(
            command.Name, command.Key, command.RepositoryUrl, command.TargetUrl,
            command.Framework, command.Platform, keyRequired: true);
        var key = ProjectKey.Normalize(command.Key);
        ValidationException.ThrowIfInvalid(errors);
        if (await _store.GetByKeyAsync(key, cancellationToken) is not null)
            throw new ConflictException($"Project key '{key}' is already in use.");

        var statusErrors = new List<FieldError>();
        var status = ParseStatus(command.Status, "status", statusErrors);
        ValidationException.ThrowIfInvalid(statusErrors);

        var project = new Project
        {
            Name = command.Name.Trim(),
            Key = key,
            Description = BlankToNull(command.Description),
            RepositoryUrl = BlankToNull(command.RepositoryUrl),
            TargetUrl = BlankToNull(command.TargetUrl),
            Framework = BlankToNull(command.Framework),
            Platform = BlankToNull(command.Platform),
            Status = status ?? ProjectStatus.Active,
            CreatedBy = await ResolveAppUserIdAsync(cancellationToken),
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };
        await _store.AddProjectAsync(project, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);

        // The creator becomes a project member so a non-admin creator keeps access.
        var creatorRole = await _store.GetOrCreateRoleAsync(
            "qa-lead", "QA lead / manager (FR-1.1)", cancellationToken);
        var creatorUserId = project.CreatedBy;
        if (creatorUserId.HasValue)
        {
            await _store.AddMemberAsync(new ProjectMember
            {
                ProjectId = project.Id,
                UserId = creatorUserId.Value,
                RoleId = creatorRole.Id,
            }, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }

        await _audit.RecordAsync("project.created", "project", project.Id.ToString(),
            project.Id, JsonSerializer.Serialize(new { project.Key }), cancellationToken);

        return MapDetails(project, creatorUserId.HasValue ? 1 : 0, null);
    }

    public async Task<ProjectDto> UpdateAsync(
        Guid projectId, UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsManage, cancellationToken);
        ArgumentNullException.ThrowIfNull(command);

        var project = await _store.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException("Project not found.");

        var errors = ValidateProjectFields(
            command.Name, null, command.RepositoryUrl, command.TargetUrl,
            command.Framework, command.Platform, keyRequired: false);
        var status = ParseStatus(command.Status, "status", errors);
        ValidationException.ThrowIfInvalid(errors);

        project.Name = command.Name.Trim();
        project.Description = BlankToNull(command.Description);
        project.RepositoryUrl = BlankToNull(command.RepositoryUrl);
        project.TargetUrl = BlankToNull(command.TargetUrl);
        project.Framework = BlankToNull(command.Framework);
        project.Platform = BlankToNull(command.Platform);
        if (status.HasValue) project.Status = status.Value;

        if (command.DefaultEnvironmentId.HasValue)
        {
            var env = await _store.GetEnvironmentByIdAsync(command.DefaultEnvironmentId.Value, cancellationToken);
            if (env is null || env.ProjectId != projectId)
                throw new ValidationException("Default environment must belong to the same project.",
                    new[] { new FieldError("defaultEnvironmentId", "Environment does not belong to this project.") });
            if (project.DefaultEnvironmentId != env.Id)
            {
                project.DefaultEnvironmentId = env.Id;
                await _audit.RecordAsync("project.default_environment_changed", "project",
                    project.Id.ToString(), project.Id,
                    JsonSerializer.Serialize(new { environmentId = env.Id }), cancellationToken);
            }
        }
        else
        {
            project.DefaultEnvironmentId = null;
        }

        project.UpdatedAt = _clock.UtcNow;
        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("project.updated", "project", project.Id.ToString(),
            project.Id, null, cancellationToken);

        var members = await _store.ListMembersAsync(projectId, cancellationToken);
        return MapDetails(project, members.Count, await DefaultEnvironmentAsync(project, cancellationToken));
    }

    public async Task ArchiveAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsManage, cancellationToken);
        var project = await _store.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException("Project not found.");

        if (project.Status != ProjectStatus.Archived)
        {
            project.Status = ProjectStatus.Archived;
            project.UpdatedAt = _clock.UtcNow;
            await _store.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync("project.archived", "project", project.Id.ToString(),
                project.Id, null, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ProjectMemberDto>> GetMembersAsync(
        Guid projectId, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsRead, cancellationToken);
        var rows = await _store.ListMembersAsync(projectId, cancellationToken);
        return rows.Select(r => new ProjectMemberDto(
            r.UserId, r.Email, r.DisplayName, r.RoleId, r.RoleName, r.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken)
    {
        RequireAuthenticated();
        var roles = await _store.ListRolesAsync(cancellationToken);
        return roles.Select(r => new RoleDto(r.Id, r.Name, r.Description)).ToList();
    }

    public async Task<ProjectMemberDto> AddMemberAsync(
        Guid projectId, AddMemberCommand command, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsManage, cancellationToken);
        ArgumentNullException.ThrowIfNull(command);

        _ = await _store.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException("Project not found.");
        User? user = command.UserId.HasValue
            ? await _store.GetUserByIdAsync(command.UserId.Value, cancellationToken)
            : null;
        user ??= !string.IsNullOrWhiteSpace(command.Email)
            ? await _store.GetUserByEmailAsync(command.Email.Trim(), cancellationToken)
            : null;
        if (user is null)
        {
            if (!command.UserId.HasValue && string.IsNullOrWhiteSpace(command.Email))
                throw new ValidationException("User is required.",
                    new[] { new FieldError("userId", "Provide a user id or email.") });
            throw new NotFoundException("User not found.");
        }
        var role = await _store.GetRoleByIdAsync(command.RoleId, cancellationToken)
            ?? throw new NotFoundException("Role not found.");
        if (await _store.FindMemberAsync(projectId, user.Id, cancellationToken) is not null)
            throw new ConflictException("User is already a member of this project.");

        var member = new ProjectMember
        {
            ProjectId = projectId,
            UserId = user.Id,
            RoleId = role.Id,
            CreatedAt = _clock.UtcNow,
        };
        await _store.AddMemberAsync(member, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("project.member_added", "project_member", user.Id.ToString(),
            projectId, JsonSerializer.Serialize(new { role = role.Name }), cancellationToken);

        return new ProjectMemberDto(user.Id, user.Email, user.DisplayName, role.Id, role.Name, member.CreatedAt);
    }

    public async Task<ProjectMemberDto> UpdateMemberRoleAsync(
        Guid projectId, Guid userId, UpdateMemberRoleCommand command, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsManage, cancellationToken);
        ArgumentNullException.ThrowIfNull(command);

        var member = await _store.FindMemberAsync(projectId, userId, cancellationToken)
            ?? throw new NotFoundException("Project member not found.");
        var role = await _store.GetRoleByIdAsync(command.RoleId, cancellationToken)
            ?? throw new NotFoundException("Role not found.");
        var user = await _store.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("User not found.");

        // RoleId is part of the project_members composite PK (docs/05), so a role
        // change replaces the membership row instead of mutating the key.
        await _store.RemoveMemberAsync(member, cancellationToken);
        var updated = new ProjectMember
        {
            ProjectId = projectId,
            UserId = user.Id,
            RoleId = role.Id,
            CreatedAt = _clock.UtcNow,
        };
        await _store.AddMemberAsync(updated, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("project.member_role_updated", "project_member", user.Id.ToString(),
            projectId, JsonSerializer.Serialize(new { role = role.Name }), cancellationToken);

        return new ProjectMemberDto(user.Id, user.Email, user.DisplayName, role.Id, role.Name, updated.CreatedAt);
    }

    public async Task RemoveMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsManage, cancellationToken);
        var member = await _store.FindMemberAsync(projectId, userId, cancellationToken)
            ?? throw new NotFoundException("Project member not found.");
        await _store.RemoveMemberAsync(member, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("project.member_removed", "project_member", userId.ToString(),
            projectId, null, cancellationToken);
    }

    public async Task<IReadOnlyList<EnvironmentDto>> ListEnvironmentsAsync(
        Guid projectId, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsRead, cancellationToken);
        var project = await _store.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException("Project not found.");
        var envs = await _store.ListEnvironmentsAsync(projectId, cancellationToken);
        return envs.Select(e => MapEnvironment(e, project.DefaultEnvironmentId)).ToList();
    }

    public async Task<EnvironmentDto> CreateEnvironmentAsync(
        Guid projectId, CreateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ProjectsManage, cancellationToken);
        ArgumentNullException.ThrowIfNull(command);

        _ = await _store.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException("Project not found.");
        var errors = ValidateEnvironmentFields(command.Name, command.BaseUrl);
        var status = ParseStatus(command.Status, "status", errors);
        ValidationException.ThrowIfInvalid(errors);

        var env = new TestEnvironment
        {
            ProjectId = projectId,
            Name = command.Name.Trim(),
            BaseUrl = BlankToNull(command.BaseUrl),
            Status = status ?? ProjectStatus.Active,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };
        await _store.AddEnvironmentAsync(env, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("environment.created", "environment", env.Id.ToString(),
            projectId, null, cancellationToken);

        return MapEnvironment(env, null);
    }

    public async Task<EnvironmentDto> UpdateEnvironmentAsync(
        Guid environmentId, UpdateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var env = await _store.GetEnvironmentByIdAsync(environmentId, cancellationToken)
            ?? throw new NotFoundException("Environment not found.");
        await _authorization.RequireProjectAccessAsync(
            env.ProjectId, Permissions.ProjectsManage, cancellationToken);

        var errors = new List<FieldError>();
        if (command.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(command.Name)) errors.Add(new FieldError("name", "Name must not be empty."));
            else if (command.Name.Trim().Length > MaxNameLength) errors.Add(new FieldError("name", $"Name must be at most {MaxNameLength} characters."));
        }
        if (command.BaseUrl is not null) ValidateUrl(command.BaseUrl, "baseUrl", errors);
        var status = ParseStatus(command.Status, "status", errors);
        ValidationException.ThrowIfInvalid(errors);

        if (command.Name is not null) env.Name = command.Name.Trim();
        if (command.BaseUrl is not null) env.BaseUrl = BlankToNull(command.BaseUrl);
        if (status.HasValue) env.Status = status.Value;

        Guid? defaultId = null;
        if (command.SetAsDefault == true)
        {
            var project = await _store.GetByIdAsync(env.ProjectId, cancellationToken)
                ?? throw new NotFoundException("Project not found.");
            project.DefaultEnvironmentId = env.Id;
            project.UpdatedAt = _clock.UtcNow;
            defaultId = env.Id;
            await _audit.RecordAsync("project.default_environment_changed", "project",
                project.Id.ToString(), project.Id,
                JsonSerializer.Serialize(new { environmentId = env.Id }), cancellationToken);
        }
        else
        {
            var project = await _store.GetByIdAsync(env.ProjectId, cancellationToken);
            defaultId = project?.DefaultEnvironmentId;
        }

        env.UpdatedAt = _clock.UtcNow;
        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("environment.updated", "environment", env.Id.ToString(),
            env.ProjectId, null, cancellationToken);

        return MapEnvironment(env, defaultId);
    }

    public async Task DeleteEnvironmentAsync(Guid environmentId, CancellationToken cancellationToken)
    {
        var env = await _store.GetEnvironmentByIdAsync(environmentId, cancellationToken)
            ?? throw new NotFoundException("Environment not found.");
        await _authorization.RequireProjectAccessAsync(
            env.ProjectId, Permissions.ProjectsManage, cancellationToken);

        var project = await _store.GetByIdAsync(env.ProjectId, cancellationToken);
        if (project is not null && project.DefaultEnvironmentId == env.Id)
        {
            project.DefaultEnvironmentId = null;
            project.UpdatedAt = _clock.UtcNow;
        }

        if (env.Status != ProjectStatus.Archived)
        {
            env.Status = ProjectStatus.Archived;
            env.UpdatedAt = _clock.UtcNow;
        }
        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("environment.deleted", "environment", env.Id.ToString(),
            env.ProjectId, null, cancellationToken);
    }

    // ---------- helpers ----------

    private void RequireAuthenticated()
    {
        if (!_currentUser.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
    }

    private void RequirePermission(string permission)
    {
        if (!_authorization.HasPermission(permission))
            throw new ForbiddenException($"Missing required permission '{permission}'.");
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);

    private static List<FieldError> ValidateProjectFields(
        string? name, string? key, string? repositoryUrl, string? targetUrl,
        string? framework, string? platform, bool keyRequired)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add(new FieldError("name", "Name is required."));
        else if (name.Trim().Length > MaxNameLength) errors.Add(new FieldError("name", $"Name must be at most {MaxNameLength} characters."));
        if (keyRequired)
        {
            var normalized = ProjectKey.Normalize(key);
            if (normalized.Length == 0) errors.Add(new FieldError("key", "Key is required."));
            else if (normalized.Length > ProjectKey.MaxLength) errors.Add(new FieldError("key", $"Key must be at most {ProjectKey.MaxLength} characters."));
            else if (!ProjectKey.IsValidFormat(normalized)) errors.Add(new FieldError("key", "Key must start with a letter and contain only letters, digits, '_' or '-'."));
        }
        if (repositoryUrl is not null) ValidateUrl(repositoryUrl, "repositoryUrl", errors);
        if (targetUrl is not null) ValidateUrl(targetUrl, "targetUrl", errors);
        if (framework is not null && framework.Trim().Length > 100) errors.Add(new FieldError("framework", "Framework must be at most 100 characters."));
        if (platform is not null && platform.Trim().Length > 100) errors.Add(new FieldError("platform", "Platform must be at most 100 characters."));
        return errors;
    }

    private static List<FieldError> ValidateEnvironmentFields(string? name, string? baseUrl)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add(new FieldError("name", "Name is required."));
        else if (name.Trim().Length > MaxNameLength) errors.Add(new FieldError("name", $"Name must be at most {MaxNameLength} characters."));
        if (baseUrl is not null) ValidateUrl(baseUrl, "baseUrl", errors);
        return errors;
    }

    private static void ValidateUrl(string value, string field, List<FieldError> errors)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxUrlLength ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add(new FieldError(field, "Must be an absolute http(s) URL."));
    }

    private static ProjectStatus? ParseStatus(string? status, string field, List<FieldError>? errors)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        if (Enum.TryParse<ProjectStatus>(status.Trim(), ignoreCase: true, out var parsed))
            return parsed;
        errors?.Add(new FieldError(field, "Status must be 'Active' or 'Archived'."));
        return null;
    }

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<EnvironmentSummaryDto?> DefaultEnvironmentAsync(
        Project project, CancellationToken cancellationToken)
    {
        if (!project.DefaultEnvironmentId.HasValue) return null;
        var env = await _store.GetEnvironmentByIdAsync(project.DefaultEnvironmentId.Value, cancellationToken);
        return env is null ? null : new EnvironmentSummaryDto(env.Id, env.Name, env.BaseUrl, env.Status.ToString());
    }

    private static ProjectListItemDto MapListItem(ProjectListRow row) => new(
        row.Project.Id, row.Project.Name, row.Project.Key, row.Project.Description,
        row.Project.RepositoryUrl, row.Project.TargetUrl, row.Project.Framework, row.Project.Platform,
        row.Project.Status.ToString(), row.MemberCount, row.Project.UpdatedAt);

    private static ProjectDto MapDetails(Project project, int memberCount, EnvironmentSummaryDto? defaultEnv) => new(
        project.Id, project.Name, project.Key, project.Description,
        project.RepositoryUrl, project.TargetUrl, project.Framework, project.Platform,
        project.Status.ToString(), project.DefaultEnvironmentId, defaultEnv,
        memberCount, project.CreatedBy, project.CreatedAt, project.UpdatedAt);

    private static EnvironmentDto MapEnvironment(TestEnvironment env, Guid? defaultId) => new(
        env.Id, env.ProjectId, env.Name, env.BaseUrl, env.Status.ToString(),
        defaultId.HasValue && defaultId.Value == env.Id, env.CreatedAt, env.UpdatedAt);
}
