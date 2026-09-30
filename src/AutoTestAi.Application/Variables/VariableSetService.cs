using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Variables;

/// <summary>Variable-set management use cases (Slice 3A). Project-isolated, audited.</summary>
public interface IVariableSetService
{
    Task<VariableSetDto> CreateAsync(Guid projectId, string scopeType, Guid? scopeId, string name, string variablesJson, CancellationToken ct);
    Task<IReadOnlyList<VariableSetDto>> ListAsync(Guid projectId, CancellationToken ct);
    Task<VariableSetDto> GetAsync(Guid id, CancellationToken ct);
    Task<VariableSetDto> UpdateAsync(Guid id, string name, string variablesJson, byte[]? rowVersion, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

public sealed class VariableSetService : IVariableSetService
{
    private readonly IVariableSetStore _store;
    private readonly IProjectStore _projects;
    private readonly ITestSuiteLookup _suites;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ISecretResolver _secrets;

    public VariableSetService(
        IVariableSetStore store,
        IProjectStore projects,
        ITestSuiteLookup suites,
        IAuthorizationService authorization,
        IAuditService audit,
        IDateTimeProvider clock,
        ISecretResolver secrets)
    {
        _store = store;
        _projects = projects;
        _suites = suites;
        _authorization = authorization;
        _audit = audit;
        _clock = clock;
        _secrets = secrets;
    }

    public async Task<VariableSetDto> CreateAsync(
        Guid projectId, string scopeType, Guid? scopeId, string name, string variablesJson, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.VariablesManage, ct);
        if (!VariableScopeTypes.TryParse(scopeType, out var scope))
            throw new ValidationException("Scope type is invalid.",
                new[] { new FieldError("scopeType", "Must be 'Project', 'Environment' or 'Suite'.") });
        await ValidateScopeAsync(projectId, scope, scopeId, ct);
        ValidateName(name);
        var (plain, refs) = VariableModel.ParseEntries(variablesJson);
        await ValidateSecretRefsAsync(projectId, refs, ct);

        if (scope == VariableScopeType.Project && scopeId is not null)
            throw new ValidationException("Project scope must not carry a scope id.",
                new[] { new FieldError("scopeId", "Project scope sets have no scope id.") });
        if (await _store.FindByScopeAsync(projectId, scope, scopeId, ct) is not null)
            throw new ConflictException("A variable set already exists for this scope.");

        var now = _clock.UtcNow;
        var set = new VariableSet
        {
            ProjectId = projectId,
            ScopeType = scope,
            ScopeId = scopeId,
            Name = name.Trim(),
            VariablesJson = VariableModel.SerializeEntries(plain, refs),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _store.AddAsync(set, ct);
        await _store.SaveChangesAsync(ct);
        await _audit.RecordAsync("variableset.created", "variable_set", set.Id.ToString(), projectId,
            RedactedMeta(set, plain, refs), ct);
        return Map(set, plain, refs);
    }

    public async Task<IReadOnlyList<VariableSetDto>> ListAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ProjectsRead, ct);
        var rows = await _store.ListByProjectAsync(projectId, ct);
        return rows.Select(r =>
        {
            var (plain, refs) = VariableModel.ParseEntries(r.VariablesJson);
            return Map(r, plain, refs);
        }).ToList();
    }

    public async Task<VariableSetDto> GetAsync(Guid id, CancellationToken ct)
    {
        var set = await _store.GetByIdAsync(id, ct) ?? throw new NotFoundException("Variable set not found.");
        await _authorization.RequireProjectAccessAsync(set.ProjectId, Permissions.ProjectsRead, ct);
        var (plain, refs) = VariableModel.ParseEntries(set.VariablesJson);
        return Map(set, plain, refs);
    }

