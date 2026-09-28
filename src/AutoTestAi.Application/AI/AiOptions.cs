namespace AutoTestAi.Application.AI;

/// <summary>
/// Strongly typed server-side AI configuration (Slice 4 §10, ASP.NET Core Options
/// pattern, section "AI"). Secrets here are server-side only and must never be
/// returned to the frontend or logged.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary>Active provider key: stub | ollama | openai | gemini (planned).</summary>
    public string Provider { get; set; } = "stub";

    /// <summary>Legacy alias for <see cref="Provider"/> (appsettings Phase 0 shape).</summary>
    public string? DefaultProvider { get; set; }

    public string? Model { get; set; }

    /// <summary>Ollama base URL (and optional OpenAI-compatible endpoint override).</summary>
    public string? BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Provider API key (OpenAI/Gemini). Never exposed or logged.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public double? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }

    /// <summary>Basic application-level guard: max generations per project per minute.</summary>
    public int MaxGenerationsPerMinutePerProject { get; set; } = 20;

    /// <summary>Effective provider name: Provider wins, DefaultProvider is the fallback.</summary>
    public string EffectiveProvider
        => string.IsNullOrWhiteSpace(Provider) ? (DefaultProvider ?? "stub") : Provider;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 5, 600));
}
