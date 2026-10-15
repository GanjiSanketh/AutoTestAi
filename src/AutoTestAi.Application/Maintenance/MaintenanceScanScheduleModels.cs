using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Maintenance;

/// <summary>
/// Maintenance scan schedule for read views (Phase 4 Slice 10). One row per
/// project. NextRunAt is populated only on explicit next-run retrieval
/// (single Temporal describe); ordinary reads never pay a Temporal RPC.
/// </summary>
public sealed record MaintenanceScanScheduleDto(
    Guid Id,
    Guid ProjectId,
    string CronExpression,
    string TimeZoneId,
    string Status,
    DateTimeOffset? LastTriggeredAt,
    int? LastCandidatesDetected,
    int? LastProposalsCreated,
    DateTimeOffset? NextRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Schedule creation input. Project identity comes from the route.</summary>
public sealed record CreateMaintenanceScanScheduleCommand(
    string CronExpression,
    string? TimeZoneId);

/// <summary>Schedule update input (cadence only).</summary>
public sealed record UpdateMaintenanceScanScheduleCommand(
    string CronExpression,
    string? TimeZoneId);

/// <summary>
/// Cron/overlap portion of a Temporal schedule definition, free of SDK types
/// so Application never references Temporal. Overlap is always Skip for
/// maintenance scans: concurrent scans of one project are never allowed.
/// </summary>
public sealed record MaintenanceScanScheduleDefinition(
    string CronExpression,
    string TimeZoneId,
    bool Paused);

/// <summary>
/// Persistence seam for the one-per-project maintenance scan schedule.
/// Methods do NOT authorize — <see cref="IMaintenanceScanScheduleService"/>
/// enforces that.
/// </summary>
public interface IMaintenanceScanScheduleStore
{
    Task<MaintenanceScanSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct);

    Task<MaintenanceScanSchedule?> GetByProjectIdAsync(Guid projectId, CancellationToken ct);

    Task AddAsync(MaintenanceScanSchedule schedule, CancellationToken ct);

    Task RemoveAsync(MaintenanceScanSchedule schedule, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Durable-scheduling boundary (Phase 4 Slice 10). Implemented with Temporal
/// Schedules in the Workflows layer; fakes back the API in tests.
/// Remote-missing reads/deletes are idempotent (null / no-op); remote
/// validation failures surface as <see cref="Common.ValidationException"/>;
/// unconfigured/remote failures surface as <see cref="InvalidOperationException"/>
/// (existing 503 dependency convention). Never throws Temporal SDK types.
/// </summary>
public interface IMaintenanceScanScheduleCoordinator
{
    bool IsConfigured { get; }

    Task CreateAsync(Guid scheduleId, MaintenanceScanScheduleDefinition definition, CancellationToken ct);

    Task UpdateAsync(Guid scheduleId, MaintenanceScanScheduleDefinition definition, CancellationToken ct);

    Task PauseAsync(Guid scheduleId, string note, CancellationToken ct);

    Task ResumeAsync(Guid scheduleId, string note, CancellationToken ct);

    Task DeleteAsync(Guid scheduleId, CancellationToken ct);

    /// <summary>
    /// Next scheduled fire, or null when the remote schedule is missing,
    /// paused with no upcoming action, or Temporal is unavailable.
    /// Called only on explicit next-run retrieval, never on ordinary reads.
    /// </summary>
    Task<DateTimeOffset?> GetNextRunAsync(Guid scheduleId, CancellationToken ct);
}

/// <summary>
/// Maintenance scan schedule management (Phase 4 Slice 10). Every method
/// enforces server-side authorization through schedule → project. The
/// database row owns cadence config and lifecycle; Temporal owns firing.
/// Neither system is assumed atomic with the other: mutating operations
/// order remote/DB writes so partial failures stay detectable and never
/// claim success they did not achieve.
/// </summary>
public interface IMaintenanceScanScheduleService
{
    Task<MaintenanceScanScheduleDto> CreateAsync(Guid projectId, CreateMaintenanceScanScheduleCommand command, CancellationToken ct);

    Task<MaintenanceScanScheduleDto?> GetByProjectAsync(Guid projectId, CancellationToken ct);

    Task<MaintenanceScanScheduleDto?> UpdateAsync(Guid projectId, UpdateMaintenanceScanScheduleCommand command, CancellationToken ct);

    Task PauseAsync(Guid projectId, CancellationToken ct);

    Task ResumeAsync(Guid projectId, CancellationToken ct);

    Task DeleteAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Explicit next-run retrieval: at most one Temporal Describe call.
    /// Returns null when no schedule exists or the remote state is unknown.
    /// </summary>
    Task<DateTimeOffset?> GetNextRunAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Shared fire path used by the Temporal activity. Reloads the schedule
    /// and project from authoritative persistence, fails closed unless the
    /// schedule is Active and the project exists and is not archived, then
    /// runs the existing scan core. No authorization: the activity validates
    /// ownership itself instead of trusting workflow input beyond the
    /// schedule id. Never approves, rejects, applies, or mutates versions.
    /// </summary>
    Task<MaintenanceScanResult> FireAsync(Guid scheduleId, CancellationToken ct);
}
