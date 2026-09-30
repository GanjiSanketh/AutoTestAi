using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestGeneration;

namespace AutoTestAi.Application.Secrets;

/// <summary>Secret metadata management (Slice 3A §9). Values never leave the store.</summary>
public interface ISecretMetadataService
{
    Task<SecretMetadata> CreateAsync(Guid projectId, Guid environmentId, string name, string value, string? description, CancellationToken ct);
    Task<IReadOnlyList<SecretMetadata>> ListAsync(Guid projectId, Guid? environmentId, CancellationToken ct);
    Task<bool> ExistsAsync(Guid secretId, CancellationToken ct);
    Task<SecretMetadata> UpdateAsync(Guid secretId, string? name, string? value, string? description, byte[]? rowVersion, CancellationToken ct);
    Task DeleteAsync(Guid secretId, CancellationToken ct);
}

public sealed class SecretMetadataService : ISecretMetadataService
{
    private readonly ISecretStore _store;
    private readonly IProjectStore _projects;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditService _audit;

    public SecretMetadataService(
        ISecretStore store,
        IProjectStore projects,
        IAuthorizationService authorization,
        IAuditService audit)
    {
        _store = store;
        _projects = projects;
        _authorization = authorization;
        _audit = audit;
    }

    public async Task<SecretMetadata> CreateAsync(
        Guid projectId, Guid environmentId, string name, string value, string? description, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.SecretsManage, ct);
        var env = await _projects.GetEnvironmentByIdAsync(environmentId, ct);
        if (env is null || env.ProjectId != projectId)
            throw new ValidationException("Environment must belong to this project.",
                new[] { new FieldError("environmentId", "Environment does not belong to this project.") });
        ValidateName(name);
        ValidateValue(value);
        var created = await _store.CreateAsync(projectId, environmentId, name.Trim(), value, BlankToNull(description), ct);
        await _audit.RecordAsync("secret.metadata.created", "environment_secret", created.Id.ToString(), projectId,
            Meta(created), ct);
        return created;
    }

    public async Task<IReadOnlyList<SecretMetadata>> ListAsync(Guid projectId, Guid? environmentId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ProjectsRead, ct);
        if (environmentId.HasValue)
        {
            var env = await _projects.GetEnvironmentByIdAsync(environmentId.Value, ct);
            if (env is null || env.ProjectId != projectId)
                throw new ValidationException("Environment must belong to this project.",
                    new[] { new FieldError("environmentId", "Environment does not belong to this project.") });
        }
        return await _store.ListMetadataAsync(projectId, environmentId, ct);
    }

    public async Task<bool> ExistsAsync(Guid secretId, CancellationToken ct)
    {
        var meta = await _store.GetMetadataAsync(secretId, ct) ?? throw new NotFoundException("Secret not found.");
        await _authorization.RequireProjectAccessAsync(meta.ProjectId, Permissions.ProjectsRead, ct);
        return meta.HasValue;
    }

    public async Task<SecretMetadata> UpdateAsync(
        Guid secretId, string? name, string? value, string? description, byte[]? rowVersion, CancellationToken ct)
    {
        var existing = await _store.GetMetadataAsync(secretId, ct) ?? throw new NotFoundException("Secret not found.");
        await _authorization.RequireProjectAccessAsync(existing.ProjectId, Permissions.SecretsManage, ct);
        if (name is not null) ValidateName(name);
        if (value is not null) ValidateValue(value);
        if (rowVersion is not null && existing.RowVersion is not null && !rowVersion.SequenceEqual(existing.RowVersion))
            throw new ConflictException("The secret was modified by another user. Reload and retry.");
        var updated = await _store.UpdateAsync(secretId, name?.Trim(), value, description is null ? null : BlankToNull(description), ct);
        await _audit.RecordAsync("secret.metadata.updated", "environment_secret", updated.Id.ToString(), updated.ProjectId,
            Meta(updated), ct);
        return updated;
    }

    public async Task DeleteAsync(Guid secretId, CancellationToken ct)
    {
        var existing = await _store.GetMetadataAsync(secretId, ct) ?? throw new NotFoundException("Secret not found.");
        await _authorization.RequireProjectAccessAsync(existing.ProjectId, Permissions.SecretsManage, ct);
        await _store.DeleteAsync(secretId, ct);
        await _audit.RecordAsync("secret.metadata.deleted", "environment_secret", secretId.ToString(), existing.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new { secretId })), ct);
    }

    private static void ValidateName(string name)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add(new FieldError("name", "Name is required."));
        else
        {
            if (name.Trim().Length > 200) errors.Add(new FieldError("name", "Name must be at most 200 characters."));
            if (!Variables.VariableModel.IsValidKey(name.Trim()))
                errors.Add(new FieldError("name", "Name must match ^[A-Z0-9_]{1,64}$ (uppercase, digits, underscore)."));
        }
        ValidationException.ThrowIfInvalid(errors);
    }

    private static void ValidateValue(string value)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrEmpty(value)) errors.Add(new FieldError("value", "Value is required."));
        else if (value.Length > 8000) errors.Add(new FieldError("value", "Value must be at most 8000 characters."));
        ValidationException.ThrowIfInvalid(errors);
    }

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Meta(SecretMetadata meta)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            secretId = meta.Id,
            environmentId = meta.EnvironmentId,
            name = meta.Name,
            secretRef = meta.SecretReference,
            hasValue = meta.HasValue,
        }));
}
