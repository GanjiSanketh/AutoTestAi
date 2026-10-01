namespace AutoTestAi.Application.Webhooks;

/// <summary>Slice 3B webhook-specific failures with deterministic HTTP mappings.</summary>
public sealed class WebhookTooLargeException : Exception
{
    public WebhookTooLargeException(string message) : base(message) { }
}

/// <summary>Process-local per-project webhook rate budget exhausted. Maps to 429.</summary>
public sealed class WebhookRateLimitedException : Exception
{
    public WebhookRateLimitedException(string message) : base(message) { }
}
