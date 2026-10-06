using System.Text.Json;
using AutoTestAi.Application.TestGeneration;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Normalized Jira story snapshot used for freshness comparison
/// (Phase 4 Slice 7 §13). Either side may come from stored historical
/// provenance or from a freshly normalized Jira issue; both sides share
/// the same normalized, redacted, bounded contract.
/// </summary>
public sealed record JiraStorySnapshot(
    string Title,
    string? Description,
    IReadOnlyList<string> AcceptanceCriteria,
    string IssueType);

/// <summary>
/// Pure deterministic snapshot comparison (Phase 4 Slice 7 §13).
/// Compares exactly the approved content fields; identity and generation
/// metadata (issue key, host, timestamps, ids, indexes, versions) are
/// never content. Criteria order is significant.
/// </summary>
public static class JiraStorySnapshotComparer
{
    public const string StatusCurrent = "current";
    public const string StatusChanged = "changed";

    public const string FieldTitle = "title";
    public const string FieldDescription = "description";
    public const string FieldAcceptanceCriteria = "acceptanceCriteria";
    public const string FieldIssueType = "issueType";

    /// <summary>
    /// Builds a snapshot from stored historical provenance. Returns null
    /// when the document is not usable Jira provenance (absent, non-Jira
    /// origin, missing/invalid key). Never throws.
    /// </summary>
    public static JiraStorySnapshot? TryReadStored(JsonDocument? generationRequest)
    {
        try
        {
            if (generationRequest is null)
                return null;
            var root = generationRequest.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!string.Equals(GetString(root, "origin"), JiraProvenanceReader.JiraImportOrigin, StringComparison.Ordinal))
                return null;
            if (!JiraIssueKey.TryNormalize(GetString(root, "jiraIssueKey"), out _))
                return null;
            return new JiraStorySnapshot(
                GetString(root, "storyTitle") ?? string.Empty,
                GetString(root, "storyDescription"),
                GetCriteria(root),
                GetString(root, "jiraIssueType") ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException
            or InvalidOperationException
            or ObjectDisposedException
            or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a snapshot from freshly normalized Jira content. The issue
    /// type is redacted here because stored snapshots carry the redacted
    /// form (redaction is deterministic, so equal inputs stay equal).
    /// </summary>
    public static JiraStorySnapshot FromNormalized(JiraNormalizedStory normalized)
        => new(
            normalized.StoryTitle,
            normalized.StoryDescription,
            normalized.AcceptanceCriteria,
            SensitiveDataRedactor.Redact(normalized.IssueType));

    public static (string Status, IReadOnlyList<string> ChangedFields) Compare(
        JiraStorySnapshot stored, JiraStorySnapshot fresh)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(fresh);

        var changed = new List<string>(4);
        if (!string.Equals(stored.Title, fresh.Title, StringComparison.Ordinal))
            changed.Add(FieldTitle);
        if (!string.Equals(stored.Description ?? string.Empty, fresh.Description ?? string.Empty, StringComparison.Ordinal))
            changed.Add(FieldDescription);
        if (!stored.AcceptanceCriteria.SequenceEqual(fresh.AcceptanceCriteria, StringComparer.Ordinal))
            changed.Add(FieldAcceptanceCriteria);
        if (!string.Equals(stored.IssueType, fresh.IssueType, StringComparison.Ordinal))
            changed.Add(FieldIssueType);
        return changed.Count == 0
            ? (StatusCurrent, Array.Empty<string>())
            : (StatusChanged, changed);
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static IReadOnlyList<string> GetCriteria(JsonElement root)
    {
        if (!root.TryGetProperty("acceptanceCriteria", out var element) ||
            element.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var criteria = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                criteria.Add(item.GetString() ?? string.Empty);
        }
        return criteria;
    }
}
