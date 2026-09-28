using AutoTestAi.Application.TestExecution;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>
/// Durable execution pipeline (Slice 5 §10, ADR-004). Deterministic: only
/// workflow-safe calls here; every side effect runs in an activity.
/// Retries are deliberate — functional test outcomes return as data (never
/// retried); infrastructure-touching activities carry bounded retry policies.
/// Terminal finalization is idempotent in the engine, so timeout/cancel races
/// resolve to exactly one terminal state.
///
/// Cancellation (Temporal delivers it as <see cref="CanceledFailureException"/>)
/// is handled explicitly: compensation activities run with a disconnected
/// cancellation token so terminal Cancelled is always persisted.
/// </summary>
[Workflow]
public sealed class TestExecutionWorkflow
{
    private static readonly ActivityOptions PrepareOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
    };

    private static readonly ActivityOptions RunOptions = new()
    {
        // Backstop above the worker's own execution timeout: the worker reports
        // TimedOut itself, so this fires only when the worker hangs.
        StartToCloseTimeout = TimeSpan.FromMinutes(12),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    };

    private static readonly ActivityOptions PersistOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
    };

    // Compensation must survive workflow cancellation: disconnected token.
    private static readonly ActivityOptions FinalizeOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
        CancellationToken = CancellationToken.None,
    };

    [WorkflowRun]
    public async Task<string> RunAsync(Guid executionId, Guid projectId)
    {
        _ = projectId;
        PreparedExecution prepared;
        try
        {
            prepared = await Workflow.ExecuteActivityAsync(
                (TestExecutionActivities activities) => activities.PrepareExecutionAsync(executionId),
                PrepareOptions);
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            await CompensateAsync(executionId, "cancelled-before-start");
            return "cancelled";
        }
        catch (Exception)
        {
            await Workflow.ExecuteActivityAsync(
                (TestExecutionActivities activities) => activities.FinalizeErrorAsync(
                    executionId, "Execution preparation failed."),
                FinalizeOptions);
            return "error";
        }

        if (!prepared.CanRun)
            return $"skipped:{prepared.SkipReason}";

        try
        {
            var outcome = await Workflow.ExecuteActivityAsync(
                (TestExecutionActivities activities) => activities.RunWorkerExecutionAsync(executionId),
                RunOptions);
            await Workflow.ExecuteActivityAsync(
                (TestExecutionActivities activities) => activities.PersistExecutionResultAsync(executionId, outcome),
                PersistOptions);
            return outcome.Status.ToString();
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            await CompensateAsync(executionId, "cancellation requested");
            return "cancelled";
        }
        catch (Exception)
        {
            await Workflow.ExecuteActivityAsync(
                (TestExecutionActivities activities) => activities.FinalizeTimedOutAsync(
                    executionId, "The execution did not complete in time."),
                FinalizeOptions);
            return "timedout";
        }
    }

    /// <summary>
    /// Cancellation surfaces wrapped (activity failure envelopes); walk the
    /// chain. When the workflow itself was cancelled, cancelled wins over any
    /// concurrent failure (§60: exactly one terminal state). A bare
    /// OperationCanceledException only counts when the workflow is actually
    /// cancelling, so timeouts still map to TimedOut.
    /// </summary>
    private static bool IsCancellation(Exception ex)
    {
        var cancelling = Workflow.CancellationToken.IsCancellationRequested;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is CanceledFailureException) return true;
            if (current is OperationCanceledException && cancelling) return true;
        }
        return cancelling;
    }

    private static Task CompensateAsync(Guid executionId, string reason)
        => Workflow.ExecuteActivityAsync(
            (TestExecutionActivities activities) => activities.FinalizeCancelledAsync(executionId, reason),
            FinalizeOptions);
}
