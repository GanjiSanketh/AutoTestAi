using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.SelfHealing;

namespace AutoTestAi.Api.Endpoints;

public sealed record UpsertSelfHealingPolicyBody(
    bool? Enabled,
    bool? AiFallbackEnabled,
    int? MinDeterministicScore,
    decimal? MinAiConfidence,
    IReadOnlyList<string>? AllowedStrategies);

public sealed record HealingSuggestBody(
    string? Action,
    string? OriginalTarget,
    string? DomFragment,
    IReadOnlyList<string>? Attributes,
    IReadOnlyList<string>? NearbyText);

/// <summary>
/// Self-healing test engine surface (Phase 2 Slice 11, docs/06 §10.2).
/// Project policy administration follows the Slice 10 pattern (reads via
/// executions.read, writes via settings.manage). The worker machine plane
/// (healing/suggest) authenticates with the per-assignment lease token —
/// only the live lease holder may request AI candidates — and returns
/// locator DATA that the worker must validate against the live DOM.
/// </summary>
public static class SelfHealingEndpoints
{
    public static IEndpointRouteBuilder MapSelfHealingEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/self-healing-policy", (
                Guid projectId,
                ISelfHealingPolicyService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, ct))
            .WithName("GetSelfHealingPolicy")
            .WithSummary("Get the project self-healing policy (null when never configured; healing disabled).");

        projects.MapPut("/{projectId:guid}/self-healing-policy", (
                Guid projectId,
                UpsertSelfHealingPolicyBody? body,
                ISelfHealingPolicyService service,
                CancellationToken ct) =>
            service.UpsertAsync(new UpsertSelfHealingPolicyCommand(
                projectId,
                body?.Enabled ?? false,
                body?.AiFallbackEnabled ?? false,
                body?.MinDeterministicScore,
                body?.MinAiConfidence,
                body?.AllowedStrategies), ct))
            .WithName("UpsertSelfHealingPolicy")
            .WithSummary("Configure self-healing recovery policy (admin, secret-safe, disabled by default).");

        projects.MapGet("/{projectId:guid}/self-healing-policy/status", (
                Guid projectId,
                ISelfHealingPolicyService service,
                CancellationToken ct) =>
            service.GetStatusAsync(projectId, ct))
            .WithName("GetSelfHealingStatus")
            .WithSummary("Self-healing status and recovery counts (never includes secrets or DOM).");

        projects.MapGet("/{projectId:guid}/executions/{executionId:guid}/healing", (
                Guid projectId,
                Guid executionId,
                ISelfHealingPolicyService service,
                CancellationToken ct) =>
            service.ListAttemptsAsync(projectId, executionId, ct))
            .WithName("ListExecutionHealingAttempts")
            .WithSummary("Healing outcomes for one execution (original + recovered locators, never test mutation).");

        // Worker machine plane: assignment-token auth (fencing), anonymous to
        // Keycloak. Unknown refs and token mismatches yield 404/401 without
        // revealing execution existence.
        app.MapPost("/api/v1/execution-grid/assignments/{assignmentRef}/healing/suggest", async (
                string assignmentRef,
                HealingSuggestBody? body,
                ISelfHealingService healing,
                IGridAssignmentStore assignments,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                var credential = CredentialFrom(httpContext);
                if (string.IsNullOrWhiteSpace(assignmentRef) || string.IsNullOrWhiteSpace(credential))
                    return Results.NotFound();
                var lease = await assignments.FindActiveByRefAsync(assignmentRef, ct);
                if (lease is null || !string.Equals(
                        lease.AssignmentToken.ToString(), credential.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Results.Unauthorized();
                var evidence = new HealingEvidenceDto(
                    body?.Action ?? string.Empty,
                    body?.OriginalTarget ?? string.Empty,
                    body?.DomFragment ?? string.Empty,
                    (body?.Attributes ?? Array.Empty<string>()).Where(s => s is not null).ToList(),
                    (body?.NearbyText ?? Array.Empty<string>()).Where(s => s is not null).ToList());
                var candidates = await healing.SuggestCandidatesAsync(lease.ExecutionId, evidence, ct);
                return Results.Ok(new
                {
                    candidates = candidates.Select(c => new
                    {
                        strategy = c.Strategy,
                        value = c.Value,
                        reason = c.Reason,
                        confidence = c.Confidence,
                    }).ToList(),
                });
            })
            .WithName("SuggestHealingCandidates")
            .WithSummary("Worker AI fallback: suggest candidate locators (lease-token auth, validated DATA only).")
            .AllowAnonymous();

        return app;
    }

    private static string CredentialFrom(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim() : string.Empty;
    }
}