    public async Task<VariableSetDto> UpdateAsync(
        Guid id, string name, string variablesJson, byte[]? rowVersion, CancellationToken ct)
    {
        var set = await _store.GetByIdAsync(id, ct) ?? throw new NotFoundException("Variable set not found.");
        await _authorization.RequireProjectAccessAsync(set.ProjectId, Permissions.VariablesManage, ct);
        ValidateName(name);
        var (plain, refs) = VariableModel.ParseEntries(variablesJson);
        await ValidateSecretRefsAsync(set.ProjectId, refs, ct);

        if (rowVersion is not null && set.RowVersion is not null && !rowVersion.SequenceEqual(set.RowVersion))
            throw new ConflictException("The variable set was modified by another user. Reload and retry.");

        set.Name = name.Trim();
        set.VariablesJson = VariableModel.SerializeEntries(plain, refs);
        set.UpdatedAt = _clock.UtcNow;
        await _store.SaveChangesAsync(ct);
        await _audit.RecordAsync("variableset.updated", "variable_set", set.Id.ToString(), set.ProjectId,
            RedactedMeta(set, plain, refs), ct);
        return Map(set, plain, refs);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var set = await _store.GetByIdAsync(id, ct) ?? throw new NotFoundException("Variable set not found.");
        await _authorization.RequireProjectAccessAsync(set.ProjectId, Permissions.VariablesManage, ct);
        await _store.DeleteAsync(set, ct);
        await _store.SaveChangesAsync(ct);
        await _audit.RecordAsync("variableset.deleted", "variable_set", set.Id.ToString(), set.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new { variableSetId = set.Id })), ct);
    }

    private async Task ValidateScopeAsync(Guid projectId, VariableScopeType scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope == VariableScopeType.Project)
        {
            if (scopeId is not null)
                throw new ValidationException("Project scope must not carry a scope id.",
                    new[] { new FieldError("scopeId", "Project scope sets have no scope id.") });
            _ = await _projects.GetByIdAsync(projectId, ct) ?? throw new NotFoundException("Project not found.");
            return;
        }
        if (!scopeId.HasValue || scopeId.Value == Guid.Empty)
            throw new ValidationException("Scope id is required.",
                new[] { new FieldError("scopeId", "Environment and Suite scopes require a scope id.") });
        if (scope == VariableScopeType.Environment)
        {
            var env = await _projects.GetEnvironmentByIdAsync(scopeId.Value, ct);
            if (env is null || env.ProjectId != projectId)
                throw new ValidationException("Scope environment must belong to this project.",
                    new[] { new FieldError("scopeId", "Environment does not belong to this project.") });
        }
        else
        {
            var suite = await _suites.GetSuiteByIdAsync(scopeId.Value, ct);
            if (suite is null || suite.ProjectId != projectId)
                throw new ValidationException("Scope suite must belong to this project.",
                    new[] { new FieldError("scopeId", "Suite does not belong to this project.") });
        }
    }

    private static void ValidateName(string name)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add(new FieldError("name", "Name is required."));
        else if (name.Trim().Length > 200) errors.Add(new FieldError("name", "Name must be at most 200 characters."));
        ValidationException.ThrowIfInvalid(errors);
    }

    private async Task ValidateSecretRefsAsync(
        Guid projectId, Dictionary<string, string> refs, CancellationToken ct)
    {
        foreach (var (key, secretRef) in refs)
        {
            if (!SecretReference.TryParseSecretId(secretRef, out var secretId))
                throw new ValidationException("Invalid secret reference.",
                    new[] { new FieldError($"variables.{key}", "Must be an opaque secret reference (env_secret:<id>).") });
            // Existence is advisory here (allows creating sets before secrets);
            // resolution-time checks are authoritative. Cross-project safety is
            // enforced by the resolver scoping secrets to their project.
            _ = await _secrets.ExistsAsync(secretRef, ct);
            _ = secretId;
            _ = projectId;
        }
    }

    private static string RedactedMeta(
        VariableSet set,
        Dictionary<string, string> plain,
        Dictionary<string, string> refs)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            variableSetId = set.Id,
            scopeType = set.ScopeType.ToString(),
            scopeId = set.ScopeId,
            keys = plain.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            secretKeys = refs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            hasSecretRef = refs.Count > 0,
        }));

    private static VariableSetDto Map(
        VariableSet set,
        Dictionary<string, string> plain,
        Dictionary<string, string> refs)
        => new(set.Id, set.ProjectId, set.ScopeType.ToString(), set.ScopeId,
            set.Name, set.VariablesJson,
            plain.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            refs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            set.RowVersion, set.CreatedAt, set.UpdatedAt);
}
