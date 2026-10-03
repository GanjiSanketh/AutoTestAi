namespace AutoTestAi.Application.TestExecution;

// ---------- mobile worker protocol DTOs (mirror workers/appium contract) ----------

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
    Guid AssignmentToken);

public sealed record MobileStepResultDto(
    int Order,
    string Action,
    string? Target,
    string Status,
    long StartedAtUnixMs,
    long CompletedAtUnixMs,
    long DurationMs,
    string? ErrorMessage);

public sealed record MobileLogDto(long Seq, long TimestampUnixMs, string Level, string Message);

public sealed record MobileScreenshotDto(int? StepOrder, string FileName, string ContentType, string Base64Content);

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
    string? AppiumSessionId);

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
