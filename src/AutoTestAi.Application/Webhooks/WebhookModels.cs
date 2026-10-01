using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Webhooks;

// ---------- Commands ----------

/// <summary>Create or replace a project's CI/CD integration for one provider.</summary>
public sealed record UpsertCiIntegrationCommand(
    Guid ProjectId,
    string Provider,
    bool Enabled,
    Guid? DefaultSuiteId,
    Guid? DefaultEnvironmentId,
    IReadOnlyList<string>? EventAllowlist,
    IReadOnlyList<string>? BranchAllowlist,
    IReadOnlyList<string>? RepositoryAllowlist,
    IReadOnlyDictionary<string, string>? VariableMapping,
    string? Username,
    IReadOnlyDictionary<string, string>? SecretMapping,
    string? WebhookSecret);

/// <summary>Ingress input assembled by the API endpoint (already size-guarded).</summary>
public sealed record WebhookIngressInput(
    string Provider,
    Guid ProjectId,
    Guid IntegrationId,
    byte[] RawBody,
    IReadOnlyDictionary<string, string> Headers);

// ---------- DTOs (never carry secrets, signatures, headers, or payloads) ----------

public sealed record CiIntegrationDto(
    Guid Id,
    Guid ProjectId,
    string Provider,
    bool Enabled,
    bool Configured,
    Guid? DefaultSuiteId,
    Guid? DefaultEnvironmentId,
    IReadOnlyList<string> EventAllowlist,
    IReadOnlyList<string> BranchAllowlist,
    IReadOnlyList<string> RepositoryAllowlist,
    IReadOnlyDictionary<string, string> VariableMapping,
    string? Username,
    IReadOnlyDictionary<string, string> SecretMapping,
    bool HasSecret,
    string WebhookUrl,
    DateTimeOffset UpdatedAt);

public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid IntegrationId,
    Guid ProjectId,
    string Provider,
    string DeliveryId,
    string EventType,
    string VerificationStatus,
    string ProcessingStatus,
    Guid? ExecutionId,
    int TriggeredCount,
    string? FailureReason,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset CreatedAt);

/// <summary>Deterministic ingress outcome (maps to HTTP semantics in the endpoint).</summary>
public sealed record WebhookIngressResult(
    string Outcome,
    Guid? DeliveryId,
    string? EventType,
    Guid? ExecutionId,
    int TriggeredCount,
    string? FailureReason)
{
    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
    public const string Ignored = "ignored";
}

// ---------- Services ----------

public interface ICiIntegrationService
{
    Task<CiIntegrationDto> UpsertAsync(UpsertCiIntegrationCommand command, CancellationToken ct);
    Task<CiIntegrationDto?> GetAsync(Guid projectId, string provider, CancellationToken ct);
    Task<IReadOnlyList<CiIntegrationDto>> ListAsync(Guid projectId, CancellationToken ct);
}

public interface IWebhookIngestionService
{
    Task<WebhookIngressResult> IngestAsync(WebhookIngressInput input, CancellationToken ct);
}

public interface IWebhookProcessingService
{
    /// <summary>Claims and processes one delivery; safe to call concurrently and repeatedly.</summary>
    Task ProcessAsync(Guid deliveryId, CancellationToken ct);

    /// <summary>Re-drives stale Accepted deliveries (crash recovery).</summary>
    Task<int> ReconcileStaleAsync(CancellationToken ct);

    /// <summary>Re-drives a Failed delivery (authorized retry); same delivery id, idempotent.</summary>
    Task RetryAsync(Guid deliveryId, CancellationToken ct);
}

public interface IWebhookDeliveryQueryService
{
    Task<Common.PagedResult<WebhookDeliveryDto>> ListAsync(
        Guid projectId, Guid integrationId, int page, int pageSize, CancellationToken ct);
    Task<WebhookDeliveryDto> GetAsync(Guid projectId, Guid deliveryId, CancellationToken ct);
}

public static class WebhookModelMapper
{
    public static WebhookDeliveryDto Map(Domain.Entities.WebhookDelivery d) => new(
        d.Id, d.IntegrationId, d.ProjectId, d.Provider, d.DeliveryId, d.EventType,
        d.VerificationStatus.ToString(), d.ProcessingStatus.ToString(),
        d.ExecutionId, d.TriggeredCount, d.FailureReason,
        d.ReceivedAt, d.ProcessedAt, d.CreatedAt);

    public static bool IsTerminal(WebhookProcessingStatus status)
        => status is WebhookProcessingStatus.Triggered
            or WebhookProcessingStatus.Failed
            or WebhookProcessingStatus.Rejected
            or WebhookProcessingStatus.Duplicate
            or WebhookProcessingStatus.Ignored;
}
