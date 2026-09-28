using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.AI;

/// <summary>
/// Configuration-driven provider resolution (Slice 4 §6). Business logic depends
/// only on <see cref="IAiProvider"/>; this resolver is the single place that maps
/// configuration to an adapter instance.
/// </summary>
public sealed class AiProviderResolver : IAiProviderResolver
{
    public static readonly IReadOnlyList<string> SupportedProviders =
        ["stub", "ollama", "openai"];

    private readonly IEnumerable<IAiProvider> _providers;
    private readonly IOptions<AiOptions> _options;

    public AiProviderResolver(IEnumerable<IAiProvider> providers, IOptions<AiOptions> options)
    {
        _providers = providers;
        _options = options;
    }

    public IAiProvider Resolve()
    {
        var settings = _options.Value;
        var name = (settings.EffectiveProvider ?? "stub").Trim().ToLowerInvariant();

        if (string.Equals(name, "gemini", StringComparison.Ordinal))
            throw AiProviderException.Unsupported("gemini",
                "AI provider 'gemini' is not implemented in this release. " +
                "Configure AI:Provider to one of: stub, ollama, openai.");

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw AiProviderException.Unsupported(name,
                $"Unsupported AI provider '{name}'. " +
                $"Supported providers: {string.Join(", ", SupportedProviders)}.");

        if (string.Equals(name, "openai", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(settings.ApiKey))
            throw AiProviderException.NotConfigured("openai",
                "AI provider 'openai' is selected but AI:ApiKey is not configured. " +
                "Set the server-side API key; it is never exposed to clients.");

        return provider;
    }

    public AiProviderStatus GetStatus()
    {
        var settings = _options.Value;
        var name = (settings.EffectiveProvider ?? "stub").Trim().ToLowerInvariant();
        var known = _providers.Any(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (!known && !string.Equals(name, "gemini", StringComparison.Ordinal))
            return new AiProviderStatus(name, settings.Model, false,
                $"Unsupported provider '{name}'.", AiPromptVersions.TestGenerationV1);

        var configured = name switch
        {
            "openai" => !string.IsNullOrWhiteSpace(settings.ApiKey),
            "gemini" => false,
            // stub is always configured; ollama reachability is only known at call time.
            _ => true,
        };
        var detail = name switch
        {
            "gemini" => "Gemini adapter is planned but not implemented in this release.",
            "openai" when !configured => "AI:ApiKey is not configured.",
            _ => null,
        };
        return new AiProviderStatus(name, settings.Model, configured, detail, AiPromptVersions.TestGenerationV1);
    }
}
