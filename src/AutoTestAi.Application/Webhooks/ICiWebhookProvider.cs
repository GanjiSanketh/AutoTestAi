namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Minimal header accessor so provider adapters stay testable without
/// ASP.NET Core dependencies. Values are raw header strings; adapters own
/// all parsing and must treat them as untrusted input.
/// </summary>
public interface ICiWebhookHeaders
{
    string? Get(string name);
}

/// <summary>
/// Provider-specific webhook adapter (Phase 3 Slice 3B). Each provider owns
/// its authentication model, delivery/event extraction, and normalization.
/// There is deliberately NO universal verifier: GitHub uses HMAC-SHA256,
/// GitLab uses a plaintext token, Jenkins uses a configured bearer token,
/// Azure uses Basic credentials.
/// </summary>
public interface ICiWebhookProvider
{
    string ProviderName { get; }

    /// <summary>
    /// Verifies the request using the resolved secret (in-memory only, never
    /// persisted or logged) and the trusted integration configuration.
    /// Returns false when authentication fails (caller maps to 401/404).
    /// </summary>
    Task<bool> AuthenticateAsync(
        byte[] rawBody,
        ICiWebhookHeaders headers,
        string? secret,
        CiIntegrationConfig config,
        CancellationToken ct);

    /// <summary>Extracts the provider delivery identifier. Null when absent.</summary>
    string? ExtractDeliveryId(ICiWebhookHeaders headers, string rawBodyPreview);

    /// <summary>Extracts the provider event type. Null when absent.</summary>
    string? ExtractEventType(ICiWebhookHeaders headers, string rawBodyPreview);

    /// <summary>
    /// Builds the normalized event from already-authenticated input.
    /// Implementations must parse defensively and return nulls for absent
    /// optional fields — never throw on unexpected payload shapes.
    /// </summary>
    NormalizedCiEvent Normalize(
        Guid integrationId,
        Guid projectId,
        string deliveryId,
        string eventType,
        byte[] rawBody,
        DateTimeOffset receivedAt,
        string payloadHash);
}
