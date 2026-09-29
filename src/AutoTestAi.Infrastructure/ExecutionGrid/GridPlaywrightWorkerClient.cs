using System.Diagnostics;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Executions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Infrastructure.ExecutionGrid;

/// <summary>
/// Grid-aware <see cref="IPlaywrightWorkerClient"/> (Phase 2 Slice 9).
/// Claims capacity leases before dispatching, renews them while polling,
/// and releases them on abort. The engine keeps its Slice 5 contract
/// untouched: one execution still runs sequential steps on one worker;
/// parallelism comes from many executions leasing many workers.
///
/// Legacy fallback: with no workers registered and a configured
/// <c>Worker:BaseUrl</c>, calls pass straight through (Slice 5 behavior).
/// </summary>
public sealed class GridPlaywrightWorkerClient : IPlaywrightWorkerClient
{
    private readonly PlaywrightWorkerClient _legacy;
    private readonly WorkerHttpTransport _transport;
    private readonly IGridScheduler _scheduler;
    private readonly IGridWorkerStore _workers;
    private readonly IGridAssignmentStore _assignments;
    private readonly IExecutionEventPublisher _events;
    private readonly IOptions<GridOptions> _grid;
    private readonly IOptions<WorkerOptions> _worker;
    private readonly IOptions<ExecutionOptions> _execution;
    private readonly ILogger<GridPlaywrightWorkerClient> _logger;

    public GridPlaywrightWorkerClient(
        PlaywrightWorkerClient legacy,
        WorkerHttpTransport transport,
        IGridScheduler scheduler,
        IGridWorkerStore workers,
        IGridAssignmentStore assignments,
        IExecutionEventPublisher events,
        IOptions<GridOptions> grid,
        IOptions<WorkerOptions> worker,
        IOptions<ExecutionOptions> execution,
        ILogger<GridPlaywrightWorkerClient> logger)
    {
        _legacy = legacy;
        _transport = transport;
        _scheduler = scheduler;
        _workers = workers;
        _assignments = assignments;
        _events = events;
        _grid = grid;
        _worker = worker;
        _execution = execution;
        _logger = logger;
    }

