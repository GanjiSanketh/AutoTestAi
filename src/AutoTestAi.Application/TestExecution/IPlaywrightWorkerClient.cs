namespace AutoTestAi.Application.TestExecution;

// ---------- worker protocol DTOs (mirror workers/playwright contract) ----------

/// <summary>Single step handed to the worker. Values are pre-redacted when password-like.</summary>
public sealed record WorkerStepDto(int Order, string Action, string? Target, string? Value);

public sealed record WorkerTimeoutsDto(int ExecutionMs, int StepMs);

/// <summary>
/// Worker-facing self-healing policy fragment (Phase 2 Slice 11).
/// Absent or disabled reproduces pre-Slice-11 behavior exactly.
/// </summary>
public sealed record WorkerHealingPolicyDto(
    bool Enabled,
    bool AiFallbackEnabled,
    int MaxAttemptsPerStep,
    int? MinDeterministicScore,
    decimal? MinAiConfidence,
    IReadOnlyList<string> AllowedStrategies);

public sealed record WorkerAssignmentDto(
    string AssignmentId,
    string ExecutionId,
    string Framework,
    string Browser,
    string? TargetUrl,
    IReadOnlyList<WorkerStepDto> Steps,
    WorkerTimeoutsDto Timeouts,
    bool ScreenshotOnFailure,
    bool ScreenshotOnFinish,
    Guid AssignmentToken,
    WorkerHealingPolicyDto? Healing = null);

public sealed record WorkerStepResultDto(
    int Order,
    string Action,
    string? Target,
    string Status,
    long StartedAtUnixMs,
    long CompletedAtUnixMs,
    long DurationMs,
    string? ErrorMessage,
    bool? Healed = null,
    string? RecoveredTarget = null,
    string? HealingStrategy = null,
    bool? AiAssisted = null);

public sealed record WorkerLogDto(long Seq, long TimestampUnixMs, string Level, string Message);

public sealed record WorkerScreenshotDto(int? StepOrder, string FileName, string ContentType, string Base64Content);

/// <summary>Worker-reported healing outcome for one step (Phase 2 Slice 11).</summary>
public sealed record WorkerHealingAttemptDto(
    int StepOrder,
    string StepAction,
    string? OriginalStrategy,
    string? OriginalValue,
    string? RecoveredStrategy,
    string? RecoveredValue,
    string HealingStrategy,
    string Status,
    int CandidateCount,
    bool WasApplied,
    bool IsAiAssisted,
    string? ErrorMessage);

public sealed record WorkerAssignmentResultDto(
    string AssignmentId,
    string Status,
    string? Classification,
    string? ErrorType,
    string? ErrorMessage,
    long DurationMs,
    IReadOnlyList<WorkerStepResultDto> StepResults,
    IReadOnlyList<WorkerLogDto> Logs,
    IReadOnlyList<WorkerScreenshotDto> Screenshots,
    IReadOnlyList<WorkerHealingAttemptDto>? HealingAttempts = null);

public sealed record WorkerAssignmentProgressDto(
    string AssignmentId,
    string Status,
    int? CurrentStepOrder,
    IReadOnlyList<WorkerStepResultDto> StepResults,
    IReadOnlyList<WorkerLogDto> Logs,
    WorkerAssignmentResultDto? Result);

/// <summary>
/// Thrown when the worker cannot be reached or rejects the assignment at the
/// transport level (not a functional test outcome). Retryable failures are
/// retried once by the engine; contract rejections (4xx) never retry.
/// </summary>
public sealed class WorkerInfrastructureException : Exception
{
    public bool IsRetryable { get; init; } = true;

    /// <summary>Upstream HTTP status when the failure came from a response.</summary>
    public int? HttpStatusCode { get; init; }

    public WorkerInfrastructureException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Boundary to the isolated Playwright worker over HTTP (Slice 5 §36).
/// Implemented in Infrastructure; the worker never touches the database.
/// </summary>
public interface IPlaywrightWorkerClient
{
    /// <summary>Submits an assignment; returns the worker-side assignment id.</summary>
    Task<string> StartAssignmentAsync(WorkerAssignmentDto assignment, CancellationToken ct);

    /// <summary>Polls progress; Result is set once terminal.</summary>
    Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct);

    /// <summary>Best-effort abort of a running assignment.</summary>
    Task CancelAssignmentAsync(string assignmentId, CancellationToken ct);
}
