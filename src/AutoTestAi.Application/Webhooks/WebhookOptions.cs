namespace AutoTestAi.Application.Webhooks;

/// <summary>Slice 3B webhook ingress tuning (Phase 3). All values are safe defaults.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Maximum accepted webhook body in bytes (default 1 MiB).</summary>
    public long MaxBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Deliveries per minute per project (process-local sliding window).</summary>
    public int MaxDeliveriesPerMinutePerProject { get; set; } = 100;

    /// <summary>Maximum suite members fanned out per delivery (NFR-1 bound).</summary>
    public int MaxSuiteMembers { get; set; } = 100;

    /// <summary>Seconds an Accepted delivery claim is valid before reclaimable.</summary>
    public int ClaimLeaseSeconds { get; set; } = 300;

    /// <summary>Seconds between crash-recovery reconciliation passes.</summary>
    public int ReconciliationIntervalSeconds { get; set; } = 60;

    /// <summary>How many stale Accepted rows each reconciliation pass picks up.</summary>
    public int ReconciliationBatchSize { get; set; } = 25;

    /// <summary>Delivery history retention in days (default 90; purge deferred — documented).</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Master switch for async background processing (tests disable it).</summary>
    public bool BackgroundProcessingEnabled { get; set; } = true;
}
