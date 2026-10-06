using System.Text.Json;
using AutoTestAi.Application.TestCases;

namespace AutoTestAi.Api.Endpoints;

public sealed record CreateTestCaseBody(
    string? TestKey,
    string? Title,
    string? Description,
    string? Module,
    string? Framework,
    string? Platform,
    string? Priority,
    string? Status,
    string? SourceType,
    string? SourceCode,
    JsonElement? StructuredSteps,
    string? GenerationProvider = null,
    string? GenerationModel = null,
    long? GenerationLatencyMs = null,
    JsonElement? GenerationRequest = null);

public sealed record UpdateTestCaseBody(
    string? Title,
    string? Description,
    string? Module,
    string? Framework,
    string? Platform,
    string? Priority,
    string? Status,
    string? SourceType,
    string? SourceCode,
    JsonElement? StructuredSteps,
    bool HasSourceCode,
    bool HasStructuredSteps);

public sealed record ReviewTestCaseBody(Guid? VersionId, string? ReviewStatus);

/// <summary>Test repository surface (docs/06 §6). All routes require authentication;
/// the service layer enforces permissions + project membership via test case → project.</summary>
public static class TestCaseEndpoints
{
    public static IEndpointRouteBuilder MapTestCaseEndpoints(this IEndpointRouteBuilder app)
    {
        var projectCases = app.MapGroup("/api/v1/projects").RequireAuthorization();

        projectCases.MapGet("/{projectId:guid}/test-cases", (
                Guid projectId,
                int? page,
                int? pageSize,
                string? search,
                string? status,
                string? priority,
                string? framework,
                string? platform,
                string? reviewStatus,
                string? jiraIssueKey,
                ITestCaseService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, page ?? 1, pageSize ?? 25,
                new TestCaseFilters(search, status, priority, framework, platform, reviewStatus, jiraIssueKey), ct))
            .WithName("ListTestCases")
            .WithSummary("Paginated, filtered test-case list (no source code).");

        projectCases.MapPost("/{projectId:guid}/test-cases", async (
                Guid projectId,
                CreateTestCaseBody body,
                ITestCaseService service,
                CancellationToken ct) =>
            {
                var created = await service.CreateAsync(new CreateTestCaseCommand(
                    projectId,
                    body?.TestKey ?? string.Empty,
                    body?.Title ?? string.Empty,
                    body?.Description,
                    body?.Module,
                    body?.Framework,
                    body?.Platform,
                    body?.Priority,
                    body?.Status,
                    body?.SourceType,
                    body?.SourceCode,
                    body?.StructuredSteps,
                    body?.GenerationProvider,
                    body?.GenerationModel,
                    body?.GenerationLatencyMs,
                    body?.GenerationRequest is { } generationRequest
                        ? JsonDocument.Parse(generationRequest.GetRawText())
                        : null), ct);
                return Results.Created($"/api/v1/test-cases/{created.Id}", created);
            })
            .WithName("CreateTestCase")
            .WithSummary("Create a test case with version 1.");

        var cases = app.MapGroup("/api/v1/test-cases").RequireAuthorization();

        cases.MapGet("/{testCaseId:guid}", (
                Guid testCaseId,
                ITestCaseService service,
                CancellationToken ct) =>
            service.GetByIdAsync(testCaseId, ct))
            .WithName("GetTestCase")
            .WithSummary("Test case details for authorized callers.");

        cases.MapPut("/{testCaseId:guid}", (
                Guid testCaseId,
                UpdateTestCaseBody body,
                ITestCaseService service,
                CancellationToken ct) =>
            service.UpdateAsync(testCaseId, new UpdateTestCaseCommand(
                body?.Title ?? string.Empty,
                body?.Description,
                body?.Module,
                body?.Framework,
                body?.Platform,
                body?.Priority,
                body?.Status,
                body?.SourceType,
                body?.SourceCode,
                body?.StructuredSteps,
                body?.HasSourceCode ?? false,
                body?.HasStructuredSteps ?? false), ct))
            .WithName("UpdateTestCase")
            .WithSummary("Update metadata; content edits allocate a new version.");

        cases.MapDelete("/{testCaseId:guid}", async (
                Guid testCaseId,
                ITestCaseService service,
                CancellationToken ct) =>
            {
                await service.ArchiveAsync(testCaseId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteTestCase")
            .WithSummary("Soft delete: archives the test case, preserving versions.");

        cases.MapGet("/{testCaseId:guid}/versions", (
                Guid testCaseId,
                ITestCaseService service,
                CancellationToken ct) =>
            service.ListVersionsAsync(testCaseId, ct))
            .WithName("ListTestCaseVersions")
            .WithSummary("Version history (newest first).");

        cases.MapGet("/{testCaseId:guid}/versions/{versionId:guid}", (
                Guid testCaseId,
                Guid versionId,
                ITestCaseService service,
                CancellationToken ct) =>
            service.GetVersionAsync(testCaseId, versionId, ct))
            .WithName("GetTestCaseVersion")
            .WithSummary("Single immutable version (must belong to the test case).");

        cases.MapPost("/{testCaseId:guid}/review", (
                Guid testCaseId,
                ReviewTestCaseBody body,
                ITestCaseService service,
                CancellationToken ct) =>
            {
                if (body?.VersionId is null)
                    throw new AutoTestAi.Application.Common.ValidationException(
                        "Version is required.",
                        new[] { new AutoTestAi.Application.Common.FieldError("versionId", "Version id is required.") });
                return service.ReviewAsync(testCaseId, new ReviewTestCaseCommand(
                    body.VersionId.Value, body.ReviewStatus ?? string.Empty), ct);
            })
            .WithName("ReviewTestCase")
            .WithSummary("Set a version's review status (validated transition).");

        return app;
    }
}
