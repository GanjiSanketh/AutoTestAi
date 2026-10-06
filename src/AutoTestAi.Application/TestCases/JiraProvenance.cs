using System.Text.Json;
using AutoTestAi.Application.TestGeneration;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Safe narrow Jira provenance surfaced on TestCase versions
/// (Phase 4 Slice 6). Shaped server-side from stored historical
/// <c>GenerationRequest</c> JSONB; never the raw JSON.
/// </summary>
public sealed record JiraProvenanceDto(
    string Origin,
    string JiraIssueKey,
    string? JiraIssueType,
    string? JiraBaseUrlHost,
    string? JiraFetchedAt);

/// <summary>
/// Defensive read projection over historical generation metadata
/// (Phase 4 Slice 6 §§7–9). Stored <c>GenerationRequest</c> is treated as
/// untrusted historical JSON: absent, non-Jira, malformed, or keyless
/// payloads yield null — never an exception, never raw content.
/// </summary>
public static class JiraProvenanceReader
{
    public const string JiraImportOrigin = "jira-import";

    private const int MaxIssueTypeLength = 60;
    private const int MaxHostLength = 253;
    private const int MaxFetchedAtLength = 64;

    public static JiraProvenanceDto? TryRead(JsonDocument? generationRequest)
    {
        try
        {
            if (generationRequest is null)
                return null;
            var root = generationRequest.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!string.Equals(GetString(root, "origin"), JiraImportOrigin, StringComparison.Ordinal))
                return null;
            if (!JiraIssueKey.TryNormalize(GetString(root, "jiraIssueKey"), out var key))
                return null;
            return new JiraProvenanceDto(
                JiraImportOrigin,
                key,
                Clean(GetString(root, "jiraIssueType"), MaxIssueTypeLength),
                Clean(GetString(root, "jiraBaseUrlHost"), MaxHostLength),
                Clean(GetString(root, "jiraFetchedAt"), MaxFetchedAtLength));
        }
        catch (Exception ex) when (ex is JsonException
            or InvalidOperationException
            or ObjectDisposedException
            or ArgumentException)
        {
            // Historical metadata must never break reads.
            return null;
        }
    }

    /// <summary>Exact normalized-key match used by list filtering.</summary>
    public static bool Matches(JsonDocument? generationRequest, string normalizedKey)
        => TryRead(generationRequest)?.JiraIssueKey == normalizedKey;

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
