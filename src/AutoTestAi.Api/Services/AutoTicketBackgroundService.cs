using AutoTestAi.Application.Tickets;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Api.Services;

/// <summary>
/// Background executor for automatic Jira tickets (Phase 2 Slice 10).
/// Drains the in-memory handoff queue and periodically reconciles
/// persisted Pending/Failed-due rows so eligible automation survives an
/// API process restart. Never touches Jira credentials: the scoped
/// automation service resolves them server-side per attempt.
/// </summary>
public sealed class AutoTicketBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly AutoTicketQueue _queue;
    private readonly IOptions<AutoTicketOptions> _options;
    private readonly ILogger<AutoTicketBackgroundService> _logger;

    public AutoTicketBackgroundService(
        IServiceProvider services,
        AutoTicketQueue queue,
        IOptions<AutoTicketOptions> options,
        ILogger<AutoTicketBackgroundService> logger)
    {
        _services = services;
        _queue = queue;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup reconciliation: re-queue intents persisted before a restart.
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
                await ExecuteOneAsync(item.TicketId, ct);
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
            var query = scope.ServiceProvider.GetRequiredService<IAutoTicketQueryStore>();
            var clock = scope.ServiceProvider.GetRequiredService<AutoTestAi.Application.Common.IDateTimeProvider>();
            var due = await query.ListDueAutomationAsync(
                clock.UtcNow, _options.Value.ReconciliationBatchSize, ct);
            foreach (var ticket in due)
            {
                if (ct.IsCancellationRequested)
                    return;
                await ExecuteOneAsync(ticket.Id, ct);
            }
        }
        catch (Exception ex)
        {
            // Reconciliation is best-effort; queue drainage continues.
            _logger.LogWarning(ex, "Auto-ticket reconciliation pass did not complete.");
        }
    }

    private async Task ExecuteOneAsync(Guid ticketId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var automation = scope.ServiceProvider.GetRequiredService<IAutomatedTicketService>();
            await automation.ExecutePendingAsync(ticketId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic ticket {TicketId} execution did not complete.", ticketId);
        }
    }
}
