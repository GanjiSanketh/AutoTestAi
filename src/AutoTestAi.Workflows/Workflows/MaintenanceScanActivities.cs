using AutoTestAi.Application.Maintenance;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>
/// Schedule-fire activities (Phase 4 Slice 10). Thin wrappers over the
/// Temporal-free <see cref="IMaintenanceScanScheduleService"/> fire path
/// resolved per-call from a service scope, mirroring
/// <see cref="SuiteScheduleActivities"/>.
/// </summary>
public sealed class MaintenanceScanActivities
{
    private readonly IServiceScopeFactory _scopes;

    public MaintenanceScanActivities(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>
    /// Business validation failures from the fire path are deterministic for
    /// this tick (archived project, disabled schedule) and must never be
    /// retried.
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
    public async Task<MaintenanceScanResult> RunMaintenanceScanAsync(Guid scheduleId)
    {
        using var scope = _scopes.CreateScope();
        var schedules = scope.ServiceProvider.GetRequiredService<IMaintenanceScanScheduleService>();
        var context = ActivityExecutionContext.Current;
        try
        {
            return await schedules.FireAsync(scheduleId, context.CancellationToken);
        }
        catch (Exception ex) when (ex is AutoTestAi.Application.Common.ValidationException
            or AutoTestAi.Application.Common.ConflictException
            or AutoTestAi.Application.Common.NotFoundException
            or AutoTestAi.Application.Authorization.ForbiddenException)
        {
            throw new ApplicationFailureException(
                ex.Message, "MaintenanceScanValidation", nonRetryable: true, details: null, nextRetryDelay: null);
        }
    }
}
