using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.ExecutionGrid;

// ---------- worker-facing commands ----------

/// <summary>Worker registration intent. The credential is server-issued.</summary>
public sealed record RegisterWorkerCommand(
    string WorkerKey,
    string? DisplayName,
    string? WorkerType,
    string? Framework,
    IReadOnlyList<string>? Browsers,
    string? Version,
    int? Capacity,
    string? BaseUrl,
    string? ProvisioningToken);

public sealed record RegisterWorkerResult(
    Guid WorkerId,
    string Credential,
    int HeartbeatIntervalSeconds,
    int LeaseDurationSeconds);

public sealed record WorkerHeartbeatCommand(
    Guid WorkerId,
    string Credential,
    int? Capacity,
    int? ActiveAssignmentCount,
    string? Version);

public sealed record WorkerHeartbeatResult(
    Guid WorkerId,
    string EffectiveStatus,
    bool Draining,
    long ServerTimeUnixMs,
    int HeartbeatIntervalSeconds);

// ---------- admin DTOs (never carry credentials or secrets) ----------

public sealed record GridWorkerDto(
    Guid Id,
    string WorkerKey,
    string DisplayName,
    string WorkerType,
    string Framework,
    IReadOnlyList<string> Browsers,
    string Version,
    string Status,
    string EffectiveStatus,
    int Capacity,
    int ActiveAssignmentCount,
    int AvailableSlots,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GridAssignmentDto(
    Guid Id,
    Guid ExecutionId,
    Guid ExecutionTestId,
    Guid WorkerId,
    string Status,
    int Attempt,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastRenewedAt,
    string WorkerAssignmentRef,
    Guid AssignmentToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GridStatusDto(
    int TotalWorkers,
    int AvailableWorkers,
    int BusyWorkers,
    int DrainingWorkers,
    int UnhealthyWorkers,
    int OfflineWorkers,
    int DisabledWorkers,
    int TotalCapacity,
    int ActiveAssignments,
    int AvailableSlots,
    int QueuedExecutions,
    int ActiveLeases,
    int ExpiredLeasesLastHour,
    IReadOnlyList<GridWorkerDto> Workers);

// ---------- scheduler results ----------

/// <summary>An atomically established lease plus the owning worker.</summary>
public sealed record GridClaim(
    GridAssignment Assignment,
    GridWorker Worker,
    Guid AssignmentToken);

/// <summary>
/// Read-only worker/grid administration (Slice 9). Worker credential
/// verification backs the machine-auth endpoint filter.
/// </summary>
public interface IExecutionGridService
{
    Task<RegisterWorkerResult> RegisterWorkerAsync(RegisterWorkerCommand command, CancellationToken ct);

    Task<WorkerHeartbeatResult> HeartbeatAsync(WorkerHeartbeatCommand command, CancellationToken ct);

    /// <summary>Returns the worker when the credential matches; null otherwise
    /// (unknown, mismatched, or revoked). Never throws for bad credentials.</summary>
    Task<GridWorker?> ValidateWorkerCredentialAsync(Guid workerId, string credential, CancellationToken ct);

    Task<GridWorkerDto> GetWorkerAsync(Guid workerId, CancellationToken ct);

    Task<IReadOnlyList<GridWorkerDto>> ListWorkersAsync(CancellationToken ct);

    Task<GridStatusDto> GetStatusAsync(CancellationToken ct);

    Task<GridWorkerDto> DrainWorkerAsync(Guid workerId, CancellationToken ct);

    Task<GridWorkerDto> DisableWorkerAsync(Guid workerId, CancellationToken ct);

    Task<GridWorkerDto> EnableWorkerAsync(Guid workerId, CancellationToken ct);
}

/// <summary>
/// Deterministic grid scheduler (Slice 9). Least-loaded eligible worker wins;
/// ties break on WorkerKey. All mutations are concurrency-guarded.
/// </summary>
public interface IGridScheduler
{
    /// <summary>Atomically claims a lease, or returns null when the execution
    /// must stay queued (no capacity). Throws ConflictException/NotFoundException
    /// when the execution can never run.</summary>
    Task<GridClaim?> TryClaimAsync(
        Guid executionId, IReadOnlySet<Guid> excludeWorkerIds, CancellationToken ct);

    Task RenewLeaseAsync(Guid assignmentId, CancellationToken ct);

    /// <summary>Idempotent terminal release. Never throws for already-terminal leases.</summary>
    Task ReleaseLeaseAsync(Guid executionTestId, string terminalStatus, CancellationToken ct);

    /// <summary>Opportunistic bounded expiry of leases held by lost workers.</summary>
    Task<int> ReapExpiredLeasesAsync(CancellationToken ct);

    Task<int> CountActiveAssignmentsAsync(CancellationToken ct);
}

/// <summary>Thin best-effort release facade for the engine and control plane.
/// Implementations must never throw: terminal persistence always wins.</summary>
public interface IGridLeaseManager
{
    /// <summary>
    /// Legacy method - finds active lease by execution ID and releases it.
    /// Does not provide fencing; prefer ReleaseAssignmentAsync for new code.
    /// </summary>
    Task ReleaseForExecutionAsync(Guid executionId, string terminalStatus, CancellationToken ct);

    /// <summary>
    /// Releases a specific assignment lease with token validation (fencing).
    /// </summary>
    Task ReleaseAssignmentAsync(Guid executionTestId, Guid assignmentId, Guid assignmentToken, string terminalStatus, CancellationToken ct);
}

/// <summary>Stateless worker-credential hashing (SHA-256 over salt + token).</summary>
public static class GridCredentialHasher
{
    public static string NewSalt()
    {
        var bytes = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public static string NewCredential()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return ToUrlSafe(Convert.ToBase64String(bytes));
    }

    public static string Hash(string salt, string credential)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(salt + ":" + credential));
        return Convert.ToHexString(bytes);
    }

    public static bool Verify(string salt, string expectedHash, string presented)
    {
        var actual = Hash(salt, presented);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(actual),
            System.Text.Encoding.UTF8.GetBytes(expectedHash));
    }

    public static string ToUrlSafe(string base64)
        => base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
