using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Suite execution orchestration (Phase 4 Slices 9A/9B).
/// </summary>
public interface ISuiteExecutionService
{
    /// <summary>
    /// Manual Run Now: authorizes the caller, then fans out with
    /// <see cref="TriggerType.Manual"/>.
    /// </summary>
    Task<ExecuteSuiteResult> ExecuteAsync(Guid projectId, Guid suiteId, ExecuteSuiteCommand command, CancellationToken ct);

    /// <summary>
    /// System-initiated suite run (scheduled execution). Performs no caller
    /// authorization — the scheduler validates ownership first. Only callable
    /// server-side; never exposed with user-supplied auth bypass.
    /// </summary>
    Task<ExecuteSuiteResult> ExecuteAsSystemAsync(
        Guid projectId, Guid suiteId, TriggerType trigger, string? idempotencyKey, Guid? scheduleId, CancellationToken ct);
}
