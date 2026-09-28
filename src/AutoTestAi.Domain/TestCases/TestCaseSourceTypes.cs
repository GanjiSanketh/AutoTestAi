namespace AutoTestAi.Domain.TestCases;

/// <summary>Origin of a test case version's content. Stored lower-case.</summary>
public static class TestCaseSourceTypes
{
    public const string Manual = "manual";
    public const string Ai = "ai";
    public const string Imported = "imported";

    public static bool IsSupported(string? value)
        => string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Ai, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Imported, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value)
        => (value ?? Manual).Trim().ToLowerInvariant();
}
