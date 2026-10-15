using AutoTestAi.Application.Maintenance;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Maintenance scan schedule surface (Phase 4 Slice 10). One schedule per
/// project; the schedule only controls WHEN the existing deterministic scan
/// runs. Authentication is required on every route; the service layer
/// enforces permissions + project membership with the project resolved
/// server-side from the route. Approval/application stay human-gated.
/// </summary>
public static class MaintenanceScanScheduleEndpoints
{
    /// <summary>Schedule creation/update body. Project identity comes from the route.</summary>
    public sealed record MaintenanceScanScheduleBody(
        string? CronExpression,
        string? TimeZoneId);

    public static IEndpointRouteBuilder MapMaintenanceScanScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var schedules = app
            .MapGroup("/api/v1/projects/{projectId:guid}/maintenance/schedule")
            .RequireAuthorization();

        schedules.MapGet("", async (
                Guid projectId,
                IMaintenanceScanScheduleService service,
                CancellationToken ct) =>
            {
                var result = await service.GetByProjectAsync(projectId, ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            })
            .WithName("GetMaintenanceScanSchedule")
            .WithSummary("Get the project maintenance scan schedule (no Temporal call).");

        schedules.MapPost("", async (
                Guid projectId,
                MaintenanceScanScheduleBody body,
                IMaintenanceScanScheduleService service,
                CancellationToken ct) =>
            {
                var created = await service.CreateAsync(projectId, new CreateMaintenanceScanScheduleCommand(
                    body?.CronExpression ?? string.Empty,
                    body?.TimeZoneId), ct);
                return Results.Created($"/api/v1/projects/{projectId}/maintenance/schedule", created);
            })
            .WithName("CreateMaintenanceScanSchedule")
            .WithSummary("Create the project maintenance scan schedule (also creates the Temporal schedule).");

        schedules.MapPut("", async (
                Guid projectId,
                MaintenanceScanScheduleBody body,
                IMaintenanceScanScheduleService service,
                CancellationToken ct) =>
            {
                var result = await service.UpdateAsync(projectId, new UpdateMaintenanceScanScheduleCommand(
                    body?.CronExpression ?? string.Empty,
                    body?.TimeZoneId), ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            })
            .WithName("UpdateMaintenanceScanSchedule")
            .WithSummary("Update the maintenance scan cadence.");

        schedules.MapPost("/pause", async (
                Guid projectId,
                IMaintenanceScanScheduleService service,
                CancellationToken ct) =>
            {
                await service.PauseAsync(projectId, ct);
                return Results.NoContent();
            })
            .WithName("PauseMaintenanceScanSchedule")
            .WithSummary("Pause the maintenance scan schedule (remote pause first).");

        schedules.MapPost("/resume", async (
                Guid projectId,
                IMaintenanceScanScheduleService service,
                CancellationToken ct) =>
            {
                await service.ResumeAsync(projectId, ct);
                return Results.NoContent();
            })
            .WithName("ResumeMaintenanceScanSchedule")
            .WithSummary("Resume a paused maintenance scan schedule.");

        schedules.MapDelete("", async (
                Guid projectId,
                IMaintenanceScanScheduleService service,
                CancellationToken ct) =>
            {
                await service.DeleteAsync(projectId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteMaintenanceScanSchedule")
            .WithSummary("Archive the maintenance scan schedule (removes the Temporal schedule).");

        schedules.MapGet("/next-run", async (
                Guid projectId,
                IMaintenanceScanScheduleService service,
                CancellationToken ct) =>
            {
                var nextRun = await service.GetNextRunAsync(projectId, ct);
                return nextRun is not null
                    ? Results.Ok(new { nextRunAt = nextRun })
                    : Results.NotFound();
            })
            .WithName("GetMaintenanceScanScheduleNextRun")
            .WithSummary("Explicit next-run retrieval (at most one Temporal describe).");

        return app;
    }
}
