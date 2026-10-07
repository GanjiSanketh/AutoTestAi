using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using System.Text.Json;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Suite schedule management (Phase 4 Slice 9B). Every method enforces
/// server-side authorization through schedule → suite → project. The
/// database row owns cadence config and lifecycle; Temporal owns firing.
/// Neither system is assumed atomic with the other: mutating operations
/// order remote/DB writes so partial failures stay detectable and never
/// claim success they did not achieve.
/// </summary>
public sealed class SuiteScheduleService : ISuiteScheduleService
{
    /// <summary>Manual Run Now client keys must fit the schedule base budget (60 - 39).</summary>
    private const int MaxRunNowKeyLength = 21;

    private readonly ISuiteScheduleStore _schedules;
    private readonly ISuiteStore _suites;
    private readonly ISuiteExecutionService _executions;
    private readonly ISuiteScheduleCoordinator _coordinator;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public SuiteScheduleService(
        ISuiteScheduleStore schedules,
        ISuiteStore suites,
        ISuiteExecutionService executions,
        ISuiteScheduleCoordinator coordinator,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _schedules = schedules;
        _suites = suites;
        _executions = executions;
        _coordinator = coordinator;
        _authorization = authorization;
        _currentUser = currentUser;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<SuiteScheduleDto> CreateAsync(Guid projectId, Guid suiteId, CreateSuiteScheduleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);

        var errors = new List<FieldError>();
        var name = ValidateName(command.Name, errors);
        var cron = ValidateCron(command.CronExpression, errors);
        var timeZone = ValidateTimeZone(command.TimeZoneId, errors);
        var overlap = ValidateOverlap(command.OverlapPolicy, errors);
        ValidationException.ThrowIfInvalid(errors);

        var suite = await _suites.GetByIdRawAsync(suiteId, ct)
            ?? throw new NotFoundException("Test suite not found.");
        if (suite.ProjectId != projectId)
            throw new ForbiddenException("The suite does not belong to this project.");
        if (suite.Status == ProjectStatus.Archived)
            throw new ConflictException("Cannot schedule an archived suite.");

        if (await _schedules.ExistsWithNameAsync(projectId, name!, null, ct))
            throw new ConflictException($"A schedule with the name '{name}' already exists in this project.");

        var now = _clock.UtcNow;
        var schedule = new TestSuiteSchedule
        {
            ProjectId = projectId,
            SuiteId = suiteId,
            Name = name!,
            CronExpression = cron!,
            TimeZoneId = timeZone!,
            Status = ScheduleStatus.Active,
            OverlapPolicy = overlap!.Value,
            CreatedBy = await ResolveAppUserIdAsync(ct),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _schedules.AddAsync(schedule, ct);
        await _schedules.SaveChangesAsync(ct);

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
            await _audit.RecordAsync("suite.schedule_failed", "test_suite_schedule",
                schedule.Id.ToString(), projectId,
                JsonSerializer.Serialize(new { scheduleId = schedule.Id, suiteId, reason = "temporal_create_failed" }), ct);
            throw;
        }

        await _audit.RecordAsync("suite.schedule_created", "test_suite_schedule",
            schedule.Id.ToString(), projectId,
            JsonSerializer.Serialize(new
            {
                scheduleId = schedule.Id,
                suiteId,
                name = schedule.Name,
                cron = schedule.CronExpression,
                timeZone = schedule.TimeZoneId,
                overlap = schedule.OverlapPolicy.ToString(),
            }), ct);

        return Map(schedule, suite.Name, NextRunAt: null);
    }

    public async Task<IReadOnlyList<SuiteScheduleDto>> ListBySuiteAsync(Guid projectId, Guid suiteId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesRead, ct);

        var suite = await _suites.GetByIdRawAsync(suiteId, ct)
            ?? throw new NotFoundException("Test suite not found.");
        if (suite.ProjectId != projectId)
            throw new ForbiddenException("The suite does not belong to this project.");

