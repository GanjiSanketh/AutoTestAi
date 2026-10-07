using AutoTestAi.Application.TestCases;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Test suite orchestration surface (Phase 4 Slice 9A).
/// Authentication is required on every route; the service layer enforces
/// permissions + project membership via suite → project. No endpoint defines
/// a named authorization policy (matches every other surface in this API).
/// </summary>
public static class SuiteEndpoints
{
    /// <summary>Manual suite execution body. Suite identity comes from the route.</summary>
    public sealed record ExecuteSuiteBody(Guid ProjectId, string? IdempotencyKey);

    public static IEndpointRouteBuilder MapSuiteEndpoints(this IEndpointRouteBuilder app)
    {
        var projectSuites = app.MapGroup("/api/v1/projects/{projectId:guid}/test-suites").RequireAuthorization();

        projectSuites.MapGet("", (
                Guid projectId,
                string? search,
                string? status,
                int? page,
                int? pageSize,
                ISuiteService service,
                CancellationToken ct) =>
            service.ListAsync(projectId,
                new SuiteListFilters(search, status),
                page ?? 1, pageSize ?? 25, ct))
            .WithName("ListSuites")
            .WithSummary("Paginated, filtered suite list.");

        projectSuites.MapPost("", async (
                Guid projectId,
                CreateSuiteCommand body,
                ISuiteService service,
                CancellationToken ct) =>
            {
                var created = await service.CreateAsync(new CreateSuiteCommand(
                    projectId,
                    body?.Name ?? string.Empty,
                    body?.Description,
                    body?.Status,
                    body?.Members), ct);
                return Results.Created($"/api/v1/test-suites/{created.Id}", created);
            })
            .WithName("CreateSuite")
            .WithSummary("Create a test suite.");

        var suites = app.MapGroup("/api/v1/test-suites").RequireAuthorization();

        suites.MapGet("/{suiteId:guid}", async (
                Guid suiteId,
                ISuiteService service,
                CancellationToken ct) =>
            {
                var result = await service.GetByIdAsync(suiteId, ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            })
            .WithName("GetSuite")
            .WithSummary("Get suite details with members.");

        suites.MapPut("/{suiteId:guid}", async (
                Guid suiteId,
                UpdateSuiteCommand body,
                ISuiteService service,
                CancellationToken ct) =>
            {
                var result = await service.UpdateAsync(suiteId, body, ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            })
            .WithName("UpdateSuite")
            .WithSummary("Update suite metadata.");

        suites.MapDelete("/{suiteId:guid}", async (
                Guid suiteId,
                ISuiteService service,
                CancellationToken ct) =>
            {
                await service.ArchiveAsync(suiteId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteSuite")
            .WithSummary("Archive a test suite.");

        // Membership
        suites.MapPost("/{suiteId:guid}/test-cases", async (
                Guid suiteId,
                CreateSuiteMemberCommand body,
                ISuiteService service,
                CancellationToken ct) =>
            {
                var result = await service.AddTestCaseAsync(suiteId, body, ct);
                return Results.Ok(result);
            })
            .WithName("AddTestCaseToSuite")
            .WithSummary("Add a test case to the suite.");

        suites.MapDelete("/{suiteId:guid}/test-cases/{testCaseId:guid}", async (
                Guid suiteId,
                Guid testCaseId,
                ISuiteService service,
                CancellationToken ct) =>
            {
                await service.RemoveTestCaseAsync(suiteId, testCaseId, ct);
                return Results.NoContent();
            })
            .WithName("RemoveTestCaseFromSuite")
            .WithSummary("Remove a test case from the suite.");

        suites.MapPut("/{suiteId:guid}/test-cases/order", async (
                Guid suiteId,
                ReorderSuiteMembersCommand body,
                ISuiteService service,
                CancellationToken ct) =>
            {
                await service.ReorderAsync(suiteId, body, ct);
                return Results.NoContent();
            })
            .WithName("ReorderSuiteMembers")
            .WithSummary("Reorder test cases in the suite.");

        // Manual execution ("Run Now"): suite identity comes from the route so
        // a spoofed body id can never steer execution at another suite.
        suites.MapPost("/{suiteId:guid}/execute", async (
                Guid suiteId,
                ExecuteSuiteBody body,
                ISuiteExecutionService executionService,
                CancellationToken ct) =>
            {
                var result = await executionService.ExecuteAsync(
                    body.ProjectId, suiteId,
                    new ExecuteSuiteCommand(body.ProjectId, suiteId, body.IdempotencyKey), ct);
                return Results.Ok(result);
            })
            .WithName("ExecuteSuite")
            .WithSummary("Manually execute the suite.");

        // Execution history
        suites.MapGet("/{suiteId:guid}/executions", async (
                Guid suiteId,
                string? status,
                string? triggerType,
                int? page,
                int? pageSize,
                ISuiteService service,
                CancellationToken ct) =>
            {
                var result = await service.GetExecutionHistoryAsync(suiteId,
                    new SuiteExecutionHistoryFilters(status, triggerType),
                    page ?? 1, pageSize ?? 25, ct);
                return Results.Ok(result);
            })
            .WithName("GetSuiteExecutionHistory")
            .WithSummary("Get execution history for the suite.");

        // Suite report (Slice 9B: optional trigger filter + daily trend)
        suites.MapGet("/{suiteId:guid}/report", async (
                Guid suiteId,
                string? from,
                string? to,
                string? trigger,
                string? groupBy,
                ISuiteService service,
                CancellationToken ct) =>
            {
                DateTimeOffset? fromDate = null;
                DateTimeOffset? toDate = null;

                if (!string.IsNullOrWhiteSpace(from))
                {
                    if (!DateTimeOffset.TryParse(from, out var parsedFrom))
                        return Results.BadRequest(new { error = "Invalid 'from' date format." });
                    fromDate = parsedFrom;
                }

                if (!string.IsNullOrWhiteSpace(to))
                {
                    if (!DateTimeOffset.TryParse(to, out var parsedTo))
                        return Results.BadRequest(new { error = "Invalid 'to' date format." });
                    toDate = parsedTo;
                }

                var result = await service.GetReportAsync(suiteId,
                    new SuiteReportFilters(fromDate, toDate, trigger, groupBy), ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();
            })
            .WithName("GetSuiteReport")
            .WithSummary("Get a suite execution report with trigger breakdown and optional daily trend.");

        return app;
    }
}
