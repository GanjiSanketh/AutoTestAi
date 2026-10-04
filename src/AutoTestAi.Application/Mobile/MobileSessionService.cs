using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Runtime ownership for Appium sessions (Slice 3C-4B-1).
/// Creating → Active → Closed, or Creating/Active → Orphaned on ownership loss.
/// Every fenced mutation verifies the caller still owns the execution:
/// project scope, session→assignment binding, active GridAssignment with a
/// matching AssignmentToken, and current slot ownership (ClaimToken stays
/// server-side; the worker never sees it). Stale callers get ConflictException.
/// No public HTTP surface: consumed by the execution/scheduler internals.
/// </summary>
public interface IMobileSessionService
{
    Task<MobileDeviceSession> CreateAsync(Guid projectId, Guid deviceId, Guid slotId, Guid executionId, Guid assignmentId, CancellationToken ct);
    Task<MobileDeviceSession> ActivateAsync(Guid projectId, Guid sessionId, Guid assignmentId, Guid assignmentToken, string appiumSessionId, CancellationToken ct);
    Task HeartbeatAsync(Guid projectId, Guid sessionId, Guid assignmentId, Guid assignmentToken, CancellationToken ct);
    Task CloseAsync(Guid projectId, Guid sessionId, Guid assignmentId, Guid assignmentToken, CancellationToken ct);
    /// <summary>
    /// Best-effort terminal-path close (Slice 3C-4B-2): closes the session
    /// bound to an assignment, if any. Idempotent; throws ConflictException
    /// when ownership moved on so callers can swallow stale closes.
    /// </summary>
    Task CloseForAssignmentAsync(Guid projectId, Guid assignmentId, Guid assignmentToken, CancellationToken ct);
    Task<int> ReapStaleAsync(DateTimeOffset staleBefore, int take, CancellationToken ct);
}

public sealed class MobileSessionService : IMobileSessionService
{
    private static readonly HashSet<GridAssignmentStatus> ActiveLease = new()
    {
        GridAssignmentStatus.Claimed,
        GridAssignmentStatus.Running,
    };

    private readonly IMobileRegistryStore _sessions;
    private readonly IGridAssignmentStore _assignments;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<MobileSessionService> _logger;

    public MobileSessionService(
        IMobileRegistryStore sessions,
        IGridAssignmentStore assignments,
        IDateTimeProvider clock,
        ILogger<MobileSessionService> logger)
    {
        _sessions = sessions;
        _assignments = assignments;
        _clock = clock;
        _logger = logger;
    }

