using AutoTestAi.Workflows.Abstractions;
using AutoTestAi.Workflows.Configuration;
using AutoTestAi.Workflows.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;

namespace AutoTestAi.Workflows;

/// <summary>Temporal-backed starter. Fails loudly when Temporal is not configured.</summary>
public sealed class TemporalWorkflowStarter : ITestExecutionWorkflowStarter
{
    private readonly TemporalOptions _options;
    private readonly ILogger<TemporalWorkflowStarter> _logger;

    public TemporalWorkflowStarter(IOptions<TemporalOptions> options, ILogger<TemporalWorkflowStarter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.Configured;

    public async Task<string> StartTestExecutionAsync(Guid executionId, Guid projectId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Temporal is not configured. Set the Temporal section (address/namespace).");

        var connectOptions = new TemporalClientConnectOptions(_options.Address)
        {
            Namespace = _options.Namespace,
        };
        var client = await TemporalClient.ConnectAsync(connectOptions).WaitAsync(cancellationToken);
        var workflowId = $"test-execution-{executionId:N}";
        var handle = await client.StartWorkflowAsync(
            (TestExecutionWorkflow wf) => wf.RunAsync(executionId, projectId),
            new WorkflowOptions(workflowId, _options.TaskQueue));
        _logger.LogInformation(
            "Started TestExecutionWorkflow {WorkflowId} for execution {ExecutionId}.",
            handle.Id, executionId);
        return handle.Id;
    }
}
