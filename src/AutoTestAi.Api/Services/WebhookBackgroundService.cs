using AutoTestAi.Application.Webhooks;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Api.Services;

/// <summary>
/// Background webhook processor (Phase 3 Slice 3B). Drains the in-memory
/// handoff queue and periodically reconciles stale Accepted rows so eligible
/// deliveries survive an API process restart. Never touches provider secrets:
/// the scoped processing service resolves them server-side per attempt.
/// Mirrors AutoTicketBackgroundService.
/// </summary>
public sealed class WebhookBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly WebhookQueue _queue;
    private readonly IOptions<WebhookOptions> _options;
    private readonly ILogger<WebhookBackgroundService> _logger;

    public WebhookBackgroundService(
        IServiceProvider services,
        WebhookQueue queue,
        IOptions<WebhookOptions> options,
        ILogger<WebhookBackgroundService> logger)
    {
        _services = services;
        _queue = queue;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.BackgroundProcessingEnabled)
            return;

        await ReconcileAsync(stoppingToken);

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Clamp(_options.Value.ReconciliationIntervalSeconds, 5, 600)));
        var queueTask = DrainQueueAsync(stoppingToken);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        await queueTask;
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _queue.ReadAllAsync(ct))
                await ExecuteOneAsync(item.DeliveryId, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var processing = scope.ServiceProvider.GetRequiredService<IWebhookProcessingService>();
            await processing.ReconcileStaleAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook reconciliation pass did not complete.");
        }
    }

    private async Task ExecuteOneAsync(Guid deliveryId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var processing = scope.ServiceProvider.GetRequiredService<IWebhookProcessingService>();
            await processing.ProcessAsync(deliveryId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} processing did not complete.", deliveryId);
        }
    }
}
