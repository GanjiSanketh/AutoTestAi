using System.Text.Json;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Variables;

/// <summary>
/// Trusted variable resolution (Slice 3A §5/§6/§11). Loads Project→Environment→Suite
/// scopes plus the persisted execution envelope, merges deterministically, resolves
/// secret references via <see cref="ISecretResolver"/>, and substitutes ${{ KEY }}.
/// Secret values exist only in the returned <see cref="ResolvedVariables"/>.
/// </summary>
public interface IVariableResolutionService
{
    Task<ResolvedVariables> ResolveForExecutionAsync(
        Guid projectId, Guid environmentId, Guid? suiteId, Guid executionId, CancellationToken ct);
}

public sealed record ResolvedVariables(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> SecretValues,
    IReadOnlyList<string> MissingKeys,
    IReadOnlySet<string> SecretKeys);

public sealed class VariableResolutionService : IVariableResolutionService
{
    public static readonly IReadOnlyDictionary<string, string> SystemDefaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BROWSER"] = "chromium",
        };

    private readonly IVariableSetStore _sets;
    private readonly IExecutionVariablesStore _envelopes;
    private readonly ISecretResolver _secrets;

    public VariableResolutionService(
        IVariableSetStore sets,
        IExecutionVariablesStore envelopes,
        ISecretResolver secrets)
    {
        _sets = sets;
        _envelopes = envelopes;
        _secrets = secrets;
    }

    public async Task<ResolvedVariables> ResolveForExecutionAsync(
        Guid projectId, Guid environmentId, Guid? suiteId, Guid executionId, CancellationToken ct)
    {
        var sets = await _sets.ListByProjectAsync(projectId, ct);
        VariableSet? Project() => sets.FirstOrDefault(s =>
            s.ScopeType == Domain.Enums.VariableScopeType.Project);
        VariableSet? Env() => sets.FirstOrDefault(s =>
            s.ScopeType == Domain.Enums.VariableScopeType.Environment && s.ScopeId == environmentId);
        VariableSet? Suite() => suiteId.HasValue
            ? sets.FirstOrDefault(s =>
                s.ScopeType == Domain.Enums.VariableScopeType.Suite && s.ScopeId == suiteId.Value)
            : null;

        var (projectPlain, projectRefs) = VariableModel.ParseEntries(Project()?.VariablesJson);
        var (envPlain, envRefs) = VariableModel.ParseEntries(Env()?.VariablesJson);
        var (suitePlain, suiteRefs) = VariableModel.ParseEntries(Suite()?.VariablesJson);

        var envelope = await _envelopes.GetByExecutionAsync(executionId, ct);
        var overridePlain = new Dictionary<string, string>(StringComparer.Ordinal);
        var overrideRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (envelope is not null)
        {
            overridePlain = ParseFlat(envelope.VariableOverridesJson, "variableOverrides");
            overrideRefs = ParseFlat(envelope.SecretRefOverridesJson, "secretRefOverrides");
            foreach (var (_, r) in overrideRefs)
                if (!SecretReference.IsValid(r))
                    throw new ValidationException("Invalid secret reference override.",
                        new[] { new FieldError("secretRefOverrides", "Each override must be an opaque secret reference (env_secret:<id>).") });
        }

        var merged = VariableModel.Merge(
            SystemDefaults, projectPlain, envPlain, suitePlain, overridePlain,
            projectRefs, envRefs, suiteRefs, overrideRefs);

        var values = new Dictionary<string, string>(merged.Plain, StringComparer.Ordinal);
        var secretValues = new List<string>();
        foreach (var (key, secretRef) in merged.SecretRefs)
        {
            string value;
            try { value = await _secrets.ResolveAsync(secretRef, ct); }
            catch (NotFoundException ex)
            { throw new ConflictException($"Secret for variable '{key}' is not available: {ex.Message}"); }
            values[key] = value;
            if (!string.IsNullOrEmpty(value))
                secretValues.Add(value);
        }
        return new ResolvedVariables(values, secretValues, Array.Empty<string>(),
            new HashSet<string>(merged.SecretRefs.Keys, StringComparer.Ordinal));
    }

    private static Dictionary<string, string> ParseFlat(string? json, string field)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return result;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ValidationException($"{field} must be a JSON object.",
                new[] { new FieldError(field, "Must be a JSON object.") });
        var errors = new List<FieldError>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!VariableModel.IsValidKey(property.Name))
            { errors.Add(new FieldError($"{field}.{property.Name}", "Key must match ^[A-Z0-9_]{1,64}$.")); continue; }
            if (property.Value.ValueKind != JsonValueKind.String)
            { errors.Add(new FieldError($"{field}.{property.Name}", "Override value must be a string.")); continue; }
            var value = property.Value.GetString() ?? string.Empty;
            if (value.Length > VariableModel.MaxValueLength)
            { errors.Add(new FieldError($"{field}.{property.Name}", $"Must be at most {VariableModel.MaxValueLength} characters.")); continue; }
            result[property.Name] = value;
        }
        ValidationException.ThrowIfInvalid(errors);
        return result;
    }
}