        var rows = await _schedules.ListBySuiteAsync(suiteId, includeArchived: false, ct);
        return rows.Select(s => Map(s, suite.Name, NextRunAt: null)).ToList();
    }

    public async Task<SuiteScheduleDto?> GetByIdAsync(Guid scheduleId, CancellationToken ct)
    {
        var schedule = await RequireScheduleAsync(scheduleId, Permissions.TestCasesRead, ct);
        var suite = await _suites.GetByIdRawAsync(schedule.SuiteId, ct);
        var nextRun = await NextRunBestEffortAsync(schedule.Id, ct);
        return Map(schedule, suite?.Name ?? string.Empty, nextRun);
    }

    public async Task<SuiteScheduleDto?> UpdateAsync(Guid scheduleId, UpdateSuiteScheduleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var schedule = await RequireScheduleAsync(scheduleId, Permissions.TestCasesManage, ct);

        var errors = new List<FieldError>();
        var name = ValidateName(command.Name, errors);
        var cron = ValidateCron(command.CronExpression, errors);
        var timeZone = ValidateTimeZone(command.TimeZoneId, errors);
        var overlap = ValidateOverlap(command.OverlapPolicy, errors);
        ValidationException.ThrowIfInvalid(errors);

        if (schedule.Status == ScheduleStatus.Archived)
            throw new ConflictException("Archived schedules cannot be modified.");

        if (await _schedules.ExistsWithNameAsync(schedule.ProjectId, name!, scheduleId, ct))
            throw new ConflictException($"A schedule with the name '{name}' already exists in this project.");

        // DB first (values restorable in memory), remote second; on remote
        // failure roll the row back so DB never claims a cadence Temporal
        // does not have.
        var prior = (schedule.Name, schedule.CronExpression, schedule.TimeZoneId, schedule.OverlapPolicy);
        schedule.Name = name!;
        schedule.CronExpression = cron!;
        schedule.TimeZoneId = timeZone!;
        schedule.OverlapPolicy = overlap!.Value;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        try
        {
            await _coordinator.UpdateAsync(schedule.Id, DefinitionOf(schedule), ct);
        }
        catch
        {
            schedule.Name = prior.Name;
            schedule.CronExpression = prior.CronExpression;
            schedule.TimeZoneId = prior.TimeZoneId;
            schedule.OverlapPolicy = prior.OverlapPolicy;
            schedule.UpdatedAt = _clock.UtcNow;
            await _schedules.SaveChangesAsync(ct);
            throw;
        }

        var suite = await _suites.GetByIdRawAsync(schedule.SuiteId, ct);
        await _audit.RecordAsync("suite.schedule_updated", "test_suite_schedule",
            schedule.Id.ToString(), schedule.ProjectId,
            JsonSerializer.Serialize(new
            {
                scheduleId = schedule.Id,
                suiteId = schedule.SuiteId,
                name = schedule.Name,
                cron = schedule.CronExpression,
                timeZone = schedule.TimeZoneId,
                overlap = schedule.OverlapPolicy.ToString(),
            }), ct);

        return Map(schedule, suite?.Name ?? string.Empty, NextRunAt: null);
    }

    public async Task PauseAsync(Guid scheduleId, CancellationToken ct)
    {
        var schedule = await RequireScheduleAsync(scheduleId, Permissions.TestCasesManage, ct);
        if (schedule.Status == ScheduleStatus.Archived)
            throw new ConflictException("Archived schedules cannot be paused.");

        // Remote first: only flip the row after Temporal confirms the pause.
        await _coordinator.PauseAsync(schedule.Id, "Paused by user.", ct);

        schedule.Status = ScheduleStatus.Disabled;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.schedule_paused", "test_suite_schedule",
            schedule.Id.ToString(), schedule.ProjectId,
            JsonSerializer.Serialize(new { scheduleId = schedule.Id, suiteId = schedule.SuiteId }), ct);
    }

    public async Task ResumeAsync(Guid scheduleId, CancellationToken ct)
    {
        var schedule = await RequireScheduleAsync(scheduleId, Permissions.TestCasesManage, ct);
        if (schedule.Status == ScheduleStatus.Archived)
            throw new ConflictException("Archived schedules cannot be resumed.");

        var suite = await _suites.GetByIdRawAsync(schedule.SuiteId, ct)
            ?? throw new NotFoundException("Test suite not found.");
        if (suite.Status == ProjectStatus.Archived)
            throw new ConflictException("Cannot resume a schedule whose suite is archived.");

        await _coordinator.ResumeAsync(schedule.Id, "Resumed by user.", ct);

        schedule.Status = ScheduleStatus.Active;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.schedule_resumed", "test_suite_schedule",
            schedule.Id.ToString(), schedule.ProjectId,
            JsonSerializer.Serialize(new { scheduleId = schedule.Id, suiteId = schedule.SuiteId }), ct);
    }

    public async Task ArchiveAsync(Guid scheduleId, CancellationToken ct)
    {
        var schedule = await RequireScheduleAsync(scheduleId, Permissions.TestCasesManage, ct);
        if (schedule.Status == ScheduleStatus.Archived)
            return;

        // Remote delete is idempotent (missing = already gone).
        await _coordinator.DeleteAsync(schedule.Id, ct);

        schedule.Status = ScheduleStatus.Archived;
        schedule.UpdatedAt = _clock.UtcNow;
        await _schedules.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.schedule_deleted", "test_suite_schedule",
            schedule.Id.ToString(), schedule.ProjectId,
            JsonSerializer.Serialize(new { scheduleId = schedule.Id, suiteId = schedule.SuiteId }), ct);
    }

    public async Task<ExecuteSuiteResult> RunNowAsync(Guid scheduleId, RunScheduleNowCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var schedule = await _schedules.GetByIdAsync(scheduleId, ct)
            ?? throw new NotFoundException("Test suite schedule not found.");

        // Project identity resolves server-side from the row: a spoofed
        // project can never steer another project's execution.
        await _authorization.RequireProjectAccessAsync(
            schedule.ProjectId, Permissions.TestCasesManage, ct);

        var key = string.IsNullOrWhiteSpace(command.IdempotencyKey) ? null : command.IdempotencyKey.Trim();
        if (key is not null && key.Length > MaxRunNowKeyLength)
            throw new ValidationException("Idempotency key is too long.",
                [new FieldError("idempotencyKey", $"Idempotency key must be at most {MaxRunNowKeyLength} characters.")]);

        if (schedule.Status == ScheduleStatus.Archived)
            throw new ConflictException("Archived schedules cannot be run.");

        // Run Now never touches cadence or pause state; a client retry with
        // the same key converges on the first run via execution idempotency.
        // Disabled schedules may still be run explicitly; archived ones may not.
        var basis = key is null
            ? $"{SuiteScheduleKeys.Prefix}{schedule.Id:N}-{Guid.NewGuid():N}"[..60]
            : SuiteScheduleKeys.ForManualTrigger(schedule.Id, key);
        return await FireCoreAsync(schedule, basis, requireActive: false, ct);
    }

    public async Task<int> DisableForSuiteAsync(Guid suiteId, CancellationToken ct)
    {
        var active = await _schedules.ListActiveBySuiteAsync(suiteId, ct);
        var disabled = 0;
        foreach (var schedule in active)
        {
            var remotePaused = true;
            try
            {
                await _coordinator.PauseAsync(schedule.Id, "Suite archived.", ct);
            }
            catch
            {
                // Fail closed: the row is still flipped to Disabled (the fire
                // path revalidates suite status and refuses to run), and the
                // missed remote pause is recorded in audit for operators.
                remotePaused = false;
            }

            schedule.Status = ScheduleStatus.Disabled;
            schedule.UpdatedAt = _clock.UtcNow;
            disabled++;

            await _audit.RecordAsync("suite.schedule_paused", "test_suite_schedule",
                schedule.Id.ToString(), schedule.ProjectId,
                JsonSerializer.Serialize(new
                {
                    scheduleId = schedule.Id,
                    suiteId,
                    reason = "suite_archived",
                    remotePaused,
                }), ct);
        }

        if (disabled > 0)
            await _schedules.SaveChangesAsync(ct);
        return disabled;
    }

    public async Task<ExecuteSuiteResult> FireAsync(Guid scheduleId, string idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        // No authorization here: HTTP callers authorize first; the Temporal
        // activity revalidates ownership below instead of trusting workflow
        // input beyond the schedule id.
        var schedule = await _schedules.GetByIdAsync(scheduleId, ct)
            ?? throw new NotFoundException("Test suite schedule not found.");

        return await FireCoreAsync(schedule, idempotencyKey, requireActive: true, ct);
    }

    private async Task<ExecuteSuiteResult> FireCoreAsync(
        TestSuiteSchedule schedule, string idempotencyKey, bool requireActive, CancellationToken ct)
    {
        try
        {
            if (requireActive && schedule.Status != ScheduleStatus.Active)
                throw new ValidationException("The schedule is not active.",
                    [new FieldError("scheduleId", "Only active schedules can fire.")]);

            var suite = await _suites.GetByIdRawAsync(schedule.SuiteId, ct)
                ?? throw new NotFoundException("Test suite not found.");
            if (suite.ProjectId != schedule.ProjectId)
                throw new ForbiddenException("The schedule does not belong to the suite project.");
            if (suite.Status == ProjectStatus.Archived)
                throw new ConflictException("Cannot fire a schedule whose suite is archived.");

            var result = await _executions.ExecuteAsSystemAsync(
                schedule.ProjectId, suite.Id, TriggerType.Schedule, idempotencyKey, schedule.Id, ct);

            schedule.LastTriggeredAt = _clock.UtcNow;
            schedule.LastExecutionId = result.ExecutionId;
            schedule.UpdatedAt = _clock.UtcNow;
            await _schedules.SaveChangesAsync(ct);

            await _audit.RecordAsync("suite.schedule_triggered", "test_suite_schedule",
                schedule.Id.ToString(), schedule.ProjectId,
                JsonSerializer.Serialize(new
                {
                    scheduleId = schedule.Id,
                    suiteId = suite.Id,
                    executionId = result.ExecutionId,
                    testCount = result.TestCount,
                }), ct);

            return result;
        }
        catch (Exception ex) when (ex is ValidationException or ConflictException or NotFoundException or ForbiddenException)
        {
            // Every deterministic fire rejection is audited so skipped ticks
            // stay visible; the Temporal activity treats these as terminal
            // for the tick (never retried).
            var suiteId = schedule.SuiteId;
            await _audit.RecordAsync("suite.schedule_failed", "test_suite_schedule",
                schedule.Id.ToString(), schedule.ProjectId,
                JsonSerializer.Serialize(new
                {
                    scheduleId = schedule.Id,
                    suiteId,
                    reason = ex.GetType().Name,
                    message = ex.Message,
                }), ct);
            throw;
        }
    }

    // ---------- helpers ----------

    /// <summary>
    /// Slice-1 opaque-scope convention for schedule ids: non-admins get 403
    /// on unknown ids (no existence leak); admins get a true 404.
    /// </summary>
    private async Task<TestSuiteSchedule> RequireScheduleAsync(Guid scheduleId, string permission, CancellationToken ct)
    {
        var schedule = await _schedules.GetByIdAsync(scheduleId, ct);
        await _authorization.RequireProjectAccessAsync(
            schedule?.ProjectId ?? scheduleId, permission, ct);
        return schedule ?? throw new NotFoundException("Test suite schedule not found.");
    }

    private async Task<DateTimeOffset?> NextRunBestEffortAsync(Guid scheduleId, CancellationToken ct)
    {
        try
        {
            return await _coordinator.GetNextRunAsync(scheduleId, ct);
        }
        catch
        {
            // Display-only enrichment: a remote miss never fails the read.
            return null;
        }
    }

    private static SuiteScheduleDefinition DefinitionOf(TestSuiteSchedule schedule)
        => new(
            schedule.CronExpression,
            schedule.TimeZoneId,
            Paused: schedule.Status != ScheduleStatus.Active,
            schedule.OverlapPolicy.ToString());

    private static SuiteScheduleDto Map(TestSuiteSchedule schedule, string suiteName, DateTimeOffset? NextRunAt)
        => new(
            schedule.Id,
            schedule.ProjectId,
            schedule.SuiteId,
            suiteName,
            schedule.Name,
            schedule.CronExpression,
            schedule.TimeZoneId,
            schedule.Status.ToString(),
            schedule.OverlapPolicy.ToString(),
            schedule.LastTriggeredAt,
            schedule.LastExecutionId,
            NextRunAt,
            schedule.CreatedAt,
            schedule.UpdatedAt);

    private static string? ValidateName(string? name, List<FieldError> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            errors.Add(new FieldError("name", "Schedule name is required."));
        else if (trimmed.Length > 200)
            errors.Add(new FieldError("name", "Schedule name must be at most 200 characters."));
        return errors.Count == 0 ? trimmed : null;
    }

    private static string? ValidateCron(string? cron, List<FieldError> errors)
    {
        var trimmed = cron?.Trim() ?? string.Empty;
        if (!CronValidation.IsPlausible(trimmed))
            errors.Add(new FieldError("cronExpression",
                "Cron expression must be a 5-field (minute hour day month weekday) or 6-field (leading seconds) expression."));
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

    private static ScheduleOverlapPolicy? ValidateOverlap(string? overlap, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(overlap))
            return ScheduleOverlapPolicy.Skip;
        if (Enum.TryParse<ScheduleOverlapPolicy>(overlap.Trim(), true, out var parsed))
            return parsed;
        errors.Add(new FieldError("overlapPolicy", "Overlap policy must be 'Skip' or 'Allow'."));
        return null;
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);
}
