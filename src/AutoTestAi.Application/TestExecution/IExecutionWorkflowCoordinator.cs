namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Durable-workflow boundary for executions (Slice 5 §10). Implemented with
/// Temporal in the Workflows layer; fakes back the API in tests.
/// </summary>
public interface IExecutionWorkflowCoordinator
{
    bool IsConfigured { get; }

    /// <summary>Starts the execution workflow; returns the workflow id.</summary>
    Task<string> StartAsync(Guid executionId, Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Requests workflow cancellation. Returns false when the workflow is
    /// already gone (caller reconciles terminal state itself).
    /// </summary>
    Task<bool> CancelAsync(string workflowId, CancellationToken cancellationToken);
}
