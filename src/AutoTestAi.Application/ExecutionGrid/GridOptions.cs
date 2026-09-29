namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>
/// Execution-grid governance (Phase 2 Slice 9, section "Grid"). All values
/// are server-side configuration with safe defaults; capacity is always
/// enforced server-side, never trusted from workers or browsers.
/// </summary>
public sealed class GridOptions
{
    public const string SectionName = "Grid";

    /// <summary>Default max concurrent assignments per worker.</summary>
    public int DefaultWorkerCapacity { get; set; } = 4;

    /// <summary>Hard ceiling for any single worker's capacity.</summary>
    public int MaxWorkerCapacity { get; set; } = 16;

    /// <summary>Heartbeat cadence workers should use (seconds).</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>Heartbeat freshness window for scheduling (seconds).</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 90;

    /// <summary>Staleness window after which a worker reads Offline (seconds).</summary>
    public int OfflineThresholdSeconds { get; set; } = 300;

    /// <summary>Assignment lease lifetime; renewed while work is active (seconds).</summary>
    public int LeaseDurationSeconds { get; set; } = 300;

    /// <summary>How long dispatch waits for grid capacity before giving up (seconds).</summary>
    public int QueueWaitTimeoutSeconds { get; set; } = 600;

    /// <summary>Global ceiling on concurrent leased streams (BRD target: 100).</summary>
    public int GlobalMaxActiveStreams { get; set; } = 100;

    /// <summary>Per-project ceiling on concurrent leased streams.</summary>
    public int ProjectMaxActiveStreams { get; set; } = 25;

    /// <summary>Shared provisioning secret for worker registration.
    /// Empty disables registration (fail closed). Server-side only.</summary>
    public string? ProvisioningToken { get; set; }

    public TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(Math.Clamp(HeartbeatTimeoutSeconds, 15, 3600));
    public TimeSpan OfflineThreshold => TimeSpan.FromSeconds(Math.Clamp(OfflineThresholdSeconds, 60, 86400));
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(Math.Clamp(LeaseDurationSeconds, 60, 3600));
    public TimeSpan QueueWaitTimeout => TimeSpan.FromSeconds(Math.Clamp(QueueWaitTimeoutSeconds, 30, 3600));
}
