using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Synchronous webhook ingress (Phase 3 Slice 3B): size guard → rate guard →
/// integration resolution → provider authentication → delivery extraction →
/// durable persistence → fast acknowledgement. Never waits for test execution.
/// Secrets exist only as in-memory locals and are never persisted, logged, or
/// included in audit/telemetry/exceptions.
/// </summary>
public sealed class WebhookIngestionService : IWebhookIngestionService
{
    private readonly IIntegrationStore _integrations;
    private readonly IWebhookDeliveryStore _deliveries;
    private readonly ISecretResolver _secrets;
    private readonly WebhookRateLimiter _rateLimiter;
    private readonly WebhookQueue _queue;
    private readonly IAuditService _audit;
    private readonly Common.IDateTimeProvider _clock;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookIngestionService> _logger;
    private readonly IReadOnlyDictionary<string, ICiWebhookProvider> _providers;

    public WebhookIngestionService(
        IIntegrationStore integrations,
        IWebhookDeliveryStore deliveries,
        ISecretResolver secrets,
        WebhookRateLimiter rateLimiter,
        WebhookQueue queue,
        IAuditService audit,
        Common.IDateTimeProvider clock,
        IOptions<WebhookOptions> options,
        ILogger<WebhookIngestionService> logger,
        IEnumerable<ICiWebhookProvider> providers)
    {
        _integrations = integrations;
        _deliveries = deliveries;
        _secrets = secrets;
        _rateLimiter = rateLimiter;
        _queue = queue;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.ProviderName, p => p, StringComparer.Ordinal);
    }

    public async Task<WebhookIngressResult> IngestAsync(WebhookIngressInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var started = Stopwatch.GetTimestamp();
        var provider = (input.Provider ?? string.Empty).Trim().ToLowerInvariant();
        WebhookMetrics.DeliveryReceived(provider);

        // 1. Provider allowlist (unknown provider => 404 without probing integrations).
        if (!_providers.TryGetValue(provider, out var adapter))
        {
            WebhookMetrics.DeliveryRejected(provider, "unknown_provider");
            throw new Common.NotFoundException("Webhook provider not found.");
        }

        // 2. Body size guard before any expensive work.
        var rawBody = input.RawBody ?? Array.Empty<byte>();
        if (rawBody.LongLength > _options.MaxBodyBytes)
        {
            WebhookMetrics.DeliveryRejected(provider, "too_large");
            throw new WebhookTooLargeException(
                $"Webhook body exceeds the {_options.MaxBodyBytes} byte limit.");
        }

        // 3. Rate guard (process-local; correctness never depends on it).
        _rateLimiter.CheckOrThrow(input.ProjectId);

        // 4. Integration resolution: must exist, belong to the project, be an
        //    active cicd row, and match the route provider. All mismatches map
        //    to 404 so configuration existence is not enumerated.
        var integration = await _integrations.GetByIdAsync(input.IntegrationId, ct);
        if (integration is null ||
            integration.ProjectId != input.ProjectId ||
            integration.Status != IntegrationStatus.Active ||
            !string.Equals(integration.IntegrationType, CiProviderNames.IntegrationType, StringComparison.Ordinal) ||
            !string.Equals(integration.Provider, provider, StringComparison.OrdinalIgnoreCase))
        {
            await AuditAsync("webhook.rejected", input, null, "unknown_integration", ct);
            WebhookMetrics.DeliveryRejected(provider, "unknown_integration");
            throw new Common.NotFoundException("Webhook integration not found.");
        }

        var headers = new DictionaryHeaders(input.Headers);
        var bodyText = rawBody.Length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(rawBody);

        // 5. Provider authentication. The secret is resolved in-memory and
        //    discarded; a missing/unresolvable reference fails closed.
        string? secret = null;
        if (!string.IsNullOrEmpty(integration.SecretReference))
        {
            try { secret = await _secrets.ResolveAsync(integration.SecretReference!, ct); }
            catch (Common.NotFoundException) { secret = null; }
        }
        var config = CiIntegrationConfig.FromJson(integration.Configuration);
        var authenticated = await adapter.AuthenticateAsync(rawBody, headers, secret, config, ct);
        if (!authenticated)
        {
            await AuditAsync("webhook.authentication_failed", input, integration, null, ct);
            WebhookMetrics.DeliveryRejected(provider, "authentication_failed");
            throw new UnauthorizedAccessException("Webhook authentication failed.");
        }

        // 6. Delivery/event extraction.
        var deliveryId = adapter.ExtractDeliveryId(headers, bodyText)?.Trim();
        var eventType = adapter.ExtractEventType(headers, bodyText)?.Trim();
        if (string.IsNullOrEmpty(deliveryId) || deliveryId.Length > 200)
        {
            await AuditAsync("webhook.rejected", input, integration, "missing_delivery_id", ct);
            WebhookMetrics.DeliveryRejected(provider, "missing_delivery_id");
            throw new Common.ValidationException("The webhook delivery could not be identified.",
                new[] { new Common.FieldError("deliveryId", "A provider delivery identifier is required.") });
        }
        if (string.IsNullOrEmpty(eventType) || eventType.Length > 200)
            eventType = "unknown";

        // 7. Durable idempotency: pre-check then insert; the unique
        //    (IntegrationId, DeliveryId) constraint is the cross-instance
        //    correctness boundary (unique-violation => duplicate).
        var existing = await _deliveries.FindByIntegrationAndDeliveryAsync(integration.Id, deliveryId, ct);
        if (existing is not null)
        {
            await AuditAsync("webhook.duplicate", input, integration, deliveryId, ct);
            WebhookMetrics.DeliveryDuplicate(provider);
            return new WebhookIngressResult(
                WebhookIngressResult.Duplicate, existing.Id, existing.EventType,
                existing.ExecutionId, existing.TriggeredCount, existing.FailureReason);
        }

        var now = _clock.UtcNow;
        var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
        var normalized = adapter.Normalize(
            integration.Id, input.ProjectId, deliveryId, eventType, rawBody, now, payloadHash);
        var delivery = new WebhookDelivery
        {
            IntegrationId = integration.Id,
            ProjectId = input.ProjectId,
            Provider = provider,
            DeliveryId = deliveryId,
            EventType = eventType,
            ReceivedAt = now,
            PayloadHash = payloadHash,
            VerificationStatus = WebhookVerificationStatus.Verified,
            ProcessingStatus = WebhookProcessingStatus.Accepted,
            NormalizedMetadataJson = SensitiveDataRedactor.Redact(BuildMetadataJson(normalized, config)),
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await _deliveries.AddAsync(delivery, ct);
            await _deliveries.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            var winner = await _deliveries.FindByIntegrationAndDeliveryAsync(integration.Id, deliveryId, ct);
            _logger.LogDebug(ex, "Webhook delivery converged on an existing row for integration {IntegrationId}.", integration.Id);
            await AuditAsync("webhook.duplicate", input, integration, deliveryId, ct);
            WebhookMetrics.DeliveryDuplicate(provider);
            return new WebhookIngressResult(
                WebhookIngressResult.Duplicate, winner?.Id, winner?.EventType,
                winner?.ExecutionId, winner?.TriggeredCount ?? 0, winner?.FailureReason);
        }

        await AuditAsync("webhook.accepted", input, integration, deliveryId, ct);
        WebhookMetrics.RecordProcessingDuration(provider,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        _queue.Enqueue(new WebhookWorkItem(delivery.Id));

        return new WebhookIngressResult(
            WebhookIngressResult.Accepted, delivery.Id, eventType, null, 0, null);
    }

    private static string BuildMetadataJson(NormalizedCiEvent normalized, CiIntegrationConfig config)
        => JsonSerializer.Serialize(new
        {
            provider = normalized.Provider,
            deliveryId = normalized.DeliveryId,
            eventType = normalized.EventType,
            branch = normalized.Branch,
            commitSha = normalized.CommitSha,
            repository = normalized.Repository,
            pullRequestNumber = normalized.PullRequestNumber,
            buildNumber = normalized.BuildNumber,
            ciRunId = normalized.CiRunId,
            actor = normalized.Actor,
            suiteId = config.DefaultSuiteId,
            environmentId = config.DefaultEnvironmentId,
        });

    private Task AuditAsync(string action, WebhookIngressInput input, Integration? integration, string? detail, CancellationToken ct)
        => _audit.RecordAsync(action, "webhook_delivery", integration?.Id.ToString(),
            input.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                provider = (input.Provider ?? string.Empty).Trim().ToLowerInvariant(),
                integrationId = input.IntegrationId,
                detail,
            })), ct);

    private static bool IsUniqueViolation(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateException", StringComparison.Ordinal))
            return true;
        if (ex.InnerException is not null && IsUniqueViolation(ex.InnerException))
            return true;
        return ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DictionaryHeaders : ICiWebhookHeaders
    {
        private readonly IReadOnlyDictionary<string, string> _headers;
        public DictionaryHeaders(IReadOnlyDictionary<string, string> headers) => _headers = headers;
        public string? Get(string name)
        {
            foreach (var (key, value) in _headers)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return value;
            return null;
        }
    }
}
