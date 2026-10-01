using System.Text.Json;
using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Strongly validated CI/CD integration configuration (Phase 3 Slice 3B).
/// Stored as JSONB in Integration.Configuration; provider secrets are NEVER
/// serialized here — they live behind Integration.SecretReference as opaque
/// Slice 3A references (env_secret:&lt;guid&gt;).
/// </summary>
public sealed record CiIntegrationConfig(
    Guid? DefaultSuiteId,
    Guid? DefaultEnvironmentId,
    IReadOnlyList<string> EventAllowlist,
    IReadOnlyList<string> BranchAllowlist,
    IReadOnlyList<string> RepositoryAllowlist,
    IReadOnlyDictionary<string, string> VariableMapping,
    string? Username,
    IReadOnlyDictionary<string, string> SecretMapping)
{
    public const int MaxAllowlistEntries = 50;
    public const int MaxAllowlistEntryLength = 200;
    public const int MaxMappings = 20;

    /// <summary>Canonical empty configuration (integration created, not yet configured).</summary>
    public static CiIntegrationConfig Empty { get; } = new(
        null, null,
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static CiIntegrationConfig FromJson(JsonDocument? document)
    {
        if (document is null)
            return Empty;
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Empty;
            static Guid? GuidOrNull(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
                   Guid.TryParse(v.GetString(), out var g) && g != Guid.Empty ? g : null;
            static List<string> Strings(JsonElement e, string name)
            {
                var list = new List<string>();
                if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                    foreach (var item in v.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            list.Add(item.GetString()!.Trim());
                return list;
            }
            static Dictionary<string, string> Map(JsonElement e, string name)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
                    foreach (var prop in v.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                            map[prop.Name] = prop.Value.GetString()!;
                return map;
            }
            string? username = null;
            if (root.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(u.GetString()))
                username = u.GetString()!.Trim();
            return new CiIntegrationConfig(
                GuidOrNull(root, "defaultSuiteId"),
                GuidOrNull(root, "defaultEnvironmentId"),
                Strings(root, "eventAllowlist"),
                Strings(root, "branchAllowlist"),
                Strings(root, "repositoryAllowlist"),
                Map(root, "variableMapping"),
                username,
                Map(root, "secretMapping"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public string ToJson()
        => JsonSerializer.Serialize(new
        {
            defaultSuiteId = DefaultSuiteId,
            defaultEnvironmentId = DefaultEnvironmentId,
            eventAllowlist = EventAllowlist,
            branchAllowlist = BranchAllowlist,
            repositoryAllowlist = RepositoryAllowlist,
            variableMapping = VariableMapping,
            username = Username,
            secretMapping = SecretMapping,
        });

    /// <summary>Validates raw input. Provider-secret VALUES are never accepted here.</summary>
    public static void ValidateFields(
        Guid? defaultSuiteId,
        Guid? defaultEnvironmentId,
        IReadOnlyList<string>? eventAllowlist,
        IReadOnlyList<string>? branchAllowlist,
        IReadOnlyList<string>? repositoryAllowlist,
        IReadOnlyDictionary<string, string>? variableMapping,
        string? username,
        IReadOnlyDictionary<string, string>? secretMapping,
        List<FieldError> errors)
    {
        if (defaultSuiteId.HasValue && defaultSuiteId.Value == Guid.Empty)
            errors.Add(new FieldError("defaultSuiteId", "Suite id must be a non-empty GUID."));
        if (defaultEnvironmentId.HasValue && defaultEnvironmentId.Value == Guid.Empty)
            errors.Add(new FieldError("defaultEnvironmentId", "Environment id must be a non-empty GUID."));
        ValidateAllowlist(eventAllowlist, "eventAllowlist", errors);
        ValidateAllowlist(branchAllowlist, "branchAllowlist", errors);
        ValidateAllowlist(repositoryAllowlist, "repositoryAllowlist", errors);
        if (variableMapping is not null)
        {
            if (variableMapping.Count > MaxMappings)
                errors.Add(new FieldError("variableMapping", $"At most {MaxMappings} variable mappings are allowed."));
            else foreach (var (target, source) in variableMapping)
            {
                if (!Variables.VariableModel.IsValidKey(target))
                    errors.Add(new FieldError($"variableMapping.{target}", "Target key must match ^[A-Z0-9_]{1,64}$."));
                if (string.IsNullOrWhiteSpace(source) || !CiVariableMapping.IsSupportedSource(source))
                    errors.Add(new FieldError($"variableMapping.{target}", $"Source must be one of: {string.Join(", ", CiVariableMapping.SupportedSources)}."));
            }
        }
        if (!string.IsNullOrWhiteSpace(username) && username!.Trim().Length > 200)
            errors.Add(new FieldError("username", "Username must be at most 200 characters."));
        if (secretMapping is not null)
        {
            if (secretMapping.Count > MaxMappings)
                errors.Add(new FieldError("secretMapping", $"At most {MaxMappings} secret mappings are allowed."));
            else foreach (var (target, reference) in secretMapping)
            {
                if (!Variables.VariableModel.IsValidKey(target))
                    errors.Add(new FieldError($"secretMapping.{target}", "Target key must match ^[A-Z0-9_]{1,64}$."));
                if (!Secrets.SecretReference.IsValid(reference))
                    errors.Add(new FieldError($"secretMapping.{target}", "Must be an opaque secret reference (env_secret:<id>). Raw secrets are forbidden."));
            }
        }
    }

    private static void ValidateAllowlist(IReadOnlyList<string>? values, string field, List<FieldError> errors)
    {
        if (values is null) return;
        if (values.Count > MaxAllowlistEntries)
        {
            errors.Add(new FieldError(field, $"At most {MaxAllowlistEntries} entries are allowed."));
            return;
        }
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > MaxAllowlistEntryLength)
                errors.Add(new FieldError(field, $"Entries must be 1..{MaxAllowlistEntryLength} characters."));
        }
    }
}
