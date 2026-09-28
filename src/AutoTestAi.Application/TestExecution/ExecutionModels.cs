namespace AutoTestAi.Application.TestExecution;

// ---------- Commands ----------

/// <summary>
/// Start an execution for one exact immutable TestCaseVersion (Slice 5).
/// The backend resolves and binds the version — never "latest".
/// </summary>
public sealed record StartExecutionCommand(
    Guid ProjectId,
    Guid TestCaseVersionId,
    Guid? EnvironmentId = null,
    string? Browser = null,
    string? IdempotencyKey = null);

public sealed record ExecutionFilters(
    string? Status,
    Guid? TestCaseId);

// ---------- DTOs (never carry secrets or raw credentials) ----------

public sealed record ExecutionListItemDto(
    Guid Id,
    Guid ProjectId,
    string Status,
    string TriggerType,
    Guid TestCaseId,
    string TestKey,
    string TestTitle,
    int TestCaseVersionNumber,
    string? Browser,
    string? FailureClassification,
    long? DurationMs,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record ExecutionStepDto(
    int Order,
    string Action,
    string? Target,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMs,
    string? ErrorMessage);

public sealed record ExecutionLogDto(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Message);

public sealed record ExecutionArtifactDto(
    Guid Id,
    string ArtifactType,
    string? FileName,
    int? StepOrder,
    string? ContentType,
    long? SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record ExecutionTestDetailDto(
    Guid Id,
    Guid TestCaseId,
    string TestKey,
    string TestTitle,
    string? TestSourceType,
    Guid TestCaseVersionId,
    int TestCaseVersionNumber,
    string ReviewStatus,
    string Status,
    string? Framework,
    string? Browser,
    string? FailureClassification,
    int Attempt,
    long? DurationMs,
    string? ErrorType,
    string? ErrorMessage,
    IReadOnlyList<ExecutionStepDto> Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ExecutionDetailDto(
    Guid Id,
    Guid ProjectId,
    string Status,
    string TriggerType,
    Guid? EnvironmentId,
    string? WorkflowId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    ExecutionTestDetailDto Test);

public sealed record StartExecutionResultDto(
    Guid ExecutionId,
    Guid ExecutionTestId,
    Guid ProjectId,
    Guid TestCaseId,
    Guid TestCaseVersionId,
    string Status,
    string? WorkflowId,
    DateTimeOffset CreatedAt,
    bool Duplicated);

public sealed record CancelExecutionResultDto(
    Guid ExecutionId,
    string Status,
    bool CancellationRequested);

public sealed record ArtifactDownloadDto(
    string DownloadUrl,
    int ExpiresInSeconds);

// ---------- Service ----------

/// <summary>
/// Execution control-plane use cases (Slice 5, FR-3.3). Every method enforces
/// server-side authorization; the store performs no authorization checks.
/// Executions bind one exact immutable TestCaseVersion and are immutable once terminal.
/// </summary>
public interface ITestExecutionService
{
    Task<StartExecutionResultDto> StartAsync(StartExecutionCommand command, CancellationToken cancellationToken);

    Task<Common.PagedResult<ExecutionListItemDto>> ListAsync(
        Guid projectId, int page, int pageSize, ExecutionFilters filters, CancellationToken cancellationToken);

    Task<ExecutionDetailDto> GetAsync(Guid executionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionStepDto>> ListStepsAsync(Guid executionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionLogDto>> ListLogsAsync(
        Guid executionId, long? afterId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionArtifactDto>> ListArtifactsAsync(Guid executionId, CancellationToken cancellationToken);

    Task<ArtifactDownloadDto> GetArtifactDownloadUrlAsync(
        Guid executionId, Guid artifactId, CancellationToken cancellationToken);

    Task<CancelExecutionResultDto> CancelAsync(Guid executionId, CancellationToken cancellationToken);
}