    public async Task<MobileDeviceSession> CreateAsync(Guid projectId, Guid deviceId, Guid slotId, Guid executionId, Guid assignmentId, CancellationToken ct)
    {
        var slot = await _sessions.GetSlotByIdAsync(slotId, ct)
            ?? throw new NotFoundException("Mobile slot not found.");
        if (slot.ProjectId != projectId || slot.DeviceId != deviceId)
            throw new ConflictException("Mobile session slot ownership mismatch.");
        if (slot.AssignmentId != assignmentId)
            throw new ConflictException("Mobile slot is not bound to this assignment.");
        var existing = await _sessions.FindSessionByAssignmentAsync(assignmentId, ct);
        if (existing is not null && existing.ProjectId == projectId)
            throw new ConflictException("A mobile session already exists for this assignment.");

        var now = _clock.UtcNow;
        var session = new MobileDeviceSession
        {
            ProjectId = projectId,
            DeviceId = deviceId,
            DeviceSlotId = slotId,
            ExecutionId = executionId,
            AssignmentId = assignmentId,
            Status = MobileSessionStatus.Creating,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _sessions.AddSessionAsync(session, ct);
        await _sessions.SaveChangesAsync(ct);
        _logger.LogInformation("Mobile session {SessionId} created for assignment {AssignmentId}.", session.Id, assignmentId);
        return session;
    }

    public async Task<MobileDeviceSession> ActivateAsync(Guid projectId, Guid sessionId, Guid assignmentId, Guid assignmentToken, string appiumSessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appiumSessionId) || appiumSessionId.Trim().Length > 200)
            throw new ValidationException("Appium session id is invalid.", new[] { new FieldError("appiumSessionId", "Appium session id must be 1-200 characters.") });
        var session = await LoadFencedAsync(projectId, sessionId, assignmentId, assignmentToken, ct);
        if (session.Status == MobileSessionStatus.Closed)
            throw new ConflictException("Mobile session is already closed.");
        if (session.Status == MobileSessionStatus.Orphaned)
            throw new ConflictException("Mobile session was orphaned after ownership loss.");
        var now = _clock.UtcNow;
        session.AppiumSessionId = appiumSessionId.Trim();
        session.Status = MobileSessionStatus.Active;
        session.StartedAt ??= now;
        session.LastHeartbeatAt = now;
        session.UpdatedAt = now;
        await _sessions.SaveChangesAsync(ct);
        _logger.LogInformation("Mobile session {SessionId} activated for assignment {AssignmentId}.", session.Id, assignmentId);
        return session;
    }

    public async Task HeartbeatAsync(Guid projectId, Guid sessionId, Guid assignmentId, Guid assignmentToken, CancellationToken ct)
    {
        var session = await LoadFencedAsync(projectId, sessionId, assignmentId, assignmentToken, ct);
        if (session.Status is MobileSessionStatus.Closed or MobileSessionStatus.Orphaned)
            throw new ConflictException($"Mobile session is {session.Status} and cannot heartbeat.");
        var now = _clock.UtcNow;
        session.LastHeartbeatAt = now;
        session.UpdatedAt = now;
        await _sessions.SaveChangesAsync(ct);
    }

    public async Task CloseAsync(Guid projectId, Guid sessionId, Guid assignmentId, Guid assignmentToken, CancellationToken ct)
    {
        var session = await LoadFencedAsync(projectId, sessionId, assignmentId, assignmentToken, ct);
        if (session.Status == MobileSessionStatus.Closed)
            return; // idempotent
        if (session.Status == MobileSessionStatus.Orphaned)
            throw new ConflictException("Mobile session was orphaned after ownership loss.");
        var now = _clock.UtcNow;
        session.Status = MobileSessionStatus.Closed;
        session.ClosedAt = now;
        session.UpdatedAt = now;
        await _sessions.SaveChangesAsync(ct);
        _logger.LogInformation("Mobile session {SessionId} closed for assignment {AssignmentId}.", session.Id, assignmentId);
    }

    public async Task CloseForAssignmentAsync(Guid projectId, Guid assignmentId, Guid assignmentToken, CancellationToken ct)
    {
        var session = await _sessions.FindSessionByAssignmentAsync(assignmentId, ct);
        if (session is null)
            return; // idempotent: no runtime session was ever created
        if (session.ProjectId != projectId)
            throw new NotFoundException("Mobile session not found.");
        await CloseAsync(projectId, session.Id, assignmentId, assignmentToken, ct);
    }

    public async Task<int> ReapStaleAsync(DateTimeOffset staleBefore, int take, CancellationToken ct)    {
        var stale = await _sessions.ListStaleSessionsAsync(staleBefore, Math.Clamp(take, 1, 100), ct);
        var orphaned = 0;
        foreach (var row in stale)
        {
            var session = await _sessions.GetSessionByIdAsync(row.Id, ct);
            if (session is null || session.Status is MobileSessionStatus.Closed or MobileSessionStatus.Orphaned)
                continue;
            // Only orphan when the slot no longer proves current ownership;
            // a still-bound slot keeps its session (heartbeat resumes via renew).
            var slot = await _sessions.GetSlotByIdAsync(session.DeviceSlotId, ct);
            if (slot is not null && slot.ProjectId == session.ProjectId && slot.AssignmentId == session.AssignmentId)
                continue;
            session.Status = MobileSessionStatus.Orphaned;
            session.UpdatedAt = _clock.UtcNow;
            try
            {
                await _sessions.SaveChangesAsync(ct);
                orphaned++;
                _logger.LogWarning("Mobile session {SessionId} orphaned after stale heartbeat.", session.Id);
            }
            catch (ConflictException ex)
            {
                _logger.LogWarning(ex, "Mobile session {SessionId} changed concurrently during orphan sweep.", session.Id);
            }
        }
        return orphaned;
    }

    private async Task<MobileDeviceSession> LoadFencedAsync(Guid projectId, Guid sessionId, Guid assignmentId, Guid assignmentToken, CancellationToken ct)
    {
        var session = await _sessions.GetSessionByIdAsync(sessionId, ct)
            ?? throw new NotFoundException("Mobile session not found.");
        if (session.ProjectId != projectId)
            throw new NotFoundException("Mobile session not found.");
        if (session.AssignmentId != assignmentId)
            throw new ConflictException("Mobile session belongs to a different assignment.");
        if (session.ExecutionId is null)
            throw new ConflictException("Mobile session has no execution binding.");
        var assignment = await _assignments.GetByIdAsync(assignmentId, ct)
            ?? throw new ConflictException("Assignment lease is no longer active.");
        if (!ActiveLease.Contains(assignment.Status))
            throw new ConflictException($"Assignment is {assignment.Status} and no longer owns the session.");
        if (assignment.AssignmentToken != assignmentToken)
            throw new ConflictException("Stale assignment token; session ownership was reassigned.");
        if (assignment.ExecutionId != session.ExecutionId)
            throw new ConflictException("Session execution binding mismatch.");
        var slot = await _sessions.GetSlotByIdAsync(session.DeviceSlotId, ct)
            ?? throw new ConflictException("Mobile slot is no longer available.");
        if (slot.ProjectId != projectId || slot.AssignmentId != assignmentId)
            throw new ConflictException("Mobile slot ownership changed; session is stale.");
        return session;
    }
}
