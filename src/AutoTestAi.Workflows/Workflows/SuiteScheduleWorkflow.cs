using AutoTestAi.Workflows.Workflows;
using Temporalio.Common;
using Temporalio.Workflows;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>
/// Schedule-action workflow (Phase 4 Slice 9B). Started by a Temporal
/// Schedule on every tick; runs exactly one fire activity, then completes.
/// Business validation failures are terminal for the tick (returned as
/// skipped data, never retried); infrastructure failures retry bounded.
/// </summary>
[Workflow]
public sealed class SuiteScheduleWorkflow
{
    private static readonly ActivityOptions FireOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(10),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
    };

    [WorkflowRun]
    public async Task<string> RunAsync(Guid scheduleId)
    {
        try
        {
            var executionId = await Workflow.ExecuteActivityAsync(
                (SuiteScheduleActivities activities) => activities.RunSuiteScheduleAsync(scheduleId),
                FireOptions);
            return executionId.ToString();
        }
        catch (Exception ex) when (SuiteScheduleActivities.IsBusinessFailure(ex))
        {
            // Terminal for this tick: the schedule stays armed for the next
            // tick; the failure is audited server-side by the fire path.
            return $"skipped:{ex.Message}";
        }
    }
}
