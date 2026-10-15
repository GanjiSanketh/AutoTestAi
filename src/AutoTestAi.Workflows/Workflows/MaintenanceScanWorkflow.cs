using AutoTestAi.Workflows.Workflows;
using Temporalio.Common;
using Temporalio.Workflows;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>
/// Schedule-action workflow (Phase 4 Slice 10). Started by a Temporal
/// Schedule on every tick; runs exactly one fire activity, then completes.
/// Business validation failures are terminal for the tick (returned as
/// skipped data, never retried); infrastructure failures retry bounded.
/// The scan can only create Proposed rows; approval stays human-gated.
/// </summary>
[Workflow]
public sealed class MaintenanceScanWorkflow
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
            var result = await Workflow.ExecuteActivityAsync(
                (MaintenanceScanActivities activities) => activities.RunMaintenanceScanAsync(scheduleId),
                FireOptions);
            return $"scanned:{result.ProposalsCreated}/{result.CandidatesDetected}";
        }
        catch (Exception ex) when (MaintenanceScanActivities.IsBusinessFailure(ex))
        {
            // Terminal for this tick: the schedule stays armed for the next
            // tick; the failure is audited server-side by the fire path.
            return $"skipped:{ex.Message}";
        }
    }
}
