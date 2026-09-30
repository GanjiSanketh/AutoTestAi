using System.Text.Json;
using System.Text.RegularExpressions;
using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Variables;

/// <summary>
/// Variable entry validation, precedence merge, and ${{ KEY }} substitution (Slice 3A).
/// Single-pass, no eval, no recursion.
/// </summary>
public static partial class VariableModel
{
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 8000; // mirrors TestStep.MaxValueLength
    public const int MaxSecretRefLength = 256;
    public const int MaxEntries = 200;

    [GeneratedRegex("^[A-Z0-9_]{1,64}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\$\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    public const string Mask = "[REDACTED]";

    public static bool IsValidKey(string key)
        => !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && KeyPattern().IsMatch(key);

    /// <summary>
    /// Parses a VariablesJson object into (plain, secretRef) maps.
    /// Throws ValidationException on malformed entries or raw-secret shapes.
    /// </summary>
    public static (Dictionary<string, string> Plain, Dictionary<string, string> SecretRefs) ParseEntries(
        string? json, string field = "variables")
    {
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return (plain, refs);

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex)
        { throw new ValidationException("Variables must be a JSON object.", new[] { new FieldError(field, ex.Message) }); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ValidationException("Variables must be a JSON object.",
                    new[] { new FieldError(field, "Variables must be a JSON object.") });
            var errors = new List<FieldError>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name;
                if (!IsValidKey(key))
                {
                    errors.Add(new FieldError($"{field}.{key}", "Key must match ^[A-Z0-9_]{1,64}$ (uppercase, digits, underscore)."));
                    continue;
                }
                if (plain.Count + refs.Count >= MaxEntries)
                {
                    errors.Add(new FieldError(field, $"At most {MaxEntries} variables are allowed."));
                    break;
                }
                var entry = property.Value;
                if (entry.ValueKind == JsonValueKind.String)
                {
                    // Raw string shape is forbidden: it cannot express secret intent.
                    errors.Add(new FieldError($"{field}.{key}",
                        "Raw string values are not allowed. Use { \"value\": \"...\" } for plain values or { \"secretRef\": \"...\" } for secrets."));
                    continue;
                }
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new FieldError($"{field}.{key}", "Entry must be { \"value\": \"...\" } or { \"secretRef\": \"...\" }."));
                    continue;
                }
                var hasValue = entry.TryGetProperty("value", out var valueEl);
                var hasRef = entry.TryGetProperty("secretRef", out var refEl);
                if (hasValue == hasRef)
                {
                    errors.Add(new FieldError($"{field}.{key}", "Entry must contain exactly one of \"value\" or \"secretRef\"."));
                    continue;
                }
                if (hasValue)
                {
                    if (valueEl.ValueKind != JsonValueKind.String)
                    { errors.Add(new FieldError($"{field}.{key}", "\"value\" must be a string.")); continue; }
                    var value = valueEl.GetString() ?? string.Empty;
                    if (value.Length > MaxValueLength)
                    { errors.Add(new FieldError($"{field}.{key}", $"\"value\" must be at most {MaxValueLength} characters.")); continue; }
                    plain[key] = value;
                }
                else
                {
                    if (refEl.ValueKind != JsonValueKind.String)
                    { errors.Add(new FieldError($"{field}.{key}", "\"secretRef\" must be a string.")); continue; }
                    var secretRef = (refEl.GetString() ?? string.Empty).Trim();
                    if (secretRef.Length == 0 || secretRef.Length > MaxSecretRefLength)
                    { errors.Add(new FieldError($"{field}.{key}", $"\"secretRef\" must be 1..{MaxSecretRefLength} characters.")); continue; }
                    if (!Secrets.SecretReference.IsValid(secretRef))
                    { errors.Add(new FieldError($"{field}.{key}", "\"secretRef\" must be an opaque secret reference (env_secret:<id>). Raw secrets are forbidden.")); continue; }
                    refs[key] = secretRef;
                }
            }
            ValidationException.ThrowIfInvalid(errors);
        }
        return (plain, refs);
    }

    public static string SerializeEntries(
        IReadOnlyDictionary<string, string> plain,
        IReadOnlyDictionary<string, string> secretRefs)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in plain.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                writer.WriteStartObject();
                writer.WriteString("value", value);
                writer.WriteEndObject();
            }
            foreach (var (key, secretRef) in secretRefs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                writer.WriteStartObject();
                writer.WriteString("secretRef", secretRef);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Deterministic merge: System -&gt; Project -&gt; Environment -&gt; Suite -&gt; Override.
    /// Later scopes win per key. Secret-ref maps merge the same way.
    /// </summary>
    public static MergedVariables Merge(
        IReadOnlyDictionary<string, string>? system,
        IReadOnlyDictionary<string, string> project,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> suite,
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> projectRefs,
        IReadOnlyDictionary<string, string> environmentRefs,
        IReadOnlyDictionary<string, string> suiteRefs,
        IReadOnlyDictionary<string, string> overrideRefs)
    {
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        void ApplyPlain(IReadOnlyDictionary<string, string>? scope)
        {
            if (scope is null) return;
            foreach (var (k, v) in scope) plain[k] = v;
        }
        void ApplyRefs(IReadOnlyDictionary<string, string>? scope)
        {
            if (scope is null) return;
            foreach (var (k, v) in scope) refs[k] = v;
        }
        // A secretRef at a higher scope shadows a plain value below (and vice versa).
        ApplyPlain(system); ApplyPlain(project); ApplyPlain(environment); ApplyPlain(suite); ApplyPlain(overrides);
        ApplyRefs(projectRefs); ApplyRefs(environmentRefs); ApplyRefs(suiteRefs); ApplyRefs(overrideRefs);
        // Remove plain entries shadowed by a ref at any merged level.
        foreach (var key in refs.Keys)
            plain.Remove(key);
        return new MergedVariables(plain, refs);
    }

    /// <summary>
    /// Single-pass substitution of ${{ KEY }} using resolved values.
    /// Throws ConflictException listing the first missing key (deterministic).
    /// Tracks nothing; caller maps secret-derived keys separately.
    /// </summary>
    public static string Substitute(string? template, IReadOnlyDictionary<string, string> values, string field = "value")
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        string? missing = null;
        var result = PlaceholderPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (values.TryGetValue(key, out var resolved))
                return resolved;
            missing ??= key;
            return match.Value;
        });
        if (missing is not null)
            throw new ConflictException($"Variable '{missing}' is not defined for this environment. Define it in a variable set or execution override.");
        return result;
    }

    /// <summary>
    /// Substitution that also reports whether any resolved placeholder came
    /// from a secret-backed key. The trusted worker path uses this to decide:
    /// secret-derived content travels plaintext over the authenticated worker
    /// transport; literal password-like content keeps heuristic masking.
    /// </summary>
    public static (string Text, bool HadSecret) SubstituteSecretAware(
        string? template,
        IReadOnlyDictionary<string, string> values,
        IReadOnlySet<string> secretKeys)
    {
        if (string.IsNullOrEmpty(template)) return (template ?? string.Empty, false);
        var hadSecret = false;
        string? missing = null;
        var result = PlaceholderPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (values.TryGetValue(key, out var resolved))
            {
                if (secretKeys.Contains(key)) hadSecret = true;
                return resolved;
            }
            missing ??= key;
            return match.Value;
        });
        if (missing is not null)
            throw new ConflictException($"Variable '{missing}' is not defined for this environment. Define it in a variable set or execution override.");
        return (result, hadSecret);
    }

    /// <summary>Replaces exact secret-derived values with [REDACTED] (longest first).</summary>
    public static string? MaskSecrets(string? text, IEnumerable<string>? secretValues)
    {
        if (string.IsNullOrEmpty(text) || secretValues is null) return text;
        var ordered = secretValues
            .Where(v => !string.IsNullOrEmpty(v) && v.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToList();
        var result = text;
        foreach (var secret in ordered)
            result = result!.Replace(secret, Mask, StringComparison.Ordinal);
        return result;
    }
}

public sealed record MergedVariables(
    IReadOnlyDictionary<string, string> Plain,
    IReadOnlyDictionary<string, string> SecretRefs);

public sealed record VariableScopeDto(
    string ScopeType,
    Guid? ScopeId);

public sealed record VariableSetDto(
    Guid Id,
    Guid ProjectId,
    string ScopeType,
    Guid? ScopeId,
    string Name,
    string VariablesJson,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> SecretKeys,
    byte[]? RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class VariableScopeTypes
{
    public static bool TryParse(string? value, out VariableScopeType scope)
    {
        scope = VariableScopeType.Project;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Enum.TryParse(value.Trim(), ignoreCase: true, out scope)
            && Enum.IsDefined(typeof(VariableScopeType), scope);
    }
}
