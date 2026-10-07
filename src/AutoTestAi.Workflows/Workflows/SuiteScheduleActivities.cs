using AutoTestAi.Application.TestCases;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>
/// Schedule-fire activities (Phase 4 Slice 9B). Thin wrappers over the
/// Temporal-free <see cref="ISuiteScheduleService"/> fire path resolved
/// per-call from a service scope, mirroring <see cref="TestExecutionActivities"/>.
/// </summary>
public sealed class SuiteScheduleActivities
{
    private readonly IServiceScopeFactory _scopes;

    public SuiteScheduleActivities(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>
    /// Business validation failures from the fire path are deterministic for
    /// this tick (archived suite, disabled schedule, no approved versions)
    /// and must never be retried.
    /// </summary>
    public static bool IsBusinessFailure(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is ApplicationFailureException app && app.NonRetryable)
                return true;
            current = current.InnerException;
        }
        return false;
    }

    [Activity]
    public async Task<Guid> RunSuiteScheduleAsync(Guid scheduleId)
    {
        using var scope = _scopes.CreateScope();
        var schedules = scope.ServiceProvider.GetRequiredService<ISuiteScheduleService>();
        var context = ActivityExecutionContext.Current;
        // Deterministic within this action run (retries converge), unique
        // across ticks (each tick is a new run). See SuiteScheduleKeys.
        var basis = SuiteScheduleKeys.ForActionRun(scheduleId, context.Info.WorkflowRunId);
        try
        {
            var result = await schedules.FireAsync(scheduleId, basis, context.CancellationToken);
            return result.ExecutionId;
        }
        catch (Exception ex) when (ex is AutoTestAi.Application.Common.ValidationException
            or AutoTestAi.Application.Common.ConflictException
            or AutoTestAi.Application.Common.NotFoundException
            or AutoTestAi.Application.Authorization.ForbiddenException)
        {
            throw new ApplicationFailureException(
                ex.Message, "SuiteScheduleValidation", nonRetryable: true, details: null, nextRetryDelay: null);
        }
    }
}
