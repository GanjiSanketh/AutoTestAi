namespace AutoTestAi.Infrastructure.Webhooks.Providers;

using AutoTestAi.Application.Webhooks;

/// <summary>Resolves the provider adapter for a route provider name.</summary>
public sealed class CiWebhookProviderResolver
{
    private readonly IReadOnlyDictionary<string, ICiWebhookProvider> _providers;

    public CiWebhookProviderResolver(IEnumerable<ICiWebhookProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, p => p, StringComparer.Ordinal);
    }

    public bool TryResolve(string provider, out ICiWebhookProvider? adapter)
        => _providers.TryGetValue((provider ?? string.Empty).Trim().ToLowerInvariant(), out adapter);
}
