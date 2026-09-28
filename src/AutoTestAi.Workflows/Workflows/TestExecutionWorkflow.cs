using Temporalio.Workflows;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>
/// Minimal durable execution workflow skeleton (docs/03 §5).
/// Phase 1 expands this into the full pipeline:
/// validate → prepare → allocate → run → collect → retry → analyze → complete.
/// </summary>
[Workflow]
public sealed class TestExecutionWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(Guid executionId, Guid projectId)
    {
        var validated = await Workflow.ExecuteActivityAsync(
            (TestExecutionActivities a) => a.ValidateRevisionAsync(executionId),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) });

        await Workflow.ExecuteActivityAsync(
            (TestExecutionActivities a) => a.PrepareEnvironmentAsync(executionId, projectId),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5) });

        // Phase 1: allocate worker, run tests, collect artifacts, analyze failures.
        return $"validated={validated} execution={executionId}";
    }
}
