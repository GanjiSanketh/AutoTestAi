using AutoTestAi.Application.TestExecution;

namespace AutoTestAi.Api.Endpoints;

public sealed record StartExecutionBody(
    Guid? TestCaseVersionId,
    Guid? EnvironmentId,
    string? Browser,
    string? IdempotencyKey);

/// <summary>
/// Execution control-plane surface (docs/06 §8). All routes require
/// authentication; the service layer enforces permissions + project membership.
/// Executions bind one exact immutable TestCaseVersion and are immutable once terminal.
/// </summary>
public static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/executions", async (
                Guid projectId,
                StartExecutionBody? body,
                ITestExecutionService service,
                CancellationToken ct) =>
            {
                var result = await service.StartAsync(new StartExecutionCommand(
                    projectId,
                    body?.TestCaseVersionId ?? Guid.Empty,
                    body?.EnvironmentId,
                    body?.Browser,
                    body?.IdempotencyKey), ct);
                return result.Duplicated
                    ? Results.Ok(result)
                    : Results.Accepted($"/api/v1/projects/{projectId}/executions/{result.ExecutionId}", result);
            })
            .WithName("StartExecution")
            .WithSummary("Start an execution for one exact approved TestCaseVersion (202; 200 when an idempotency key repeats).");

        projects.MapGet("/{projectId:guid}/executions", (
                Guid projectId,
                int? page,
                int? pageSize,
                string? status,
                Guid? testCaseId,
                ITestExecutionService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, page ?? 1, pageSize ?? 25,
                new ExecutionFilters(status, testCaseId), ct))
            .WithName("ListExecutions")
            .WithSummary("Paginated execution history for a project.");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}", (
                Guid projectId,
                Guid executionId,
                ITestExecutionService service,
                CancellationToken ct) =>
            // Project scoping is enforced through the execution record itself.
            service.GetAsync(executionId, ct))
            .WithName("GetExecution")
            .WithSummary("Execution detail with test, steps, and classification.");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}/steps", (
                Guid projectId,
                Guid executionId,
                ITestExecutionService service,
                CancellationToken ct) =>
            service.ListStepsAsync(executionId, ct))
            .WithName("ListExecutionSteps")
            .WithSummary("Step results for an execution.");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}/logs", (
                Guid projectId,
                Guid executionId,
                long? afterId,
                int? take,
                ITestExecutionService service,
                CancellationToken ct) =>
            service.ListLogsAsync(executionId, afterId, take ?? 100, ct))
            .WithName("ListExecutionLogs")
            .WithSummary("Bounded chronological execution logs (cursor via afterId).");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}/artifacts", (
                Guid projectId,
                Guid executionId,
                ITestExecutionService service,
                CancellationToken ct) =>
            service.ListArtifactsAsync(executionId, ct))
            .WithName("ListExecutionArtifacts")
            .WithSummary("Artifact metadata for an execution (bytes via download).");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}/artifacts/{artifactId:guid}/download", (
                Guid projectId,
                Guid executionId,
                Guid artifactId,
                ITestExecutionService service,
                CancellationToken ct) =>
            service.GetArtifactDownloadUrlAsync(executionId, artifactId, ct))
            .WithName("DownloadExecutionArtifact")
            .WithSummary("Short-lived authorized download URL for one artifact.");

        projects.MapPost("/{projectId:guid}/executions/{executionId:guid}/cancel", (
                Guid projectId,
                Guid executionId,
                ITestExecutionService service,
                CancellationToken ct) =>
            service.CancelAsync(executionId, ct))
            .WithName("CancelExecution")
            .WithSummary("Cancel a queued/running execution (idempotent).");

        return app;
    }
}
