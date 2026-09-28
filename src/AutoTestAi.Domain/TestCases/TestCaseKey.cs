using System.Text.RegularExpressions;

namespace AutoTestAi.Domain.TestCases;

/// <summary>
/// Test case key rules (docs/05: unique <c>(project_id, test_key)</c>).
/// Keys are project-scoped (the same key may exist in different projects),
/// immutable after creation, and normalized to upper-case. Format: starts
/// with a letter, then letters, digits, '_' or '-', 2–32 characters total.
/// </summary>
public static partial class TestCaseKey
{
    [GeneratedRegex("^[A-Z][A-Z0-9_-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyFormat();

    public const int MaxLength = 32;

    public static string Normalize(string? key)
        => (key ?? string.Empty).Trim().ToUpperInvariant();

    public static bool IsValidFormat(string? key)
        => key is not null && KeyFormat().IsMatch(key);
}
