using AutoTestAi.Application.TestCases;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Test suite schedule surface (Phase 4 Slice 9B). Authentication is required
/// on every route; the service layer enforces permissions + project
/// membership via schedule → suite → project. Schedule identity always comes
/// from the route.
/// </summary>
public static class SuiteScheduleEndpoints
{
    /// <summary>Schedule creation body. Project/suite identity comes from the route.</summary>
    public sealed record CreateSuiteScheduleBody(
        string? Name,
        string? CronExpression,
        string? TimeZoneId,
        string? OverlapPolicy);

    /// <summary>Schedule update body.</summary>
    public sealed record UpdateSuiteScheduleBody(
        string? Name,
        string? CronExpression,
        string? TimeZoneId,
        string? OverlapPolicy);

    /// <summary>Manual Run Now body. Project identity comes from the route.</summary>
    public sealed record RunScheduleNowBody(string? IdempotencyKey);

    public static IEndpointRouteBuilder MapSuiteScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var suiteSchedules = app
            .MapGroup("/api/v1/projects/{projectId:guid}/test-suites/{suiteId:guid}/schedules")
            .RequireAuthorization();

        suiteSchedules.MapGet("", (
                Guid projectId,
                Guid suiteId,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            service.ListBySuiteAsync(projectId, suiteId, ct))
            .WithName("ListSuiteSchedules")
            .WithSummary("List schedules of a suite (archived excluded).");

        suiteSchedules.MapPost("", async (
                Guid projectId,
                Guid suiteId,
                CreateSuiteScheduleBody body,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            {
                var created = await service.CreateAsync(projectId, suiteId, new CreateSuiteScheduleCommand(
                    projectId,
                    suiteId,
                    body?.Name ?? string.Empty,
                    body?.CronExpression ?? string.Empty,
                    body?.TimeZoneId,
                    body?.OverlapPolicy), ct);
                return Results.Created($"/api/v1/test-suite-schedules/{created.Id}", created);
            })
            .WithName("CreateSuiteSchedule")
            .WithSummary("Create a suite schedule (also creates the Temporal schedule).");

        var schedules = app.MapGroup("/api/v1/test-suite-schedules").RequireAuthorization();

        schedules.MapGet("/{scheduleId:guid}", async (
                Guid scheduleId,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            {
                var result = await service.GetByIdAsync(scheduleId, ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            })
            .WithName("GetSuiteSchedule")
            .WithSummary("Get schedule details (next run best-effort).");

        schedules.MapPut("/{scheduleId:guid}", async (
                Guid scheduleId,
                UpdateSuiteScheduleBody body,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            {
                var result = await service.UpdateAsync(scheduleId, new UpdateSuiteScheduleCommand(
                    body?.Name ?? string.Empty,
                    body?.CronExpression ?? string.Empty,
                    body?.TimeZoneId,
                    body?.OverlapPolicy), ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            })
            .WithName("UpdateSuiteSchedule")
            .WithSummary("Update schedule cadence/identity.");

        schedules.MapDelete("/{scheduleId:guid}", async (
                Guid scheduleId,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            {
                await service.ArchiveAsync(scheduleId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteSuiteSchedule")
            .WithSummary("Archive a schedule (removes the Temporal schedule).");

        schedules.MapPost("/{scheduleId:guid}/pause", async (
                Guid scheduleId,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            {
                await service.PauseAsync(scheduleId, ct);
                return Results.NoContent();
            })
            .WithName("PauseSuiteSchedule")
            .WithSummary("Pause a schedule (remote pause first).");

        schedules.MapPost("/{scheduleId:guid}/resume", async (
                Guid scheduleId,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            {
                await service.ResumeAsync(scheduleId, ct);
                return Results.NoContent();
            })
            .WithName("ResumeSuiteSchedule")
            .WithSummary("Resume a paused schedule.");

        schedules.MapPost("/{scheduleId:guid}/run-now", async (
                Guid scheduleId,
                RunScheduleNowBody? body,
                ISuiteScheduleService service,
                CancellationToken ct) =>
            {
                var result = await service.RunNowAsync(
                    scheduleId, new RunScheduleNowCommand(body?.IdempotencyKey), ct);
                return Results.Ok(result);
            })
            .WithName("RunSuiteScheduleNow")
            .WithSummary("Trigger one run without changing cadence or pause state.");

        return app;
    }
}
