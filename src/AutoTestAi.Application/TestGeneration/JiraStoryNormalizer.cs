using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>Why ADF normalization failed (all map to safe, content-free errors).</summary>
public enum JiraNormalizationFailure
{
    Malformed,
    Oversized,
    Empty,
}

/// <summary>Pure normalization failure. Never carries Jira content.</summary>
public sealed class JiraStoryNormalizationException : Exception
{
    public JiraNormalizationFailure Reason { get; }

    public JiraStoryNormalizationException(JiraNormalizationFailure reason, string message)
        : base(message) => Reason = reason;

    public JiraStoryNormalizationException(JiraNormalizationFailure reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;
}

/// <summary>Normalized story content derived from a Jira issue (all redacted).</summary>
public sealed record JiraNormalizedStory(
    string StoryTitle,
    string? StoryDescription,
    IReadOnlyList<string> AcceptanceCriteria,
    string IssueType);

/// <summary>
/// Pure Application-level Jira story normalizer (Phase 4 Slice 5 §§7–12).
/// No HTTP, no database, no configuration — deterministic and unit-testable.
///
/// Only these ADF node types are supported: doc, paragraph, heading,
/// bulletList, orderedList, listItem, text, hardBreak. Marks are formatting
/// metadata and are never interpreted. Unsupported nodes (media, mediaGroup,
/// inlineCard, codeBlock, table, mention, emoji, expand, panel, unknown)
/// are dropped with their subtrees. Nothing represented by a URL, link,
/// card, attachment, or remote node is ever fetched.
/// </summary>
public static partial class JiraStoryNormalizer
{
    /// <summary>
    /// Fixed version of the Jira story normalization contract
    /// (Phase 4 Slice 7 §15). Stamped into new Jira-generation provenance
    /// as future compatibility metadata; never a reason to reject legacy
    /// checks, which run against the current known contract best-effort.
    /// </summary>
    public const string NormalizerVersion = "jira-story-normalizer-v1";

    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 4000;
    public const int MaxCriteria = 50;
    public const int MaxCriterionLength = 2000;
    public const int MaxIssueTypeLength = 60;

    private const int MaxNodes = 2000;
    private const int MaxDepth = 20;
    private const int MaxAccumulatedChars = 20000;
    private const string TruncationMarker = "… [truncated]";

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiNewline();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex AnyWhitespace();

    /// <summary>
    /// Normalizes a Jira summary + ADF description into story content.
    /// Throws <see cref="JiraStoryNormalizationException"/> on
    /// malformed/oversized/empty content (never leaking Jira text).
    /// </summary>
    public static JiraNormalizedStory Normalize(
        string? summary,
        string? descriptionAdfJson,
        string? issueType)
    {
        var type = issueType?.Trim() ?? string.Empty;
        if (type.Length == 0)
            throw new JiraStoryNormalizationException(JiraNormalizationFailure.Malformed,
                "The Jira issue does not contain usable type metadata.");
        if (type.Length > MaxIssueTypeLength)
            type = type[..MaxIssueTypeLength];

        var title = CollapseInline(summary ?? string.Empty);
        if (title.Length > MaxTitleLength)
            title = title[..(MaxTitleLength - 1)] + "…";
        title = SensitiveDataRedactor.Redact(title);
        if (string.IsNullOrWhiteSpace(title))
            throw new JiraStoryNormalizationException(JiraNormalizationFailure.Empty,
                "The Jira issue does not contain a usable summary.");

        var blocks = new List<string>();
        var criteria = new List<string>();
        if (!string.IsNullOrWhiteSpace(descriptionAdfJson))
        {
            var (descriptionBlocks, listCriteria) = FlattenAdf(descriptionAdfJson!);
            blocks.AddRange(descriptionBlocks);
            criteria.AddRange(listCriteria);
        }

        string? description = null;
        if (blocks.Count > 0)
        {
            var joined = MultiNewline().Replace(string.Join("\n\n", blocks), "\n\n").Trim();
            if (joined.Length > 0)
                description = TruncateWithMarker(SensitiveDataRedactor.Redact(joined), MaxDescriptionLength);
        }

        var normalizedCriteria = new List<string>();
        foreach (var criterion in criteria)
        {
            var collapsed = CollapseInline(criterion);
            if (collapsed.Length == 0)
                continue;
            normalizedCriteria.Add(TruncateWithMarker(SensitiveDataRedactor.Redact(collapsed), MaxCriterionLength));
            if (normalizedCriteria.Count >= MaxCriteria)
                break;
        }

        if (normalizedCriteria.Count == 0 && !string.IsNullOrWhiteSpace(description))
        {
            // No usable list items: the description itself is the criterion.
            normalizedCriteria.Add(TruncateWithMarker(description!, MaxCriterionLength));
        }

        if (normalizedCriteria.Count == 0)
            throw new JiraStoryNormalizationException(JiraNormalizationFailure.Empty,
                "The Jira issue does not contain usable story content.");

        return new JiraNormalizedStory(title, description, normalizedCriteria, type);
    }

