using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Webhooks;

/// <summary>Suite member row in deterministic execution order.</summary>
public sealed record SuiteMemberRow(Guid TestCaseId, int ExecutionOrder);

/// <summary>Persistence seam for webhook deliveries (implemented in Infrastructure).</summary>
public interface IWebhookDeliveryStore
{
    Task<WebhookDelivery?> GetByIdAsync(Guid deliveryId, CancellationToken ct);
    Task<WebhookDelivery?> FindByIntegrationAndDeliveryAsync(Guid integrationId, string deliveryId, CancellationToken ct);
    Task<IReadOnlyList<WebhookDelivery>> ListByIntegrationAsync(Guid integrationId, int skip, int take, CancellationToken ct);
    Task<int> CountByIntegrationAsync(Guid integrationId, CancellationToken ct);
    Task<IReadOnlyList<WebhookDelivery>> ListStaleAcceptedAsync(DateTimeOffset olderThan, int take, CancellationToken ct);
    /// <summary>
    /// Retention purge scan (Phase 4 Slice 12): tracked rows eligible for
    /// deletion — received strictly before <paramref name="cutoff"/>, in a
    /// terminal processing state, and holding no live claim — oldest first
    /// with a stable id tie-break, bounded by <paramref name="take"/>.
    /// Non-terminal and live-claim rows are never returned.
    /// </summary>
    Task<IReadOnlyList<WebhookDelivery>> ListExpiredAsync(DateTimeOffset cutoff, DateTimeOffset now, int take, CancellationToken ct);
    /// <summary>Removes previously listed rows; returns the deleted count.</summary>
    Task<int> DeleteRangeAsync(IReadOnlyList<WebhookDelivery> rows, CancellationToken ct);
    Task AddAsync(WebhookDelivery delivery, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Suite membership lookup in persisted ExecutionOrder (deterministic fan-out).</summary>
public interface ISuiteMemberLookup
{
    Task<IReadOnlyList<SuiteMemberRow>> ListMembersAsync(Guid suiteId, CancellationToken ct);
}
