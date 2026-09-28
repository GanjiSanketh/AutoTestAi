using AutoTestAi.Application.Defects;

namespace AutoTestAi.Api.Endpoints;

public sealed record CreateDefectBody(
    Guid? ExecutionId,
    string? Title,
    string? Description,
    string? Severity,
    Guid? FailureAnalysisId);

public sealed record UpdateDefectBody(
    string? Title,
    string? Description,
    string? Severity);

public sealed record ChangeDefectStatusBody(string? Status);

/// <summary>
/// Human-owned defect surface (docs/06 §9). Creation is an explicit user
/// action from a failed execution; relationships derive server-side.
/// AI can never create defects.
/// </summary>
public static class DefectEndpoints
{
    public static IEndpointRouteBuilder MapDefectEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projects.MapGet("/{projectId:guid}/defects", (
                Guid projectId,
                int? page,
                int? pageSize,
                string? status,
                string? severity,
                string? classification,
                Guid? testCaseId,
                string? search,
                IDefectService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, page ?? 1, pageSize ?? 25,
                new DefectFilters(status, severity, classification, testCaseId, search), ct))
            .WithName("ListDefects")
            .WithSummary("Paginated defect list with status/severity/classification filters.");

        projects.MapPost("/{projectId:guid}/defects", (
                Guid projectId,
                CreateDefectBody? body,
                IDefectService service,
                CancellationToken ct) =>
            service.CreateAsync(new CreateDefectCommand(
                projectId,
                body?.ExecutionId ?? Guid.Empty,
                body?.Title ?? string.Empty,
                body?.Description,
                body?.Severity,
                body?.FailureAnalysisId), ct))
            .WithName("CreateDefect")
            .WithSummary("File a defect against a failed execution (human action).");

        projects.MapGet("/{projectId:guid}/defects/{defectId:guid}", (
                Guid projectId,
                Guid defectId,
                IDefectService service,
                CancellationToken ct) =>
            // Project scoping is enforced through the defect record itself.
            service.GetAsync(defectId, ct))
            .WithName("GetDefect")
            .WithSummary("Defect detail with execution/version/analysis references.");

        projects.MapPut("/{projectId:guid}/defects/{defectId:guid}", (
                Guid projectId,
                Guid defectId,
                UpdateDefectBody? body,
                IDefectService service,
                CancellationToken ct) =>
            service.UpdateAsync(defectId, new UpdateDefectCommand(
                body?.Title ?? string.Empty,
                body?.Description,
                body?.Severity), ct))
            .WithName("UpdateDefect")
            .WithSummary("Update defect title/description/severity (relationships are immutable).");

        projects.MapPost("/{projectId:guid}/defects/{defectId:guid}/status", (
                Guid projectId,
                Guid defectId,
                ChangeDefectStatusBody? body,
                IDefectService service,
                CancellationToken ct) =>
            service.ChangeStatusAsync(defectId, new ChangeDefectStatusCommand(
                body?.Status ?? string.Empty), ct))
            .WithName("ChangeDefectStatus")
            .WithSummary("Transition defect status (validated, audited).");

        return app;
    }
}