    private static string CollapseInline(string value)
    {
        var collapsed = AnyWhitespace().Replace(value ?? string.Empty, " ").Trim();
        return MultiSpace().Replace(collapsed, " ").Trim();
    }

    private static string TruncateWithMarker(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        return value[..(maxLength - TruncationMarker.Length)] + TruncationMarker;
    }

    private sealed record AdfOutput(List<string> Blocks, List<string> Criteria);

    /// <summary>
    /// Iterative ADF traversal (explicit stack — no unbounded recursion).
    /// Returns description blocks plus criteria extracted from top-level
    /// list items (nested lists are flattened into their parent item).
    /// </summary>
    private static (List<string> Blocks, List<string> Criteria) FlattenAdf(string adfJson)
    {
        JsonDocument document;
        try
        {
            // Parse depth stays bounded (128): deeper documents fail here as
            // malformed, while the traversal guard below (depth 20) fires
            // first for anything that parses — defense in depth.
            document = JsonDocument.Parse(adfJson, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException ex)
        {
            throw new JiraStoryNormalizationException(JiraNormalizationFailure.Malformed,
                "The Jira issue description could not be understood.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !IsType(root, "doc") ||
                !root.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                throw new JiraStoryNormalizationException(JiraNormalizationFailure.Malformed,
                    "The Jira issue description could not be understood.");
            }

            var output = new AdfOutput(new List<string>(), new List<string>());
            var state = new TraversalState();

            foreach (var block in content.EnumerateArray())
            {
                state.Checkpoint(block);
                ProcessBlock(block, 0, state, output);
            }

            return (output.Blocks, output.Criteria);
        }
    }

    private static void ProcessBlock(JsonElement block, int depth, TraversalState state, AdfOutput output)
    {
        state.Enter(depth, block);
        var kind = GetType(block);
        switch (kind)
        {
            case "paragraph":
            case "heading":
            {
                var text = FlattenInline(block, depth + 1, state);
                text = CollapseBlock(text);
                if (text.Length > 0)
                    output.Blocks.Add(text);
                break;
            }
            case "bulletList":
            case "orderedList":
            {
                if (!block.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array)
                    break;
                var listLines = new List<string>();
                foreach (var item in items.EnumerateArray())
                {
                    state.Checkpoint(item);
                    if (item.ValueKind != JsonValueKind.Object || !IsType(item, "listItem"))
                        continue;
                    var itemText = FlattenListItem(item, depth + 1, state);
                    itemText = CollapseBlock(itemText);
                    if (itemText.Length == 0)
                        continue;
                    output.Criteria.Add(CollapseInline(itemText));
                    listLines.Add(itemText);
                }
                if (listLines.Count > 0)
                    output.Blocks.Add(string.Join("\n", listLines));
                break;
            }
            default:
                // Unsupported block (media, codeBlock, table, panel, expand,
                // unknown): dropped with its subtree. Never descended into.
                break;
        }
    }

    /// <summary>Flattens a listItem subtree (inline text + nested lists) to text.</summary>
    private static string FlattenListItem(JsonElement listItem, int depth, TraversalState state)
    {
        state.Enter(depth, listItem);
        if (!listItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var parts = new List<string>();
        foreach (var child in content.EnumerateArray())
        {
            state.Checkpoint(child);
            if (child.ValueKind != JsonValueKind.Object)
                continue;
            var kind = GetType(child);
            if (kind is "paragraph" or "heading")
            {
                var text = FlattenInline(child, depth + 1, state);
                if (text.Length > 0)
                    parts.Add(text);
            }
            else if (kind is "bulletList" or "orderedList")
            {
                if (!child.TryGetProperty("content", out var nested) || nested.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var nestedItem in nested.EnumerateArray())
                {
                    state.Checkpoint(nestedItem);
                    if (nestedItem.ValueKind != JsonValueKind.Object || !IsType(nestedItem, "listItem"))
                        continue;
                    var nestedText = FlattenListItem(nestedItem, depth + 1, state);
                    if (nestedText.Length > 0)
                        parts.Add(nestedText);
                }
            }
            // Other node kinds inside list items are dropped.
        }
        return string.Join(" ", parts);
    }

    /// <summary>Flattens paragraph/heading inline content (text + hardBreak).</summary>
    private static string FlattenInline(JsonElement container, int depth, TraversalState state)
    {
        state.Enter(depth, container);
        if (!container.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var builder = new StringBuilder();
        foreach (var node in content.EnumerateArray())
        {
            state.Checkpoint(node);
            if (node.ValueKind != JsonValueKind.Object)
                continue;
            var kind = GetType(node);
            if (kind == "text")
            {
                var text = node.TryGetProperty("text", out var textEl) &&
                    textEl.ValueKind == JsonValueKind.String ? textEl.GetString() ?? string.Empty : string.Empty;
                state.AddChars(text.Length);
                builder.Append(text);
                // Marks are formatting metadata — never interpreted.
            }
            else if (kind == "hardBreak")
            {
                builder.Append('\n');
                state.AddChars(1);
            }
            // All other inline nodes (mention, emoji, inlineCard, unknown) are dropped.
        }
        return builder.ToString();
    }

    private static string CollapseBlock(string value)
    {
        var collapsed = value.Replace('\r', '\n');
        collapsed = MultiNewline().Replace(collapsed, "\n\n");
        var lines = collapsed.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = MultiSpace().Replace(lines[i].Replace('\t', ' ').Trim(), " ").Trim();
        return string.Join("\n", lines).Trim();
    }

    private static string GetType(JsonElement element)
        => element.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString() ?? string.Empty : string.Empty;

    private static bool IsType(JsonElement element, string type)
        => string.Equals(GetType(element), type, StringComparison.Ordinal);

    /// <summary>Traversal budget guard: node count, nesting depth, text size.</summary>
    private sealed class TraversalState
    {
        private int _nodes;
        private long _chars;

        public void Checkpoint(JsonElement element)
        {
            _nodes++;
            if (_nodes > MaxNodes)
                throw new JiraStoryNormalizationException(JiraNormalizationFailure.Oversized,
                    "The Jira issue description is too large to import safely.");
            _ = element;
        }

        public void Enter(int depth, JsonElement element)
        {
            _nodes++;
            if (_nodes > MaxNodes)
                throw new JiraStoryNormalizationException(JiraNormalizationFailure.Oversized,
                    "The Jira issue description is too large to import safely.");
            if (depth > MaxDepth)
                throw new JiraStoryNormalizationException(JiraNormalizationFailure.Oversized,
                    "The Jira issue description is too deeply nested to import safely.");
            _ = element;
        }

        public void AddChars(long count)
        {
            _chars += count;
            if (_chars > MaxAccumulatedChars)
                throw new JiraStoryNormalizationException(JiraNormalizationFailure.Oversized,
                    "The Jira issue description is too large to import safely.");
        }
    }
}
