using AutoTestAi.Application.TestCases;

namespace AutoTestAi.Api.Endpoints;

/// <summary>
/// Jira freshness-check surface (Phase 4 Slice 7/8, docs/06).
/// Requires authentication; the orchestrator enforces testcases.manage +
/// project membership because the check performs an external Jira request
/// against the shared Jira-read budget. Read-only: one Jira GET per item, zero AI
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

        // Bulk freshness check (Phase 4 Slice 8)
        cases.MapPost("/jira-change-check/bulk", async (
                Guid projectId,
                JiraBulkCheckRequest body,
                IJiraChangeCheckService check,
                CancellationToken ct) =>
        {
            var result = await check.CheckBulkAsync(projectId, body.VersionIds, ct);
            return Results.Ok(result);
        })
        .WithName("BulkCheckJiraChanges")
        .WithSummary("Bulk check Jira freshness for up to 25 current Jira-origin versions. Sequential, rate-limited, per-item results. No background job.");

        return app;
    }
}

/// <summary>Bulk Jira freshness check request (Phase 4 Slice 8).</summary>
public sealed record JiraBulkCheckRequest(
    IReadOnlyList<Guid> VersionIds);
