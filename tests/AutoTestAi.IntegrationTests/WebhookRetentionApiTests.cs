using AutoTestAi.Application.Webhooks;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 12: webhook-delivery retention purge through the real
/// persistence and processing service (InMemory provider) — eligibility,
/// convergence, configuration gating, and lifecycle preservation.
/// </summary>
public sealed class WebhookRetentionApiTests : IClassFixture<Slice1ApiFactory>, IDisposable
{
    private readonly Slice1ApiFactory _factory;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public WebhookRetentionApiTests(Slice1ApiFactory factory) => _factory = factory;

    public void Dispose() => _seedLock.Dispose();

    private static WebhookDelivery Row(
        DateTimeOffset receivedAt, WebhookProcessingStatus status,
        Guid? claimToken = null, DateTimeOffset? claimExpiresAt = null)
        => new()
        {
            IntegrationId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Provider = "github",
            DeliveryId = Guid.NewGuid().ToString("N"),
            EventType = "push",
            ReceivedAt = receivedAt,
            PayloadHash = "hash",
            VerificationStatus = WebhookVerificationStatus.Verified,
            ProcessingStatus = status,
            ClaimToken = claimToken,
            ClaimExpiresAt = claimExpiresAt,
            CreatedAt = receivedAt,
            UpdatedAt = receivedAt,
        };

    private async Task SeedAsync(params WebhookDelivery[] rows)
    {
        await _seedLock.WaitAsync();
        try
        {
            await _factory.SeedAsync(async db =>
            {
                foreach (var row in rows)
                {
                    if (!await db.WebhookDeliveries.AnyAsync(d => d.Id == row.Id))
                        db.WebhookDeliveries.Add(row);
                }
                await db.SaveChangesAsync();
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static async Task<int> PurgeAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var processing = scope.ServiceProvider.GetRequiredService<IWebhookProcessingService>();
        return await processing.PurgeExpiredAsync(CancellationToken.None);
    }

    private static async Task<bool> ExistsAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<
            AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext>();
        return await db.WebhookDeliveries.AnyAsync(d => d.Id == id);
    }

    [Fact]
    public async Task Purge_RemovesOnlyExpiredTerminalRows_AndConverges()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-100);
        var recent = now.AddDays(-1);
        var oldTriggered = Row(old, WebhookProcessingStatus.Triggered);
        var oldFailed = Row(old, WebhookProcessingStatus.Failed);
        var oldRejected = Row(old, WebhookProcessingStatus.Rejected);
        var oldAccepted = Row(old, WebhookProcessingStatus.Accepted);
        var oldClaimed = Row(old, WebhookProcessingStatus.Accepted, Guid.NewGuid(), now.AddMinutes(5));
        var newTriggered = Row(recent, WebhookProcessingStatus.Triggered);
        await SeedAsync(oldTriggered, oldFailed, oldRejected, oldAccepted, oldClaimed, newTriggered);

        Assert.Equal(3, await PurgeAsync(_factory));

        Assert.False(await ExistsAsync(_factory, oldTriggered.Id));
        Assert.False(await ExistsAsync(_factory, oldFailed.Id));
        Assert.False(await ExistsAsync(_factory, oldRejected.Id));
        Assert.True(await ExistsAsync(_factory, oldAccepted.Id));
        Assert.True(await ExistsAsync(_factory, oldClaimed.Id));
        Assert.True(await ExistsAsync(_factory, newTriggered.Id));

        // Converged: a repeat pass deletes nothing.
        Assert.Equal(0, await PurgeAsync(_factory));
    }

    [Fact]
    public async Task Purge_DrainsBacklogAcrossPasses()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        var rows = Enumerable.Range(0, 5)
            .Select(_ => Row(old, WebhookProcessingStatus.Ignored))
            .ToArray();
        await SeedAsync(rows);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>();
        var listed = await store.ListExpiredAsync(
            DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow, 2, CancellationToken.None);
        Assert.Equal(2, listed.Count);
        Assert.Equal(2, await store.DeleteRangeAsync(listed, CancellationToken.None));
        foreach (var survivor in rows.Where(r => listed.All(l => l.Id != r.Id)))
            Assert.True(await ExistsAsync(_factory, survivor.Id));
    }

    [Fact]
    public async Task Purge_Disabled_SkipsWithoutDeleting()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        var row = Row(old, WebhookProcessingStatus.Triggered);
        await SeedAsync(row);

        using var disabled = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<WebhookOptions>(o => o.PurgeEnabled = false);
        }));
        try
        {
            Assert.Equal(0, await PurgeAsync(disabled));
            Assert.True(await ExistsAsync(_factory, row.Id));
        }
        finally
        {
            disabled.Dispose();
        }
    }
}
