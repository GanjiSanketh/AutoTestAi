using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Maintenance;

/// <summary>
/// Maintenance scan schedule management (Phase 4 Slice 10). One schedule per
/// project; Temporal owns firing while the database row owns cadence config
/// and lifecycle. Scheduled firing runs the existing deterministic scan core
/// through <see cref="IMaintenanceService.ScanAsSystemAsync"/> — it can only
/// create Proposed rows; approval and application stay human-gated.
/// </summary>
public sealed class MaintenanceScanScheduleService : IMaintenanceScanScheduleService
{
    private readonly IMaintenanceScanScheduleStore _schedules;
    private readonly IMaintenanceService _maintenance;
    private readonly IProjectStore _projects;
    private readonly IMaintenanceScanScheduleCoordinator _coordinator;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public MaintenanceScanScheduleService(
        IMaintenanceScanScheduleStore schedules,
        IMaintenanceService maintenance,
        IProjectStore projects,
        IMaintenanceScanScheduleCoordinator coordinator,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _schedules = schedules;
        _maintenance = maintenance;
        _projects = projects;
        _coordinator = coordinator;
        _authorization = authorization;
        _currentUser = currentUser;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<MaintenanceScanScheduleDto> CreateAsync(
        Guid projectId, CreateMaintenanceScanScheduleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);

        var errors = new List<FieldError>();
        var cron = ValidateCron(command.CronExpression, errors);
        var timeZone = ValidateTimeZone(command.TimeZoneId, errors);
        ValidationException.ThrowIfInvalid(errors);

        await RequireScannableProjectAsync(projectId, ct);

        var existing = await _schedules.GetByProjectIdAsync(projectId, ct);
        if (existing is { Status: not ScheduleStatus.Archived })
            throw new ConflictException("This project already has a maintenance scan schedule.");

        var now = _clock.UtcNow;
        var schedule = new MaintenanceScanSchedule
        {
            ProjectId = projectId,
            CronExpression = cron!,
            TimeZoneId = timeZone!,
            Status = ScheduleStatus.Active,
            CreatedBy = await ResolveAppUserIdAsync(ct),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // A previously archived row for the project is replaced, preserving
        // its audit history row while the new row carries the live cadence.
        if (existing is not null)
            await _schedules.RemoveAsync(existing, ct);

        await _schedules.AddAsync(schedule, ct);
        try
        {
            await _schedules.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueProjectConflict(ex))
        {
            // Concurrent creates converged on the one-per-project row: the
            // loser surfaces the same 409 duplicate the service check above
            // reports. Anything else (FK failures, connectivity) propagates
            // through the existing error mapping untouched.
            throw new ConflictException("This project already has a maintenance scan schedule.");
        }

        try
        {
            await _coordinator.CreateAsync(schedule.Id, DefinitionOf(schedule), ct);
        }
        catch
        {
            // Never leave an apparently-active row with no remote schedule:
            // compensate by removing the just-created row, then surface the
            // dependency failure (503 via existing conventions).
            await _schedules.RemoveAsync(schedule, ct);
            await _schedules.SaveChangesAsync(ct);
            await _audit.RecordAsync("maintenance.scan_failed", "maintenance_scan_schedule",
                schedule.Id.ToString(), projectId,
                SafeMeta(schedule.Id, projectId, schedule.CronExpression, schedule.TimeZoneId,
                    ScheduleStatus.Active.ToString(), "temporal_create_failed", null, null), ct);
            throw;
        }

        await _audit.RecordAsync("maintenance.scan_scheduled", "maintenance_scan_schedule",
            schedule.Id.ToString(), projectId,
            SafeMeta(schedule.Id, projectId, schedule.CronExpression, schedule.TimeZoneId,
                schedule.Status.ToString(), "created", null, null), ct);

        return Map(schedule, NextRunAt: null);
    }

    public async Task<MaintenanceScanScheduleDto?> GetByProjectAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesRead, ct);
        var schedule = await _schedules.GetByProjectIdAsync(projectId, ct);
        if (schedule is null || schedule.Status == ScheduleStatus.Archived)
            return null;
        return Map(schedule, NextRunAt: null);
    }

    public async Task<MaintenanceScanScheduleDto?> UpdateAsync(
        Guid projectId, UpdateMaintenanceScanScheduleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);

        var errors = new List<FieldError>();
        var cron = ValidateCron(command.CronExpression, errors);
        var timeZone = ValidateTimeZone(command.TimeZoneId, errors);
        ValidationException.ThrowIfInvalid(errors);

        var schedule = await RequireLiveScheduleAsync(projectId, ct);
        await RequireScannableProjectAsync(projectId, ct);

        // DB first (values restorable in memory), remote second; on remote
        // failure roll the row back so DB never claims a cadence Temporal
        // does not have.
        var prior = (schedule.CronExpression, schedule.TimeZoneId);
        schedule.CronExpression = cron!;
        schedule.TimeZoneId = timeZone!;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        try
        {
            await _coordinator.UpdateAsync(schedule.Id, DefinitionOf(schedule), ct);
        }
        catch
        {
            schedule.CronExpression = prior.CronExpression;
            schedule.TimeZoneId = prior.TimeZoneId;
            schedule.UpdatedAt = _clock.UtcNow;
            await _schedules.SaveChangesAsync(ct);
            throw;
        }

        await _audit.RecordAsync("maintenance.scan_scheduled", "maintenance_scan_schedule",
            schedule.Id.ToString(), projectId,
            SafeMeta(schedule.Id, projectId, schedule.CronExpression, schedule.TimeZoneId,
                schedule.Status.ToString(), "updated", null, null), ct);

        return Map(schedule, NextRunAt: null);
    }

    public async Task PauseAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);
        var schedule = await RequireLiveScheduleAsync(projectId, ct);

        // Remote first: only flip the row after Temporal confirms the pause.
        await _coordinator.PauseAsync(schedule.Id, "Paused by user.", ct);

        schedule.Status = ScheduleStatus.Disabled;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        await _audit.RecordAsync("maintenance.scan_scheduled", "maintenance_scan_schedule",
            schedule.Id.ToString(), projectId,
            SafeMeta(schedule.Id, projectId, schedule.CronExpression, schedule.TimeZoneId,
                schedule.Status.ToString(), "paused", null, null), ct);
    }

    public async Task ResumeAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);
        var schedule = await RequireLiveScheduleAsync(projectId, ct);
        await RequireScannableProjectAsync(projectId, ct);

        await _coordinator.ResumeAsync(schedule.Id, "Resumed by user.", ct);

        schedule.Status = ScheduleStatus.Active;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        await _audit.RecordAsync("maintenance.scan_scheduled", "maintenance_scan_schedule",
            schedule.Id.ToString(), projectId,
            SafeMeta(schedule.Id, projectId, schedule.CronExpression, schedule.TimeZoneId,
                schedule.Status.ToString(), "resumed", null, null), ct);
    }

    public async Task DeleteAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);
        var schedule = await _schedules.GetByProjectIdAsync(projectId, ct);
        if (schedule is null || schedule.Status == ScheduleStatus.Archived)
            return;

        // Remote delete is idempotent (missing = already gone).
        await _coordinator.DeleteAsync(schedule.Id, ct);

        schedule.Status = ScheduleStatus.Archived;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        await _audit.RecordAsync("maintenance.scan_scheduled", "maintenance_scan_schedule",
            schedule.Id.ToString(), projectId,
            SafeMeta(schedule.Id, projectId, schedule.CronExpression, schedule.TimeZoneId,
                schedule.Status.ToString(), "deleted", null, null), ct);
    }

    public async Task<DateTimeOffset?> GetNextRunAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesRead, ct);
        var schedule = await _schedules.GetByProjectIdAsync(projectId, ct);
        if (schedule is null || schedule.Status == ScheduleStatus.Archived)
            return null;
        return await _coordinator.GetNextRunAsync(schedule.Id, ct);
    }

    public async Task<MaintenanceScanResult> FireAsync(Guid scheduleId, CancellationToken ct)
    {
        // No authorization here: the Temporal activity carries only the
        // schedule id, and ownership is revalidated below against
        // authoritative persistence instead of trusting workflow input.
        var schedule = await _schedules.GetByIdAsync(scheduleId, ct)
            ?? throw new NotFoundException("Maintenance scan schedule not found.");

        try
        {
            if (schedule.Status != ScheduleStatus.Active)
                throw new ValidationException("The schedule is not active.",
                    [new FieldError("scheduleId", "Only active schedules can fire.")]);

            var project = await _projects.GetByIdAsync(schedule.ProjectId, ct)
                ?? throw new NotFoundException("Project not found.");
            if (project.Status == ProjectStatus.Archived)
                throw new ConflictException("Cannot scan an archived project.");

            var result = await _maintenance.ScanAsSystemAsync(schedule.ProjectId, ct);

            schedule.LastTriggeredAt = _clock.UtcNow;
            schedule.LastCandidatesDetected = result.CandidatesDetected;
            schedule.LastProposalsCreated = result.ProposalsCreated;
            schedule.UpdatedAt = _clock.UtcNow;
            await _schedules.SaveChangesAsync(ct);

            await _audit.RecordAsync("maintenance.scan_triggered", "maintenance_scan_schedule",
                schedule.Id.ToString(), schedule.ProjectId,
                SafeMeta(schedule.Id, schedule.ProjectId, schedule.CronExpression, schedule.TimeZoneId,
                    schedule.Status.ToString(), "scheduled",
                    result.CandidatesDetected, result.ProposalsCreated), ct);

            return result;
        }
        catch (Exception ex) when (ex is ValidationException or ConflictException or NotFoundException or ForbiddenException)
        {
            // Every deterministic fire rejection is audited so skipped ticks
            // stay visible; the Temporal activity treats these as terminal
            // for the tick (never retried).
            await _audit.RecordAsync("maintenance.scan_failed", "maintenance_scan_schedule",
                schedule.Id.ToString(), schedule.ProjectId,
                SafeMeta(schedule.Id, schedule.ProjectId, schedule.CronExpression, schedule.TimeZoneId,
                    schedule.Status.ToString(), "rejected",
                    null, null), ct);
            throw;
        }
    }

    // ---------- helpers ----------

    private async Task<MaintenanceScanSchedule> RequireLiveScheduleAsync(Guid projectId, CancellationToken ct)
    {
        var schedule = await _schedules.GetByProjectIdAsync(projectId, ct);
        if (schedule is null || schedule.Status == ScheduleStatus.Archived)
            throw new NotFoundException("Maintenance scan schedule not found.");
        return schedule;
    }

    private async Task RequireScannableProjectAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(projectId, ct)
            ?? throw new NotFoundException("Project not found.");
        if (project.Status == ProjectStatus.Archived)
            throw new ConflictException("Cannot schedule maintenance scans for an archived project.");
    }

    private static MaintenanceScanScheduleDefinition DefinitionOf(MaintenanceScanSchedule schedule)
        => new(
            schedule.CronExpression,
            schedule.TimeZoneId,
            Paused: schedule.Status != ScheduleStatus.Active);

    private static MaintenanceScanScheduleDto Map(MaintenanceScanSchedule schedule, DateTimeOffset? NextRunAt)
        => new(
            schedule.Id,
            schedule.ProjectId,
            schedule.CronExpression,
            schedule.TimeZoneId,
            schedule.Status.ToString(),
            schedule.LastTriggeredAt,
            schedule.LastCandidatesDetected,
            schedule.LastProposalsCreated,
            NextRunAt,
            schedule.CreatedAt,
            schedule.UpdatedAt);

    private static string? ValidateCron(string? cron, List<FieldError> errors)
    {
        var trimmed = cron?.Trim() ?? string.Empty;
        if (!CronValidation.IsPlausible(trimmed))
            errors.Add(new FieldError("cronExpression",
                "Cron expression must be a 5-field (minute hour day month weekday) or 6-field (leading seconds) expression."));
        else if (MaintenanceCronPolicy.IsMoreFrequentThanHourly(trimmed))
            errors.Add(new FieldError("cronExpression",
                "Maintenance scans run at most once per hour. Use a cron expression with a single minute value (for example '0 * * * *')."));
        return errors.Count == 0 ? trimmed : null;
    }

    private static string? ValidateTimeZone(string? timeZoneId, List<FieldError> errors)
    {
        var trimmed = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId.Trim();
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add(new FieldError("timeZoneId", $"Unknown timezone '{trimmed}'. Use an IANA identifier such as 'UTC' or 'America/New_York'."));
        }
        return errors.Count == 0 ? trimmed : null;
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);

    /// <summary>
    /// True when the failure is the one-per-project unique index rejecting a
    /// concurrent create. Both a uniqueness signal and this table's identity
    /// are required so foreign-key or connectivity failures are never
    /// mistranslated (Postgres reports SQLSTATE 23505 with the index name;
    /// other providers surface duplicate/unique wording).
    /// </summary>
    private static bool IsUniqueProjectConflict(Exception? ex)
    {
        while (ex is not null)
        {
            var message = ex.Message ?? string.Empty;
            var unique = message.Contains("unique", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("23505", StringComparison.Ordinal);
            // The table name appears in Postgres unique-violation messages
            // via the index name (IX_maintenance_scan_schedules_ProjectId);
            // foreign-key failures name the table without a uniqueness
            // signal and are never translated.
            var ours = message.Contains("maintenance_scan_schedules", StringComparison.OrdinalIgnoreCase);
            if (unique && ours)
                return true;
            ex = ex.InnerException;
        }
        return false;
    }

    private static string SafeMeta(
        Guid scheduleId, Guid projectId, string cron, string timeZone,
        string status, string reason, int? candidates, int? created)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            scheduleId,
            projectId,
            cron,
            timeZone,
            status,
            reason,
            candidateCount = candidates,
            createdCount = created,
            triggerSource = "schedule",
        }));
}
