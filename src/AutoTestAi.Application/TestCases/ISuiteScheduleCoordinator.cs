namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Cron/overlap portion of a Temporal schedule definition, free of SDK types
/// so Application never references Temporal.
/// </summary>
public sealed record SuiteScheduleDefinition(
    string CronExpression,
    string TimeZoneId,
    bool Paused,
    string OverlapPolicy);

/// <summary>
/// Durable-scheduling boundary (Phase 4 Slice 9B). Implemented with Temporal
/// Schedules in the Workflows layer; fakes back the API in tests.
/// Remote-missing reads/deletes are idempotent (null / no-op); remote
/// validation failures surface as <see cref="Common.ValidationException"/>;
/// unconfigured/remote failures surface as <see cref="InvalidOperationException"/>
/// (existing 503 dependency convention). Never throws Temporal SDK types.
/// </summary>
public interface ISuiteScheduleCoordinator
{
    bool IsConfigured { get; }

    Task CreateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct);

    Task UpdateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct);

    Task PauseAsync(Guid scheduleId, string note, CancellationToken ct);

    Task ResumeAsync(Guid scheduleId, string note, CancellationToken ct);

    Task DeleteAsync(Guid scheduleId, CancellationToken ct);

    /// <summary>
    /// Next scheduled fire, or null when the remote schedule is missing,
    /// paused with no upcoming action, or Temporal is unavailable.
    /// List views must not call this per row.
    /// </summary>
    Task<DateTimeOffset?> GetNextRunAsync(Guid scheduleId, CancellationToken ct);
}
