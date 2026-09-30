using AutoTestAi.Application.Common;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Normalized source-agnostic execution command (Slice 3A §10). Never carries
/// plaintext secret values — only plain overrides and secret references.
/// </summary>
public sealed record ExecutionCommand(
    Guid ProjectId,
    Guid TestCaseVersionId,
    Guid EnvironmentId,
    Guid? SuiteId,
    string Browser,
    IReadOnlyDictionary<string, string> VariableOverrides,
    IReadOnlyDictionary<string, string> SecretRefOverrides,
    string? IdempotencyKey,
    TriggerType TriggerType);

public static class ExecutionCommandFactory
{
    /// <summary>
    /// Builds the normalized command from the API surface. Rejects raw-secret
    /// shapes: secretRef overrides must be opaque references.
    /// </summary>
    public static ExecutionCommand Create(
        Guid projectId,
        Guid testCaseVersionId,
        Guid environmentId,
        Guid? suiteId,
        string browser,
        IReadOnlyDictionary<string, string>? variableOverrides,
        IReadOnlyDictionary<string, string>? secretRefOverrides,
        string? idempotencyKey,
        TriggerType triggerType = TriggerType.Manual)
    {
        var errors = new List<FieldError>();
        ValidateOverrides(variableOverrides, "variableOverrides", errors, requireSecretRef: false);
        ValidateOverrides(secretRefOverrides, "secretRefOverrides", errors, requireSecretRef: true);
        ValidationException.ThrowIfInvalid(errors);
        return new ExecutionCommand(
            projectId, testCaseVersionId, environmentId, suiteId,
            string.IsNullOrWhiteSpace(browser) ? "chromium" : browser.Trim().ToLowerInvariant(),
            new Dictionary<string, string>(variableOverrides ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            new Dictionary<string, string>(secretRefOverrides ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
            triggerType);
    }

    private static void ValidateOverrides(
        IReadOnlyDictionary<string, string>? overrides,
        string field,
        List<FieldError> errors,
        bool requireSecretRef)
    {
        if (overrides is null) return;
        if (overrides.Count > 100)
        {
            errors.Add(new FieldError(field, "At most 100 overrides are allowed."));
            return;
        }
        foreach (var (key, value) in overrides)
        {
            if (!VariableModel.IsValidKey(key))
            {
                errors.Add(new FieldError($"{field}.{key}", "Key must match ^[A-Z0-9_]{1,64}$."));
                continue;
            }
            if (value is null || value.Length > VariableModel.MaxValueLength)
            {
                errors.Add(new FieldError($"{field}.{key}", $"Override must be a string of at most {VariableModel.MaxValueLength} characters."));
                continue;
            }
            if (requireSecretRef && !SecretReference.IsValid(value))
                errors.Add(new FieldError($"{field}.{key}", "Must be an opaque secret reference (env_secret:<id>). Raw secrets are forbidden."));
        }
    }
}
