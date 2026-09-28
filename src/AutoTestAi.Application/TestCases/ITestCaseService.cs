using System.Text.Json;
using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.TestCases;

// ---------- DTOs (list items never carry source code) ----------

public sealed record TestCaseListItemDto(
    Guid Id,
    Guid ProjectId,
    string TestKey,
    string Title,
    string? Module,
    string? Framework,
    string? Platform,
    string Priority,
    string Status,
    int LatestVersionNumber,
    string LatestReviewStatus,
    DateTimeOffset UpdatedAt);

public sealed record TestStepDto(int Order, string Action, string? Target, string? Value);

public sealed record TestCaseVersionDto(
    Guid Id,
    Guid TestCaseId,
    int VersionNumber,
    string? SourceCode,
    IReadOnlyList<TestStepDto> StructuredSteps,
    string? GenerationProvider,
    string? GenerationModel,
    long? GenerationLatencyMs,
    string ReviewStatus,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record TestCaseDto(
    Guid Id,
    Guid ProjectId,
    string TestKey,
    string Title,
    string? Description,
    string? Module,
    string? Framework,
    string? Platform,
    string Priority,
    string Status,
    string? SourceType,
    int LatestVersionNumber,
    string LatestReviewStatus,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ---------- Filters ----------

public sealed record TestCaseFilters(
    string? Search,
    string? Status,
    string? Priority,
    string? Framework,
    string? Platform,
    string? ReviewStatus);

// ---------- Commands (server decides identity, project, version sequence) ----------

public sealed record CreateTestCaseCommand(
    Guid ProjectId,
    string TestKey,
    string Title,
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
    JsonDocument? GenerationRequest = null);

public sealed record UpdateTestCaseCommand(
    string Title,
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

public sealed record ReviewTestCaseCommand(Guid VersionId, string ReviewStatus);

// ---------- Service ----------

/// <summary>
/// Test repository use cases (FR-3.1, docs/06 §6). Every method enforces
/// server-side authorization via <see cref="Authorization.IAuthorizationService"/>
/// resolved through test case → project.
/// </summary>
public interface ITestCaseService
{
    Task<PagedResult<TestCaseListItemDto>> ListAsync(
        Guid projectId, int page, int pageSize, TestCaseFilters filters, CancellationToken cancellationToken);

    Task<TestCaseDto> GetByIdAsync(Guid testCaseId, CancellationToken cancellationToken);

    Task<TestCaseDto> CreateAsync(CreateTestCaseCommand command, CancellationToken cancellationToken);

    Task<TestCaseDto> UpdateAsync(
        Guid testCaseId, UpdateTestCaseCommand command, CancellationToken cancellationToken);

    /// <summary>Soft delete: sets status to Archived, preserving versions and history.</summary>
    Task ArchiveAsync(Guid testCaseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TestCaseVersionDto>> ListVersionsAsync(
        Guid testCaseId, CancellationToken cancellationToken);

    Task<TestCaseVersionDto> GetVersionAsync(
        Guid testCaseId, Guid versionId, CancellationToken cancellationToken);

    Task<TestCaseVersionDto> ReviewAsync(
        Guid testCaseId, ReviewTestCaseCommand command, CancellationToken cancellationToken);
}
