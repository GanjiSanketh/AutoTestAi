namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// CI/CD provider identifiers for Slice 3B (Phase 3). Free strings by
/// repository convention (matching Integration.Provider); validated at the
/// application boundary. Never an enum.
/// </summary>
public static class CiProviderNames
{
    public const string GitHub = "github";
    public const string GitLab = "gitlab";
    public const string Jenkins = "jenkins";
    public const string Azure = "azure";

    public const string IntegrationType = "cicd";

    public static readonly IReadOnlyList<string> All = new[]
    {
        GitHub, GitLab, Jenkins, Azure,
    };

    public static bool IsSupported(string? provider)
        => !string.IsNullOrWhiteSpace(provider) &&
           All.Contains(provider.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    public static string Normalize(string provider)
        => provider.Trim().ToLowerInvariant();
}