    public async Task<string> StartAssignmentAsync(WorkerAssignmentDto assignment, CancellationToken ct)
    {
        if (!Guid.TryParse(assignment.ExecutionId, out var executionId) ||
            !Guid.TryParse(assignment.AssignmentId, out _))
        {
            // Not a grid-shaped assignment id: legacy direct dispatch.
            return await _legacy.StartAssignmentAsync(assignment, ct);
        }
        if ((await _workers.ListAsync(ct)).Count == 0 && _worker.Value.Configured)
            return await _legacy.StartAssignmentAsync(assignment, ct);

        var excluded = new HashSet<Guid>();
        var waited = Stopwatch.StartNew();
        var queuedEventSent = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            GridClaim? claim = null;
            try
            {
                claim = await _scheduler.TryClaimAsync(executionId, excluded, ct);
            }
            catch (Application.Common.ConflictException ex)
            {
                // Unclaimable (terminal/missing): fail fast, never queue forever.
                throw new WorkerInfrastructureException(ex.Message, ex) { IsRetryable = false };
            }
            if (claim is not null)
            {
                try
                {
                    var workerAssignmentId = await _transport.StartAssignmentAsync(
                        claim.Worker.BaseUrl, _worker.Value.ApiToken,
                        _worker.Value.RequestTimeoutSeconds, assignment, ct,
                        claim.AssignmentToken);
                    await MarkRunningAsync(claim.Assignment.Id, ct);
                    await PublishAsync(executionId, ExecutionEvents.ExecutionAssigned,
                        new
                        {
                            executionId,
                            workerId = claim.Worker.Id,
                            workerKey = claim.Worker.WorkerKey,
                            assignmentId = claim.Assignment.Id,
                        }, ct);
                    return workerAssignmentId;
                }
                catch (Exception ex) when (ex is WorkerInfrastructureException or OperationCanceledException)
                {
                    // Dispatch failed: free the lease so another worker can take
                    // it, exclude this worker for this dispatch, and continue.
                    excluded.Add(claim.Worker.Id);
                    await _scheduler.ReleaseLeaseAsync(
                        claim.Assignment.ExecutionTestId,
                        nameof(GridAssignmentStatus.Released), ct);
                    if (ex is OperationCanceledException && ct.IsCancellationRequested)
                        throw;
                    _logger.LogWarning(ex, "Dispatch to worker {WorkerId} failed; trying next worker.",
                        claim.Worker.Id);
                }
            }
            else
            {
                if (!queuedEventSent)
                {
                    queuedEventSent = true;
                    await PublishAsync(executionId, ExecutionEvents.ExecutionQueued,
                        new { executionId, reason = "waiting-for-grid-capacity" }, ct);
                    _logger.LogInformation("Execution {ExecutionId} queued waiting for grid capacity.", executionId);
                }
            }

            if (waited.Elapsed >= _grid.Value.QueueWaitTimeout)
                throw new WorkerInfrastructureException(
                    "No execution capacity became available before the queue timeout.");
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_execution.Value.WorkerPollIntervalSeconds, 1, 30)), ct);
        }
    }

    public async Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct)
    {
        var lease = await _assignments.FindActiveByRefAsync(assignmentId, ct);
        if (lease is null)
            return await _legacy.GetAssignmentAsync(assignmentId, ct);
        var worker = await _workers.GetByIdAsync(lease.WorkerId, ct);
        if (worker is null)
            throw new WorkerInfrastructureException("The assigned worker is no longer registered.");
        await RenewIfNeededAsync(lease, ct);
        return await _transport.GetAssignmentAsync(
            worker.BaseUrl, _worker.Value.ApiToken,
            _worker.Value.RequestTimeoutSeconds, assignmentId, ct);
    }

    public async Task CancelAssignmentAsync(string assignmentId, CancellationToken ct)
    {
        var lease = await _assignments.FindActiveByRefAsync(assignmentId, ct);
        if (lease is null)
        {
            await _legacy.CancelAssignmentAsync(assignmentId, ct);
            return;
        }
        var worker = await _workers.GetByIdAsync(lease.WorkerId, ct);
        if (worker is not null)
        {
            try
            {
                await _transport.CancelAssignmentAsync(
                    worker.BaseUrl, _worker.Value.ApiToken,
                    _worker.Value.RequestTimeoutSeconds, assignmentId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Worker abort for assignment {AssignmentId} failed; continuing with release.",
                    assignmentId);
            }
        }
        await _scheduler.ReleaseLeaseAsync(
            lease.ExecutionTestId, nameof(GridAssignmentStatus.Cancelled), ct);
    }

    // ---------- internals ----------

    private async Task MarkRunningAsync(Guid assignmentId, CancellationToken ct)
    {
        var lease = await _assignments.GetByIdAsync(assignmentId, ct);
        if (lease is null || lease.Status != GridAssignmentStatus.Claimed)
            return; // idempotent: already advanced or reaped
        lease.Status = GridAssignmentStatus.Running;
        try
        {
            await _assignments.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lease {AssignmentId} changed concurrently while marking running.", assignmentId);
        }
    }

    private async Task RenewIfNeededAsync(
        AutoTestAi.Domain.Entities.GridAssignment lease, CancellationToken ct)
    {
        var remaining = lease.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining > _grid.Value.LeaseDuration / 2)
            return;
        try
        {
            await _scheduler.RenewLeaseAsync(lease.Id, ct);
        }
        catch (Application.Common.ConflictException ex)
        {
            // Lease is gone: surface as infrastructure failure so the engine
            // retries with a fresh claim instead of polling a dead lease.
            throw new WorkerInfrastructureException(ex.Message, ex);
        }
    }

    private async Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
    {
        try
        {
            await _events.PublishAsync(executionId, eventName, payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Grid event {Event} for execution {ExecutionId} was not published.",
                eventName, executionId);
        }
    }
}
