using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Asynchronous webhook processor (Phase 3 Slice 3B): claims an Accepted
/// delivery (cross-instance lease), filters, resolves suite/environment,
/// fans out through the existing TestExecutionService (TriggerType.Ci), and
/// records terminal delivery state. All validation completes BEFORE the first
/// execution starts; per-member idempotency keys make retries converge.
/// </summary>
public sealed class WebhookProcessingService : IWebhookProcessingService
{
    private readonly IWebhookDeliveryStore _deliveries;
    private readonly IIntegrationStore _integrations;
    private readonly IProjectStore _projects;
    private readonly ITestSuiteLookup _suites;
    private readonly ISuiteMemberLookup _members;
    private readonly ITestCaseStore _cases;
    private readonly ITestExecutionService _executions;
    private readonly ISecretResolver _resolver;
    private readonly ISecretStore _secretStore;
    private readonly WebhookQueue _queue;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookProcessingService> _logger;

    public WebhookProcessingService(
        IWebhookDeliveryStore deliveries,
        IIntegrationStore integrations,
        IProjectStore projects,
        ITestSuiteLookup suites,
        ISuiteMemberLookup members,
        ITestCaseStore cases,
        ITestExecutionService executions,
        ISecretResolver resolver,
        ISecretStore secretStore,
        WebhookQueue queue,
        IAuditService audit,
        IDateTimeProvider clock,
        IOptions<WebhookOptions> options,
        ILogger<WebhookProcessingService> logger)
    {
        _deliveries = deliveries;
        _integrations = integrations;
        _projects = projects;
        _suites = suites;
        _members = members;
        _cases = cases;
        _executions = executions;
        _resolver = resolver;
        _secretStore = secretStore;
        _queue = queue;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Deterministic per-member execution idempotency key. Bounded to the
    /// 100-char execution key limit (long delivery ids hash down).
    /// </summary>
    public static string IdempotencyKeyFor(Guid integrationId, string deliveryId, int memberIndex)
    {
        var full = $"wh:{integrationId:N}:{deliveryId}:{memberIndex}";
        if (full.Length <= 100)
            return full;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId))).ToLowerInvariant();
        return $"wh:{integrationId:N}:{hash[..32]}:{memberIndex}";
    }

    public async Task ProcessAsync(Guid deliveryId, CancellationToken ct)
    {
        var delivery = await _deliveries.GetByIdAsync(deliveryId, ct);
        if (delivery is null)
            return;
        if (WebhookModelMapper.IsTerminal(delivery.ProcessingStatus))
            return;

        // Claim the delivery (cross-instance lease). Losing the race is normal.
        if (!await TryClaimAsync(delivery, ct))
            return;

        try
        {
            await ProcessClaimedAsync(delivery, ct);
        }
        catch (Exception ex) when (ex is not (UnauthorizedAccessException or ForbiddenException))
        {
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} processing did not complete.", delivery.Id);
            await FinishFailedAsync(delivery, "processing_error", ct);
        }
    }

    public async Task<int> ReconcileStaleAsync(CancellationToken ct)
    {
        var cutoff = _clock.UtcNow.AddSeconds(-Math.Max(5, _options.ClaimLeaseSeconds));
        var stale = await _deliveries.ListStaleAcceptedAsync(cutoff, _options.ReconciliationBatchSize, ct);
        var requeued = 0;
        foreach (var delivery in stale)
        {
            if (ct.IsCancellationRequested)
                break;
            // Release expired claims so the claim protocol can proceed.
            if (delivery.ClaimToken.HasValue && delivery.ClaimExpiresAt <= _clock.UtcNow)
            {
                delivery.ClaimToken = null;
                delivery.ClaimExpiresAt = null;
                delivery.UpdatedAt = _clock.UtcNow;
                try { await _deliveries.SaveChangesAsync(ct); }
                catch (Exception ex) when (IsConcurrencyConflict(ex)) { continue; }
            }
            _queue.Enqueue(new WebhookWorkItem(delivery.Id));
            requeued++;
        }
        return requeued;
    }

    public async Task RetryAsync(Guid deliveryId, CancellationToken ct)
    {
        var delivery = await _deliveries.GetByIdAsync(deliveryId, ct)
            ?? throw new NotFoundException("Webhook delivery not found.");
        if (delivery.ProcessingStatus != WebhookProcessingStatus.Failed)
            throw new ValidationException("Only failed deliveries can be retried.",
                new[] { new FieldError("deliveryId", "Only deliveries in Failed status can be retried.") });
        delivery.ProcessingStatus = WebhookProcessingStatus.Accepted;
        delivery.VerificationStatus = WebhookVerificationStatus.Verified;
        delivery.FailureReason = null;
        delivery.ClaimToken = null;
        delivery.ClaimExpiresAt = null;
        delivery.UpdatedAt = _clock.UtcNow;
        try
        {
            await _deliveries.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("The delivery was modified concurrently. Reload and retry.");
        }
        await _audit.RecordAsync("webhook.retry_requested", "webhook_delivery",
            delivery.Id.ToString(), delivery.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                provider = delivery.Provider,
                integrationId = delivery.IntegrationId,
                deliveryId = delivery.DeliveryId,
            })), ct);
        _queue.Enqueue(new WebhookWorkItem(delivery.Id));
    }

    private async Task<bool> TryClaimAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (delivery.ClaimToken.HasValue && delivery.ClaimExpiresAt > now)
            return false;
        delivery.ClaimToken = Guid.NewGuid();
        delivery.ClaimExpiresAt = now.AddSeconds(Math.Max(5, _options.ClaimLeaseSeconds));
        delivery.UpdatedAt = now;
        try
        {
            await _deliveries.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            // Lost the claim race; the winner proceeds.
            return false;
        }
    }

    /// <summary>
    /// Concurrency-conflict detection without a hard dependency on an EF
    /// exception type (mirrors AutomatedTicketService).
    /// </summary>
    private static bool IsConcurrencyConflict(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateConcurrencyException", StringComparison.Ordinal))
            return true;
        if (ex.InnerException is not null && IsConcurrencyConflict(ex.InnerException))
            return true;
        return false;
    }

    private async Task ProcessClaimedAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        // Integration must still resolve (may have been disabled/deleted since ingress).
        var integration = await _integrations.GetByIdAsync(delivery.IntegrationId, ct);
        if (integration is null ||
            integration.ProjectId != delivery.ProjectId ||
            integration.Status != IntegrationStatus.Active ||
            !string.Equals(integration.IntegrationType, CiProviderNames.IntegrationType, StringComparison.Ordinal) ||
            !string.Equals(integration.Provider, delivery.Provider, StringComparison.OrdinalIgnoreCase))
        {
            await FinishRejectedAsync(delivery, "integration_unavailable", ct);
            return;
        }

        var config = CiIntegrationConfig.FromJson(integration.Configuration);
        var normalized = ReadNormalized(delivery);
        if (normalized is null)
        {
            await FinishFailedAsync(delivery, "invalid_delivery_metadata", ct);
            return;
        }

        if (!PassesFilters(config, normalized))
        {
            await FinishIgnoredAsync(delivery, "filtered", ct);
            return;
        }

        // Suite is mandatory for webhook execution (fail closed, no partial work yet).
        if (!config.DefaultSuiteId.HasValue)
        {
            await FinishRejectedAsync(delivery, "missing_suite", ct);
            return;
        }
        var suite = await _suites.GetSuiteByIdAsync(config.DefaultSuiteId.Value, ct);
        if (suite is null || suite.ProjectId != delivery.ProjectId || suite.Status != ProjectStatus.Active)
        {
            await FinishRejectedAsync(delivery, "invalid_suite", ct);
            return;
        }

        // Environment is mandatory for webhook execution (Slice 3A rules, no legacy path).
        if (!config.DefaultEnvironmentId.HasValue)
        {
            await FinishRejectedAsync(delivery, "missing_environment", ct);
            return;
        }
        var environment = await _projects.GetEnvironmentByIdAsync(config.DefaultEnvironmentId.Value, ct);
        if (environment is null || environment.ProjectId != delivery.ProjectId ||
            environment.Status != ProjectStatus.Active)
        {
            await FinishRejectedAsync(delivery, "invalid_environment", ct);
            return;
        }

        // Deterministic member resolution: persisted ExecutionOrder, then TestCaseId.
        var members = (await _members.ListMembersAsync(suite.Id, ct))
            .OrderBy(m => m.ExecutionOrder)
            .ThenBy(m => m.TestCaseId)
            .ToList();
        if (members.Count == 0)
        {
            await FinishRejectedAsync(delivery, "empty_suite", ct);
            return;
        }
        if (members.Count > _options.MaxSuiteMembers)
        {
            await FinishFailedAsync(delivery, "suite_too_large", ct);
            return;
        }

        var latest = await _cases.GetLatestVersionsAsync(members.Select(m => m.TestCaseId).ToList(), ct);
        var executable = new List<(SuiteMemberRow Member, TestCaseVersion Version)>();
        foreach (var member in members)
        {
            if (!latest.TryGetValue(member.TestCaseId, out var version) ||
                version.ReviewStatus != ReviewStatus.Approved)
                continue;
            var testCase = await _cases.GetByIdAsync(member.TestCaseId, ct);
            if (testCase is null || testCase.ProjectId != delivery.ProjectId ||
                testCase.Status == TestCaseStatus.Archived)
                continue;
            executable.Add((member, version));
        }
        if (executable.Count == 0)
        {
            await FinishFailedAsync(delivery, "no_executable_members", ct);
            return;
        }

        // Variable overrides (plain only) + configured secret refs (ownership-checked).
        Dictionary<string, string> variables;
        try
        {
            variables = CiVariableMapping.BuildOverrides(config.VariableMapping, normalized);
        }
        catch (ValidationException)
        {
            await FinishFailedAsync(delivery, "invalid_variable_mapping", ct);
            return;
        }
        Dictionary<string, string>? secretRefs = null;
        if (config.SecretMapping.Count > 0)
        {
            secretRefs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (target, reference) in config.SecretMapping.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!await IsOwnedSecretAsync(delivery.ProjectId, environment.Id, reference, ct))
                {
                    await FinishFailedAsync(delivery, "invalid_secret_mapping", ct);
                    return;
                }
                secretRefs[target] = reference;
            }
        }

        // Fan-out. Per-member idempotency keys make retries converge instead of
        // duplicating (StartAsync returns Duplicated for repeats).
        var executionIds = new List<Guid>();
        for (var i = 0; i < executable.Count; i++)
        {
            var (_, version) = executable[i];
            var command = new StartExecutionCommand(
                delivery.ProjectId,
                version.Id,
                environment.Id,
                Browser: null,
                IdempotencyKey: IdempotencyKeyFor(delivery.IntegrationId, delivery.DeliveryId, i),
                SuiteId: suite.Id,
                VariableOverrides: variables,
                SecretRefOverrides: secretRefs,
                Trigger: TriggerType.Ci);
            StartExecutionResultDto started;
            try
            {
                started = await _executions.StartAsSystemAsync(command, ct);
            }
            catch (Exception ex) when (ex is not (UnauthorizedAccessException or ForbiddenException))
            {
                _logger.LogWarning(ex, "Webhook delivery {DeliveryId} fan-out failed at member {Index}.", delivery.Id, i);
                await FinishFailedAsync(delivery, "execution_start_failed", ct);
                return;
            }
            executionIds.Add(started.ExecutionId);
        }

        delivery.ExecutionId = executionIds[0];
        delivery.TriggeredCount = executionIds.Count;
        delivery.ProcessingStatus = WebhookProcessingStatus.Triggered;
        delivery.ProcessedAt = _clock.UtcNow;
        delivery.ClaimToken = null;
        delivery.ClaimExpiresAt = null;
        delivery.UpdatedAt = _clock.UtcNow;
        await _deliveries.SaveChangesAsync(ct);

        WebhookMetrics.DeliveryTriggered(delivery.Provider, executionIds.Count);
        await _audit.RecordAsync("webhook.execution_requested", "webhook_delivery",
            delivery.Id.ToString(), delivery.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                provider = delivery.Provider,
                integrationId = delivery.IntegrationId,
                deliveryId = delivery.DeliveryId,
                eventType = delivery.EventType,
                executionId = delivery.ExecutionId,
                triggeredCount = delivery.TriggeredCount,
            })), ct);
    }

    private static NormalizedCiEvent? ReadNormalized(WebhookDelivery delivery)
    {
        if (string.IsNullOrWhiteSpace(delivery.NormalizedMetadataJson))
            return null;
        try
        {
            using var document = JsonDocument.Parse(delivery.NormalizedMetadataJson);
            var root = document.RootElement;
            static string? Str(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new NormalizedCiEvent(
                delivery.Provider, delivery.IntegrationId, delivery.ProjectId,
                delivery.DeliveryId, delivery.EventType,
                Str(root, "branch"), Str(root, "commitSha"), Str(root, "repository"),
                Str(root, "pullRequestNumber"), Str(root, "buildNumber"), Str(root, "ciRunId"),
                Str(root, "actor"), delivery.ReceivedAt, delivery.PayloadHash);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool PassesFilters(CiIntegrationConfig config, NormalizedCiEvent normalized)
    {
        if (config.EventAllowlist.Count > 0 &&
            !config.EventAllowlist.Any(e => string.Equals(e, normalized.EventType, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (config.BranchAllowlist.Count > 0)
        {
            var branch = normalized.Branch ?? string.Empty;
            var matched = config.BranchAllowlist.Any(pattern =>
                pattern.EndsWith("*", StringComparison.Ordinal)
                    ? branch.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(pattern, branch, StringComparison.OrdinalIgnoreCase));
            if (!matched)
                return false;
        }
        if (config.RepositoryAllowlist.Count > 0 &&
            (string.IsNullOrEmpty(normalized.Repository) ||
             !config.RepositoryAllowlist.Any(r => string.Equals(r, normalized.Repository, StringComparison.OrdinalIgnoreCase))))
            return false;
        return true;
    }

    private async Task<bool> IsOwnedSecretAsync(Guid projectId, Guid environmentId, string reference, CancellationToken ct)
    {
        if (!SecretReference.TryParseSecretId(reference, out var secretId))
            return false;
        var metadata = await _secretStore.GetMetadataAsync(secretId, ct);
        return metadata is not null &&
               metadata.ProjectId == projectId &&
               metadata.EnvironmentId == environmentId;
    }

    private async Task FinishRejectedAsync(WebhookDelivery delivery, string reason, CancellationToken ct)
        => await FinishTerminalAsync(delivery, WebhookProcessingStatus.Rejected, reason,
            "webhook.rejected", ct);

    private async Task FinishFailedAsync(WebhookDelivery delivery, string reason, CancellationToken ct)
        => await FinishTerminalAsync(delivery, WebhookProcessingStatus.Failed, reason,
            "webhook.execution_failed", ct);

    private async Task FinishIgnoredAsync(WebhookDelivery delivery, string reason, CancellationToken ct)
        => await FinishTerminalAsync(delivery, WebhookProcessingStatus.Ignored, reason,
            "webhook.rejected", ct);

    private async Task FinishTerminalAsync(
        WebhookDelivery delivery, WebhookProcessingStatus status, string reason, string auditAction, CancellationToken ct)
    {
        delivery.ProcessingStatus = status;
        delivery.FailureReason = reason.Length <= 500 ? reason : reason[..500];
        delivery.ProcessedAt = _clock.UtcNow;
        delivery.ClaimToken = null;
        delivery.ClaimExpiresAt = null;
        delivery.UpdatedAt = _clock.UtcNow;
        try
        {
            await _deliveries.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} terminal write lost a concurrent update.", delivery.Id);
        }
        if (status == WebhookProcessingStatus.Failed)
            WebhookMetrics.DeliveryFailed(delivery.Provider, reason);
        else
            WebhookMetrics.DeliveryRejected(delivery.Provider, reason);
        await _audit.RecordAsync(auditAction, "webhook_delivery",
            delivery.Id.ToString(), delivery.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                provider = delivery.Provider,
                integrationId = delivery.IntegrationId,
                deliveryId = delivery.DeliveryId,
                eventType = delivery.EventType,
                reason,
            })), ct);
    }
}
