using Temporalio.Activities;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>Placeholder activities. Real worker coordination lands in Phase 1 (FR-3.3).</summary>
public sealed class TestExecutionActivities
{
    [Activity]
    public Task<bool> ValidateRevisionAsync(Guid executionId)
        => Task.FromResult(executionId != Guid.Empty);

    [Activity]
    public Task<string> PrepareEnvironmentAsync(Guid executionId, Guid projectId)
        => Task.FromResult($"prepared execution={executionId} project={projectId}");

    [Activity]
    public Task<string> RunTestAsync(Guid executionTestId)
        => Task.FromResult($"Phase-0 placeholder: no test executed for {executionTestId}.");

    [Activity]
    public Task<string> CollectArtifactsAsync(Guid executionTestId)
        => Task.FromResult($"Phase-0 placeholder: no artifacts collected for {executionTestId}.");
}
