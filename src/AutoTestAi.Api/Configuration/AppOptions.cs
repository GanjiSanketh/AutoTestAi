namespace AutoTestAi.Api.Configuration;

/// <summary>
/// Environment-driven configuration sections (.env.example documents every value).
/// Server-side secrets must never be exposed through VITE_* variables.
/// </summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public bool Configured => !string.IsNullOrWhiteSpace(Authority);
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = ["http://localhost:5173"];
}

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";
    public string ServiceName { get; set; } = "autotestai-api";
    public string? OtlpEndpoint { get; set; }
}
