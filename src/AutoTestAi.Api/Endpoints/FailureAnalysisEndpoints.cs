using AutoTestAi.Application.FailureAnalysis;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Failure-analysis surface (docs/06 §13). Synchronous MVP: POST runs one
/// bounded analysis attempt and returns it. Triggering requires
/// executions.analyze; reading requires executions.read. Analysis is advisory:
/// it never changes execution state and never creates defects.
/// </summary>
public static class FailureAnalysisEndpoints
{
    public static IEndpointRouteBuilder MapFailureAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapPost("/{projectId:guid}/executions/{executionId:guid}/failure-analysis", (
                Guid projectId,
                Guid executionId,
                IFailureAnalysisService service,
                CancellationToken ct) =>
            // Project scoping is enforced through the execution record itself.
            service.AnalyzeAsync(executionId, ct))
            .WithName("AnalyzeExecutionFailure")
            .WithSummary("Run one AI failure-analysis attempt for a failed execution (new attempt per call).");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}/failure-analysis", (
                Guid projectId,
                Guid executionId,
                IFailureAnalysisService service,
                CancellationToken ct) =>
            service.GetLatestAsync(executionId, ct))
            .WithName("GetFailureAnalysis")
            .WithSummary("Latest failure-analysis attempt for an execution (404 when never analyzed).");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}/failure-analysis/attempts", (
                Guid projectId,
                Guid executionId,
                IFailureAnalysisService service,
                CancellationToken ct) =>
            service.ListAttemptsAsync(executionId, ct))
            .WithName("ListFailureAnalysisAttempts")
            .WithSummary("Analysis attempt history, newest first (history is never overwritten).");

        return app;
    }
}
