namespace AutoTestAi.Application.TestExecution;

// ---------- mobile worker protocol DTOs (mirror workers/appium contract) ----------

/// <summary>
/// Hard bounds for Slice 3C-4B-3 mobile failure evidence. The worker
/// enforces these before returning results; the control plane re-enforces
/// them in <c>SanitizeOutcome</c> so a worker/control-plane skew can never
/// persist unbounded evidence.
/// </summary>
public static class MobileEvidenceBounds
{
    /// <summary>Maximum persisted page-source snapshot size (chars, XML text).</summary>
    public const int MaxPageSourceChars = 1024 * 1024;

    /// <summary>Maximum persisted worker log-tail size (chars, most-recent tail).</summary>
    public const int MaxServerLogChars = 256 * 1024;
}

/// <summary>Single mobile step handed to the worker. Shape mirrors TestStep; values pre-redacted when password-like.</summary>
public sealed record MobileStepDto(int Order, string Action, string? Target, string? Value);

public sealed record MobileTimeoutsDto(int ExecutionMs, int StepMs);

/// <summary>Trusted device identity for an assignment. Identifiers and display metadata only.</summary>
public sealed record MobileDeviceTargetDto(string? Udid, string? Model, string? PlatformVersion);

/// <summary>
/// Trusted application reference for an assignment. Structured values only:
/// no storage credentials, no user filesystem paths. DownloadUrl, when
/// present, is a short-lived server-minted presigned download URL resolved
/// at dispatch time by a later slice; this contract carries it opaquely.
/// </summary>
public sealed record MobileAppTargetDto(
    string? PackageId,
    string? BundleId,
    string? Version,
    string InstallPolicy,
    string? LaunchActivity,
    string? DeepLink,
    string? DownloadUrl);

/// <summary>
/// Fixed-schema Appium capabilities built server-side by
/// MobileCapabilityBuilder from validated structured data. No dictionary:
/// unknown capability keys are impossible by construction.
/// </summary>
public sealed record MobileCapabilitiesDto(
    string PlatformName,
    string AutomationName,
    string? DeviceName,
    string? Udid,
    string? AppPackage,
    string? AppActivity,
    string? BundleId,
    string? App,
    bool NoReset,
    bool FullReset,
    int NewCommandTimeout);

public sealed record MobileAssignmentDto(
    string AssignmentId,
    string ExecutionId,
    string Framework,
    string Platform,
    MobileDeviceTargetDto Device,
    MobileAppTargetDto App,
    MobileCapabilitiesDto Capabilities,
    IReadOnlyList<MobileStepDto> Steps,
    MobileTimeoutsDto Timeouts,
    bool ScreenshotOnFailure,
    Guid AssignmentToken,
    WorkerHealingPolicyDto? Healing = null);

public sealed record MobileStepResultDto(
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

public sealed record MobileLogDto(long Seq, long TimestampUnixMs, string Level, string Message);

public sealed record MobileScreenshotDto(int? StepOrder, string FileName, string ContentType, string Base64Content);

/// <summary>
/// Bounded, redacted page-source snapshot (Slice 3C-4B-3). Captured
/// best-effort at the worker failure evidence point only. XmlContent is
/// already secret-masked, heuristically redacted, and hard-bounded to
/// 1 MB before it enters the result. Never carries tokens, credentials,
/// or URLs.
/// </summary>
public sealed record MobilePageSourceDto(int? StepOrder, string FileName, string ContentType, string XmlContent);

/// <summary>
/// Bounded, redacted worker log tail (Slice 3C-4B-3). Serialized from the
/// assignment's in-memory log ring at terminal time, most-recent tail
/// only, hard-bounded to 256 KB. Attached to non-passed results as
/// failure evidence.
/// </summary>
public sealed record MobileServerLogDto(string FileName, string ContentType, string TextContent);

public sealed record MobileAssignmentResultDto(
    string AssignmentId,
    string Status,
    string? Classification,
    string? ErrorType,
    string? ErrorMessage,
    long DurationMs,
    IReadOnlyList<MobileStepResultDto> StepResults,
    IReadOnlyList<MobileLogDto> Logs,
    IReadOnlyList<MobileScreenshotDto> Screenshots,
    string? AppiumSessionId,
    IReadOnlyList<MobilePageSourceDto>? PageSources = null,
    IReadOnlyList<MobileServerLogDto>? ServerLogs = null,
    IReadOnlyList<WorkerHealingAttemptDto>? HealingAttempts = null);

public sealed record MobileAssignmentProgressDto(
    string AssignmentId,
    string Status,
    int? CurrentStepOrder,
    IReadOnlyList<MobileStepResultDto> StepResults,
    IReadOnlyList<MobileLogDto> Logs,
    MobileAssignmentResultDto? Result,
    string? AppiumSessionId);

/// <summary>
/// Boundary to the isolated Appium worker over HTTP (Slice 3C).
/// Implemented in Infrastructure when the execution slice lands; the worker
/// never touches the database. ClaimToken is never part of this contract.
/// </summary>
public interface IMobileWorkerClient
{
    /// <summary>Submits an assignment; returns the worker-side assignment id.</summary>
    Task<string> StartAssignmentAsync(MobileAssignmentDto assignment, CancellationToken ct);

    /// <summary>Polls progress; Result is set once terminal.</summary>
    Task<MobileAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct);

    /// <summary>Best-effort abort of a running assignment.</summary>
    Task CancelAssignmentAsync(string assignmentId, CancellationToken ct);
}
