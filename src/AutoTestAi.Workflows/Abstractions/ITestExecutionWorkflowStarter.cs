namespace AutoTestAi.Workflows.Abstractions;

/// <summary>
/// Starts the durable test-execution workflow (docs/04 §7).
/// Phase 0 proves API → Temporal initiation; full orchestration lands in Phase 1.
/// </summary>
public interface ITestExecutionWorkflowStarter
{
    bool IsConfigured { get; }
    Task<string> StartTestExecutionAsync(Guid executionId, Guid projectId, CancellationToken cancellationToken);
}
