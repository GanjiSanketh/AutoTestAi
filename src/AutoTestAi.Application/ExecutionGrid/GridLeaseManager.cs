using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>
/// Best-effort lease release for terminal execution paths (Slice 9).
/// Terminal persistence always wins: release failures are logged and
/// the expired lease is later reaped opportunistically.
/// </summary>
public sealed class GridLeaseManager : IGridLeaseManager
{
    private readonly IGridAssignmentStore _assignments;
    private readonly IGridWorkerStore _workers;
    private readonly IExecutionStore _executions;
    private readonly ILogger<GridLeaseManager> _logger;

    public GridLeaseManager(
        IGridAssignmentStore assignments,
        IGridWorkerStore workers,
        IExecutionStore executions,
        ILogger<GridLeaseManager> logger)
    {
        _assignments = assignments;
        _workers = workers;
        _executions = executions;
        _logger = logger;
    }

    public async Task ReleaseForExecutionAsync(
        Guid executionId, string terminalStatus, CancellationToken ct)
    {
        try
        {
            var test = (await _executions.ListTestsByExecutionAsync(executionId, ct))
                .OrderBy(t => t.CreatedAt).FirstOrDefault();
            if (test is null)
                return;
            var assignment = await _assignments.FindActiveByTestAsync(test.Id, ct);
            if (assignment is null)
                return;
            assignment.Status = MapTerminal(terminalStatus);
            var worker = await _workers.GetByIdAsync(assignment.WorkerId, ct);
            if (worker is not null)
            {
                worker.ActiveAssignmentCount = Math.Max(0, worker.ActiveAssignmentCount - 1);
                worker.RowVersion++;
            }
            await _assignments.SaveChangesAsync(ct);
            _logger.LogInformation("Lease {AssignmentId} released as {Status} for execution {ExecutionId}.",
                assignment.Id, assignment.Status, executionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lease release for execution {ExecutionId} failed; reap will recover it.",
                executionId);
        }
    }

    public async Task ReleaseAssignmentAsync(
        Guid executionTestId, Guid assignmentId, Guid assignmentToken, string terminalStatus, CancellationToken ct)
    {
        try
        {
            var assignment = await _assignments.GetByIdAsync(assignmentId, ct);
            if (assignment is null)
                return;

            // Validate assignment token - this is the fencing mechanism
            if (assignment.AssignmentToken != assignmentToken)
            {
                _logger.LogWarning("Stale lease release attempt for assignment {AssignmentId}: token mismatch.", assignmentId);
                return;
            }

            // Validate that this is the active assignment for the execution test
            var activeAssignment = await _assignments.FindActiveByTestAsync(executionTestId, ct);
            if (activeAssignment is null || activeAssignment.Id != assignmentId)
            {
                _logger.LogWarning("Stale lease release attempt for execution test {ExecutionTestId}: no active assignment or assignment mismatch.", executionTestId);
                return;
            }

            if (!Enum.TryParse<GridAssignmentStatus>(terminalStatus, ignoreCase: true, out var terminal) ||
                ActiveLease.Contains(terminal) || terminal == GridAssignmentStatus.Pending)
                throw new ValidationException("Terminal lease status is invalid.",
                    new[] { new FieldError("status", "Lease status must be Completed, Released, Expired or Cancelled.") });

            assignment.Status = terminal;
            var worker = await _workers.GetByIdAsync(assignment.WorkerId, ct);
            if (worker is not null)
            {
                worker.ActiveAssignmentCount = Math.Max(0, worker.ActiveAssignmentCount - 1);
                worker.RowVersion++;
            }
            await _assignments.SaveChangesAsync(ct);
            _logger.LogInformation("Lease {AssignmentId} released as {Status} for execution test {ExecutionTestId}.",
                assignment.Id, assignment.Status, executionTestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lease release for assignment {AssignmentId} failed; reap will recover it.", assignmentId);
        }
    }

    private static GridAssignmentStatus MapTerminal(string terminalStatus)
        => terminalStatus switch
        {
            nameof(ExecutionStatus.Cancelled) => GridAssignmentStatus.Cancelled,
            nameof(ExecutionStatus.TimedOut) => GridAssignmentStatus.Expired,
            nameof(ExecutionStatus.Error) => GridAssignmentStatus.Released,
            _ => GridAssignmentStatus.Completed,
        };

    private static readonly IReadOnlySet<GridAssignmentStatus> ActiveLease =
        new HashSet<GridAssignmentStatus>
        {
            GridAssignmentStatus.Claimed,
            GridAssignmentStatus.Running,
        };
}
