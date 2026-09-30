namespace AutoTestAi.Infrastructure.Secrets;

/// <summary>Key material configuration for the encrypted secret vault (Slice 3A §8).</summary>
public sealed class SecretVaultOptions
{
    public const string SectionName = "Secrets";

    /// <summary>Base64 AES-256 key (32 bytes). Required in production.</summary>
    public string? KekBase64 { get; set; }

    /// <summary>Active key version written on new encryptions.</summary>
    public string KeyVersion { get; set; } = "v1";
}
