namespace AutoTestAi.Application.AI;

/// <summary>Machine-readable provider failure kinds for error mapping (Slice 4 §20).</summary>
public enum AiProviderErrorKind
{
    /// <summary>Provider selected but required configuration is missing (e.g. API key).</summary>
    NotConfigured,
    /// <summary>Configured provider name is unknown or not implemented (e.g. gemini).</summary>
    UnsupportedProvider,
    /// <summary>Provider reachable but errored, or unreachable (network/refused).</summary>
    Unavailable,
    /// <summary>Provider call exceeded the configured timeout.</summary>
    Timeout,
    /// <summary>Provider returned HTTP 429.</summary>
    RateLimited,
    /// <summary>Provider returned malformed/unparseable output.</summary>
    MalformedResponse,
}

/// <summary>
/// Controlled generation failure. Carries no secrets, no vendor stack traces —
/// safe to map into the API ProblemDetails envelope.
/// </summary>
public sealed class AiProviderException : Exception
{
    public AiProviderErrorKind Kind { get; }
    public string? Provider { get; }

    public AiProviderException(AiProviderErrorKind kind, string message, string? provider = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Provider = provider;
    }

    public static AiProviderException NotConfigured(string provider, string message)
        => new(AiProviderErrorKind.NotConfigured, message, provider);

    public static AiProviderException Unsupported(string provider, string message)
        => new(AiProviderErrorKind.UnsupportedProvider, message, provider);

    public static AiProviderException Unavailable(string provider, string message, Exception? inner = null)
        => new(AiProviderErrorKind.Unavailable, message, provider, inner);

    public static AiProviderException Timeout(string provider, string message, Exception? inner = null)
        => new(AiProviderErrorKind.Timeout, message, provider, inner);

    public static AiProviderException RateLimited(string provider, string message)
        => new(AiProviderErrorKind.RateLimited, message, provider);

    public static AiProviderException Malformed(string provider, string message, Exception? inner = null)
        => new(AiProviderErrorKind.MalformedResponse, message, provider, inner);
}
