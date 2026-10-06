using System.Text.RegularExpressions;
using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Jira issue-key validation (Phase 4 Slice 5 §4). Server is authoritative;
/// client-side hints are UX only. The client supplies only the issue key —
/// never a Jira URL, host, project, integration id, or credentials.
/// </summary>
public static class JiraIssueKey
{
    public const int MaxLength = 30;

    private const string Pattern = "^[A-Z][A-Z0-9]+-[0-9]+$";

    /// <summary>
    /// Trims and uppercases the input, then validates shape and length.
    /// Returns false (with a user-safe reason) instead of throwing.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
            return false;
        var upper = trimmed.ToUpperInvariant();
        if (!Regex.IsMatch(upper, Pattern, RegexOptions.CultureInvariant))
            return false;
        normalized = upper;
        return true;
    }

    /// <summary>Validates or throws a 400-mapped <see cref="ValidationException"/>.</summary>
    public static string NormalizeOrThrow(string? input)
    {
        if (!TryNormalize(input, out var normalized))
            throw new ValidationException(
                "The Jira issue key is invalid.",
                new[] { new FieldError("issueKey", "Issue key must look like PROJ-123 (letters, digits, one hyphen) and be at most 30 characters.") });
        return normalized;
    }
}
