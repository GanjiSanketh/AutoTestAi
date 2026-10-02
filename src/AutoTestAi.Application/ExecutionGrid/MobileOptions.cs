namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>Strongly typed mobile slot-scheduling settings (Phase 3 Slice 3C-3).
/// Mirrors GridOptions clamping conventions; all values bounded.</summary>
public sealed class MobileOptions
{
    public const string SectionName = "Mobile";

    /// <summary>Seconds a claimed slot lease remains valid without renewal.</summary>
    public int LeaseDurationSeconds { get; set; } = 300;

    /// <summary>How many expired slots each recovery pass picks up.</summary>
    public int ReapBatchSize { get; set; } = 100;

    /// <summary>How many worker/slot pairs each claim pass attempts.</summary>
    public int MaxClaimAttempts { get; set; } = 8;

    /// <summary>
    /// Grace period before a committed Claimed slot with no assignment linkage
    /// (which the atomic scheduler never produces) may be recovered.
    /// </summary>
    public int ClaimGraceSeconds { get; set; } = 600;

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(Math.Clamp(LeaseDurationSeconds, 60, 3600));
    public TimeSpan ClaimGrace => TimeSpan.FromSeconds(Math.Clamp(ClaimGraceSeconds, 60, 3600));
}
