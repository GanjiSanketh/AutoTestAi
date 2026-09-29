using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>
/// A registered Playwright execution worker (Phase 2 Slice 9). Workers
/// execute tests but are never authoritative for execution state.
/// Credential material is stored as hash+salt only — never plaintext, and
/// never projected into DTOs, logs, or audit metadata.
/// </summary>
public sealed class GridWorker : EntityBase
{
    /// <summary>Stable worker-provided identity (e.g. configured WORKER_ID).
    /// Immutable after registration; unique across the grid.</summary>
    public string WorkerKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Worker implementation type. Slice 9 supports "playwright" only;
    /// the column allows future types without redesign.</summary>
    public string WorkerType { get; set; } = "playwright";

    public string Framework { get; set; } = "playwright";

    /// <summary>Advertised browser capabilities (e.g. chromium, firefox).</summary>
    public List<string> Browsers { get; set; } = new();

    public string Version { get; set; } = string.Empty;

    /// <summary>Administrative lifecycle state. Liveness (Unhealthy/Offline)
    /// is derived from <see cref="LastHeartbeatAt"/> at read time.</summary>
    public GridWorkerStatus Status { get; set; } = GridWorkerStatus.Registered;

    /// <summary>Maximum concurrent assignments this worker accepts.</summary>
    public int Capacity { get; set; }

    /// <summary>Active (Claimed/Running) assignments. Maintained under
    /// optimistic concurrency; never allowed to exceed <see cref="Capacity"/>.</summary>
    public int ActiveAssignmentCount { get; set; }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>SHA-256(salt + credential) hex. The credential itself is
    /// returned once at registration and never stored.</summary>
    public string CredentialHash { get; set; } = string.Empty;

    public string CredentialSalt { get; set; } = string.Empty;

    /// <summary>How the control plane reaches this worker (callback URL).
    /// Server-side only; never exposed to browsers.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Optimistic concurrency token guarding capacity accounting.</summary>
    public uint RowVersion { get; set; }
}

/// <summary>
    /// An assignment lease binding one execution test to one worker
    /// (Phase 2 Slice 9). Exactly one non-terminal lease may exist per
    /// execution test (unique filtered index); claiming is atomic.
    /// </summary>
    public sealed class GridAssignment : EntityBase
    {
        public Guid ExecutionId { get; set; }

        public Guid ExecutionTestId { get; set; }

        public Guid WorkerId { get; set; }

        public GridAssignmentStatus Status { get; set; } = GridAssignmentStatus.Claimed;

        /// <summary>Mirrors the execution test attempt this lease serves.</summary>
        public int Attempt { get; set; } = 1;

        public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset ExpiresAt { get; set; }

        public DateTimeOffset LastRenewedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Worker-side assignment identifier (test id hex, Slice 5 contract).</summary>
        public string WorkerAssignmentRef { get; set; } = string.Empty;

        /// <summary>
        /// Cryptographically random token identifying this specific assignment attempt.
        /// Used to fence stale worker completions after lease expiry/requeue.
        /// </summary>
        public Guid AssignmentToken { get; set; } = Guid.NewGuid();
    }
