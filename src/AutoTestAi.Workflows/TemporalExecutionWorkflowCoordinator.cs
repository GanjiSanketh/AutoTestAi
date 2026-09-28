using AutoTestAi.Application.TestExecution;
using AutoTestAi.Workflows.Abstractions;
using AutoTestAi.Workflows.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace AutoTestAi.Workflows;

/// <summary>Temporal-backed execution coordinator (Slice 5 §10).</summary>
public sealed class TemporalExecutionWorkflowCoordinator : IExecutionWorkflowCoordinator
{
    private readonly TemporalOptions _options;
    private readonly ITestExecutionWorkflowStarter _starter;
    private readonly ILogger<TemporalExecutionWorkflowCoordinator> _logger;

    public TemporalExecutionWorkflowCoordinator(
        IOptions<TemporalOptions> options,
        ITestExecutionWorkflowStarter starter,
        ILogger<TemporalExecutionWorkflowCoordinator> logger)
    {
        _options = options.Value;
        _starter = starter;
        _logger = logger;
    }

    public bool IsConfigured => _starter.IsConfigured;

    public Task<string> StartAsync(Guid executionId, Guid projectId, CancellationToken cancellationToken)
        => _starter.StartTestExecutionAsync(executionId, projectId, cancellationToken);

    public async Task<bool> CancelAsync(string workflowId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Temporal is not configured. Set the Temporal section (address/namespace).");
        var client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions(_options.Address) { Namespace = _options.Namespace })
            .WaitAsync(cancellationToken);
        try
        {
            await client.GetWorkflowHandle(workflowId).CancelAsync().WaitAsync(cancellationToken);
            _logger.LogInformation("Cancellation requested for workflow {WorkflowId}.", workflowId);
            return true;
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            // Already gone: the caller reconciles terminal state itself.
            return false;
        }
    }
}
