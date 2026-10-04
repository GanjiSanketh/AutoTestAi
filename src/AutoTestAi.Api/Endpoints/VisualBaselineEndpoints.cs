using AutoTestAi.Application.Common;
using AutoTestAi.Application.Mobile;

namespace AutoTestAi.Api.Endpoints;

public sealed record ProposeBaselineBody(
    Guid? TestCaseVersionId,
    int? StepOrder,
    string? ImageBase64,
    int? Width,
    int? Height);

/// <summary>
/// Visual baseline lifecycle surface (Phase 3 Slice 3C-4D-1, docs/04 §18.4).
/// Candidate → Active → Superseded with explicit approval only; rejected
/// candidates are removed; an Active baseline is never deleted. Reads
/// require executions.read; mutations require settings.manage. Baseline
/// image bytes travel as bounded base64 in the propose body only; review
/// downloads use short-lived presigned URLs. No comparison lives here
/// (owned by 3C-4D-2); execution never consults these endpoints.
/// </summary>
public static class VisualBaselineEndpoints
{
    private const int MaxImageBase64Chars = 12 * 1024 * 1024;

    public static IEndpointRouteBuilder MapVisualBaselineEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/visual-baselines", (
                Guid projectId,
                Guid? testCaseVersionId,
                string? status,
                IVisualBaselineService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, testCaseVersionId, status, ct))
            .WithName("ListVisualBaselines")
            .WithSummary("List a project's visual baselines, optionally filtered.");

        projects.MapGet("/{projectId:guid}/visual-baselines/{baselineId:guid}", (
                Guid projectId,
                Guid baselineId,
                IVisualBaselineService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, baselineId, ct))
            .WithName("GetVisualBaseline")
            .WithSummary("One visual baseline (project ownership enforced).");

        projects.MapPost("/{projectId:guid}/visual-baselines", async (
                Guid projectId,
                ProposeBaselineBody? body,
                IVisualBaselineService service,
                CancellationToken ct) =>
            {
                var result = await service.ProposeAsync(new ProposeBaselineCommand(
                    projectId,
                    body?.TestCaseVersionId ?? Guid.Empty,
                    body?.StepOrder ?? 0,
                    DecodeImage(body?.ImageBase64),
                    body?.Width ?? 0,
                    body?.Height ?? 0), ct);
                return Results.Created(
                    $"/api/v1/projects/{projectId}/visual-baselines/{result.Id}", result);
            })
            .WithName("ProposeVisualBaseline")
            .WithSummary("Propose a candidate baseline image (settings.manage, idempotent).");

        projects.MapPost("/{projectId:guid}/visual-baselines/{baselineId:guid}/approve", (
                Guid projectId,
                Guid baselineId,
                IVisualBaselineService service,
                CancellationToken ct) =>
            service.ApproveAsync(new ApproveBaselineCommand(projectId, baselineId), ct))
            .WithName("ApproveVisualBaseline")
            .WithSummary("Approve a candidate baseline (settings.manage, audited).");

        projects.MapDelete("/{projectId:guid}/visual-baselines/{baselineId:guid}", async (
                Guid projectId,
                Guid baselineId,
                IVisualBaselineService service,
                CancellationToken ct) =>
            {
                await service.RejectAsync(projectId, baselineId, ct);
                return Results.NoContent();
            })
            .WithName("RejectVisualBaseline")
            .WithSummary("Reject (remove) a candidate baseline (settings.manage).");

        projects.MapGet("/{projectId:guid}/visual-baselines/{baselineId:guid}/download", async (
                Guid projectId,
                Guid baselineId,
                IVisualBaselineService service,
                CancellationToken ct) =>
            {
                var url = await service.GetDownloadUrlAsync(projectId, baselineId, ct);
                return Results.Ok(new { downloadUrl = url, expiresInSeconds = 900 });
            })
            .WithName("DownloadVisualBaseline")
            .WithSummary("Short-lived download URL for baseline review (executions.read).");

        return app;
    }

    private static byte[] DecodeImage(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return Array.Empty<byte>();
        var text = base64.Trim();
        if (text.Length > MaxImageBase64Chars)
            throw new ValidationException("Reference image is invalid.",
                new[] { new FieldError("imageBase64", "Reference image payload is too large.") });
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new ValidationException("Reference image is invalid.",
                new[] { new FieldError("imageBase64", "Reference image must be base64.") });
        }
    }
}
