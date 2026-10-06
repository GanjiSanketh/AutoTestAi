using AutoTestAi.Application.TestCases;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Jira freshness-check surface (Phase 4 Slice 7, docs/06).
/// Requires authentication; the orchestrator enforces testcases.manage +
/// project membership because the check performs an external Jira request
/// against the shared Jira-read budget. Read-only: one Jira GET, zero AI
/// calls, nothing persisted. Regeneration (if wanted) happens separately
/// through the existing Jira story generation endpoint and save flow.
/// </summary>
public static class JiraChangeCheckEndpoints
{
    public static IEndpointRouteBuilder MapJiraChangeCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var cases = app.MapGroup("/api/v1/test-cases").RequireAuthorization();

        cases.MapPost("/{testCaseId:guid}/versions/{versionId:guid}/jira-change-check", (
                Guid testCaseId,
                Guid versionId,
                IJiraChangeCheckService check,
                CancellationToken ct) =>
            check.CheckAsync(testCaseId, versionId, ct))
            .WithName("CheckJiraChanges")
            .WithSummary("Check whether the Jira issue behind a version changed; nothing is persisted.");

        return app;
    }
}
