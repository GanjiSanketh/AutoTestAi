using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Executions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Infrastructure.ExecutionGrid;

/// <summary>
/// Grid-aware <see cref="IMobileWorkerClient"/> (Phase 3 Slice 3C-4B-2).
/// Thin transport over the lease the coordinator claimed: resolves the
/// owning worker per call, renews the lease while polling (which piggybacks
/// the linked slot lease and session heartbeat), and releases on abort.
/// The coordinator owns claim/build/session/outcome; this client never builds
/// assignments, never touches sessions, and never sees ClaimToken values.
/// </summary>
public sealed class GridAppiumWorkerClient : IMobileWorkerClient
{
    private readonly MobileWorkerTransport _transport;
    private readonly IGridScheduler _scheduler;
    private readonly IGridWorkerStore _workers;
    private readonly IGridAssignmentStore _assignments;
    private readonly IExecutionEventPublisher _events;
    private readonly IOptions<GridOptions> _grid;
    private readonly IOptions<WorkerOptions> _worker;
    private readonly ILogger<GridAppiumWorkerClient> _logger;

    public GridAppiumWorkerClient(
        MobileWorkerTransport transport,
        IGridScheduler scheduler,
        IGridWorkerStore workers,
        IGridAssignmentStore assignments,
        IExecutionEventPublisher events,
        IOptions<GridOptions> grid,
        IOptions<WorkerOptions> worker,
        ILogger<GridAppiumWorkerClient> logger)
    {
        _transport = transport;
        _scheduler = scheduler;
        _workers = workers;
        _assignments = assignments;
        _events = events;
        _grid = grid;
        _worker = worker;
        _logger = logger;
    }

    public async Task<string> StartAssignmentAsync(MobileAssignmentDto assignment, CancellationToken ct)
    {
        // The coordinator claimed the lease first (device-specific
        // capabilities require the claimed slot); resolve it by the
        // worker assignment reference, which is the test id hex.
        var lease = await _assignments.FindActiveByRefAsync(assignment.AssignmentId, ct)
            ?? throw new WorkerInfrastructureException("The mobile assignment lease is not active.");
        var worker = await _workers.GetByIdAsync(lease.WorkerId, ct)
            ?? throw new WorkerInfrastructureException("The assigned Appium worker is no longer registered.");
        var workerAssignmentId = await _transport.StartAssignmentAsync(
            worker.BaseUrl, _worker.Value.ApiToken,
            _worker.Value.RequestTimeoutSeconds, assignment, ct,
            lease.AssignmentToken);
        await MarkRunningAsync(lease.Id, ct);
        await PublishAsync(lease.ExecutionId, ExecutionEvents.ExecutionAssigned,
            new
            {
                executionId = lease.ExecutionId,
                workerId = worker.Id,
                workerKey = worker.WorkerKey,
                assignmentId = lease.Id,
            }, ct);
        return workerAssignmentId;
    }

    public async Task<MobileAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct)
    {
        var lease = await _assignments.FindActiveByRefAsync(assignmentId, ct)
            ?? throw new WorkerInfrastructureException("The mobile assignment lease is no longer active.");
        var worker = await _workers.GetByIdAsync(lease.WorkerId, ct)
            ?? throw new WorkerInfrastructureException("The assigned Appium worker is no longer registered.");
        await RenewIfNeededAsync(lease, ct);
        return await _transport.GetAssignmentAsync(
            worker.BaseUrl, _worker.Value.ApiToken,
            _worker.Value.RequestTimeoutSeconds, assignmentId, ct);
    }

    public async Task CancelAssignmentAsync(string assignmentId, CancellationToken ct)
    {
        var lease = await _assignments.FindActiveByRefAsync(assignmentId, ct);
        if (lease is null)
            return; // nothing active to abort; release is idempotent downstream
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
                _logger.LogWarning(ex, "Appium worker abort for assignment {AssignmentId} failed; continuing with release.",
                    assignmentId);
            }
        }
        await _scheduler.ReleaseLeaseAsync(
            lease.ExecutionTestId, nameof(GridAssignmentStatus.Cancelled), ct);
    }

    // ---------- internals (mirror GridPlaywrightWorkerClient) ----------

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
            _logger.LogWarning(ex, "Mobile lease {AssignmentId} changed concurrently while marking running.", assignmentId);
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
            // Lease is gone: surface as infrastructure failure so the
            // coordinator retries with a fresh claim instead of polling dead.
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
