using AutoTestAi.Application.Webhooks;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.Webhooks;

/// <summary>Fail-closed webhook seams when no database is configured.</summary>
public sealed class UnavailableWebhookDeliveryStore : IWebhookDeliveryStore
{
    private static Exception Unavailable() => new InvalidOperationException("Webhook delivery storage is not configured.");
    public Task<WebhookDelivery?> GetByIdAsync(Guid deliveryId, CancellationToken ct) => throw Unavailable();
    public Task<WebhookDelivery?> FindByIntegrationAndDeliveryAsync(Guid integrationId, string deliveryId, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<WebhookDelivery>> ListByIntegrationAsync(Guid integrationId, int skip, int take, CancellationToken ct) => throw Unavailable();
    public Task<int> CountByIntegrationAsync(Guid integrationId, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<WebhookDelivery>> ListStaleAcceptedAsync(DateTimeOffset olderThan, int take, CancellationToken ct) => throw Unavailable();
    public Task AddAsync(WebhookDelivery delivery, CancellationToken ct) => throw Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => throw Unavailable();
}

public sealed class UnavailableSuiteMemberLookup : ISuiteMemberLookup
{
    public Task<IReadOnlyList<SuiteMemberRow>> ListMembersAsync(Guid suiteId, CancellationToken ct)
        => throw new InvalidOperationException("Suite membership storage is not configured.");
}
