using AutoTestAi.Application.Webhooks;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Webhooks;

/// <summary>EF Core webhook-delivery seam (Phase 3 Slice 3B). No authorization here.</summary>
public sealed class EfWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly AutoTestAiDbContext _db;

    public EfWebhookDeliveryStore(AutoTestAiDbContext db) => _db = db;

    public Task<WebhookDelivery?> GetByIdAsync(Guid deliveryId, CancellationToken ct)
        => _db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);

    public Task<WebhookDelivery?> FindByIntegrationAndDeliveryAsync(
        Guid integrationId, string deliveryId, CancellationToken ct)
        => _db.WebhookDeliveries.FirstOrDefaultAsync(
            d => d.IntegrationId == integrationId && d.DeliveryId == deliveryId, ct);

    public async Task<IReadOnlyList<WebhookDelivery>> ListByIntegrationAsync(
        Guid integrationId, int skip, int take, CancellationToken ct)
        => await _db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.IntegrationId == integrationId)
            .OrderByDescending(d => d.ReceivedAt)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

    public Task<int> CountByIntegrationAsync(Guid integrationId, CancellationToken ct)
        => _db.WebhookDeliveries.CountAsync(d => d.IntegrationId == integrationId, ct);

    public async Task<IReadOnlyList<WebhookDelivery>> ListStaleAcceptedAsync(
        DateTimeOffset olderThan, int take, CancellationToken ct)
        => await _db.WebhookDeliveries
            .Where(d => d.ProcessingStatus == Domain.Enums.WebhookProcessingStatus.Accepted &&
                        d.UpdatedAt < olderThan &&
                        (d.ClaimToken == null || d.ClaimExpiresAt <= DateTimeOffset.UtcNow))
            .OrderBy(d => d.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task AddAsync(WebhookDelivery delivery, CancellationToken ct)
        => await _db.WebhookDeliveries.AddAsync(delivery, ct);

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}

/// <summary>EF Core suite-membership seam in persisted ExecutionOrder.</summary>
public sealed class EfSuiteMemberLookup : ISuiteMemberLookup
{
    private readonly AutoTestAiDbContext _db;

    public EfSuiteMemberLookup(AutoTestAiDbContext db) => _db = db;

    public async Task<IReadOnlyList<SuiteMemberRow>> ListMembersAsync(Guid suiteId, CancellationToken ct)
        => await _db.SuiteTestCases.AsNoTracking()
            .Where(s => s.SuiteId == suiteId)
            .OrderBy(s => s.ExecutionOrder)
            .ThenBy(s => s.TestCaseId)
            .Select(s => new SuiteMemberRow(s.TestCaseId, s.ExecutionOrder))
            .ToListAsync(ct);
}
