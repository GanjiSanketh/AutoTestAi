using AutoTestAi.Workflows.Configuration;
using AutoTestAi.Workflows.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Worker;

namespace AutoTestAi.Workflows.Workers;

/// <summary>
/// Registers the workflow + activities with Temporal when configured.
/// Exits gracefully (no crash) when Temporal is unavailable in Phase 0.
/// </summary>
public sealed class TemporalWorkerService : BackgroundService
{
    private readonly TemporalOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TemporalWorkerService> _logger;

    public TemporalWorkerService(
        IOptions<TemporalOptions> options,
        IServiceScopeFactory scopes,
        ILogger<TemporalWorkerService> logger)
    {
        _options = options.Value;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Configured)
        {
            _logger.LogWarning("Temporal worker disabled: Temporal section is not configured.");
            return;
        }

        try
        {
            var client = await TemporalClient.ConnectAsync(
                new TemporalClientConnectOptions(_options.Address) { Namespace = _options.Namespace })
                .WaitAsync(stoppingToken);
            using var worker = new TemporalWorker(
                client,
                new TemporalWorkerOptions(_options.TaskQueue)
                    .AddWorkflow<TestExecutionWorkflow>()
                    .AddAllActivities(new TestExecutionActivities(_scopes)));
            _logger.LogInformation(
                "Temporal worker listening on queue {TaskQueue} (namespace {Namespace}).",
                _options.TaskQueue, _options.Namespace);
            await worker.ExecuteAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temporal worker failed. The API continues to serve requests.");
        }
    }
}
