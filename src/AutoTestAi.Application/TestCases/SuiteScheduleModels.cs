using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Suite schedule for list/detail views. NextRunAt is populated only on
/// detail reads (single Temporal describe); lists never pay per-row RPCs.
/// </summary>
public sealed record SuiteScheduleDto(
    Guid Id,
    Guid ProjectId,
    Guid SuiteId,
    string SuiteName,
    string Name,
    string CronExpression,
    string TimeZoneId,
    string Status,
    string OverlapPolicy,
    DateTimeOffset? LastTriggeredAt,
    Guid? LastExecutionId,
    DateTimeOffset? NextRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Schedule creation input.</summary>
public sealed record CreateSuiteScheduleCommand(
    Guid ProjectId,
    Guid SuiteId,
    string Name,
    string CronExpression,
    string? TimeZoneId,
    string? OverlapPolicy);

/// <summary>Schedule update input (cadence + identity).</summary>
public sealed record UpdateSuiteScheduleCommand(
    string Name,
    string CronExpression,
    string? TimeZoneId,
    string? OverlapPolicy);

/// <summary>Manual Run Now input. Schedule identity comes from the route; the
/// project is resolved server-side from the schedule row.</summary>
public sealed record RunScheduleNowCommand(string? IdempotencyKey);

/// <summary>
/// Schedule management use cases (Phase 4 Slice 9B).
/// </summary>
public interface ISuiteScheduleService
{
    Task<SuiteScheduleDto> CreateAsync(Guid projectId, Guid suiteId, CreateSuiteScheduleCommand command, CancellationToken ct);

    Task<IReadOnlyList<SuiteScheduleDto>> ListBySuiteAsync(Guid projectId, Guid suiteId, CancellationToken ct);

    Task<SuiteScheduleDto?> GetByIdAsync(Guid scheduleId, CancellationToken ct);

    Task<SuiteScheduleDto?> UpdateAsync(Guid scheduleId, UpdateSuiteScheduleCommand command, CancellationToken ct);

    Task PauseAsync(Guid scheduleId, CancellationToken ct);

    Task ResumeAsync(Guid scheduleId, CancellationToken ct);

    Task ArchiveAsync(Guid scheduleId, CancellationToken ct);

    /// <summary>
    /// Triggers exactly one execution through the shared fire path without
    /// touching cadence or pause state. Synchronous: returns the head
    /// execution like manual Run Now.
    /// </summary>
    Task<ExecuteSuiteResult> RunNowAsync(Guid scheduleId, RunScheduleNowCommand command, CancellationToken ct);

    /// <summary>
    /// Disables every active schedule of a suite (suite archive cascade).
    /// Internal: called by suite archival, not exposed over HTTP directly.
    /// </summary>
    Task<int> DisableForSuiteAsync(Guid suiteId, CancellationToken ct);

    /// <summary>
    /// Shared fire path used by the Temporal activity and Run Now.
    /// Validates schedule + suite, fans out with TriggerType.Schedule,
    /// persists last-run pointers, and audits. No authorization: callers
    /// (HTTP layer, Temporal activity) authorize/validate ownership first.
    /// </summary>
    Task<ExecuteSuiteResult> FireAsync(Guid scheduleId, string idempotencyKey, CancellationToken ct);
}
