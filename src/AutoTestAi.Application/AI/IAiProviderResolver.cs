namespace AutoTestAi.Application.AI;

/// <summary>
/// Safe provider metadata for the frontend status indicator (Slice 4 §28).
/// Contains no secrets, keys, or private endpoint URLs.
/// </summary>
public sealed record AiProviderStatus(
    string Provider,
    string? Model,
    bool Configured,
    string? Detail,
    string PromptVersion);

/// <summary>
/// Selects the configured <see cref="IAiProvider"/> (Slice 4 §6). Never silently
/// falls back to a different provider: unsupported names and missing required
/// configuration raise <see cref="AiProviderException"/>.
/// </summary>
public interface IAiProviderResolver
{
    IAiProvider Resolve();
    AiProviderStatus GetStatus();
}
