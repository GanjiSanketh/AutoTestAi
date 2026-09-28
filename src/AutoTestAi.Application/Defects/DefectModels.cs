namespace AutoTestAi.Application.Defects;

// ---------- Commands ----------

/// <summary>
/// Explicit human defect creation from a failed execution (Slice 6 §19).
/// Relationships are derived server-side from the execution — never trusted
/// from the client. An advisory analysis may be referenced but is optional.
/// </summary>
public sealed record CreateDefectCommand(
    Guid ProjectId,
    Guid ExecutionId,
    string Title,
    string? Description,
    string? Severity,
    Guid? FailureAnalysisId = null);

public sealed record UpdateDefectCommand(
    string Title,
    string? Description,
    string? Severity);

public sealed record ChangeDefectStatusCommand(string Status);

public sealed record DefectFilters(
    string? Status,
    string? Severity,
    string? Classification,
    Guid? TestCaseId,
    string? Search);

// ---------- DTOs ----------

public sealed record DefectListItemDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    string Severity,
    string Status,
    string? FailureClassification,
    Guid? ExecutionId,
    string? TestKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DefectAnalysisDto(
    Guid Id,
    int Attempt,
    string Status,
    string Classification,
    string? Summary,
    decimal? Confidence,
    string? Provider,
    string? Model);

public sealed record DefectDetailDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    string? Description,
    string Severity,
    string Status,
    string? FailureClassification,
    Guid? ExecutionId,
    Guid? ExecutionTestId,
    Guid? TestCaseId,
    string? TestKey,
    string? TestTitle,
    Guid? TestCaseVersionId,
    int? TestCaseVersionNumber,
    Guid? FailureAnalysisId,
    DefectAnalysisDto? Analysis,
    decimal? AiConfidence,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ---------- Service ----------

/// <summary>
/// Human-owned defect lifecycle (Slice 6). Defects are created explicitly by
/// authenticated users from failed executions; AI output is advisory only and
/// can never create a defect on its own.
/// </summary>
public interface IDefectService
{
    Task<DefectDetailDto> CreateAsync(CreateDefectCommand command, CancellationToken cancellationToken);

    Task<Common.PagedResult<DefectListItemDto>> ListAsync(
        Guid projectId, int page, int pageSize, DefectFilters filters, CancellationToken cancellationToken);

    Task<DefectDetailDto> GetAsync(Guid defectId, CancellationToken cancellationToken);

    Task<DefectDetailDto> UpdateAsync(
        Guid defectId, UpdateDefectCommand command, CancellationToken cancellationToken);

    Task<DefectDetailDto> ChangeStatusAsync(
        Guid defectId, ChangeDefectStatusCommand command, CancellationToken cancellationToken);
}
