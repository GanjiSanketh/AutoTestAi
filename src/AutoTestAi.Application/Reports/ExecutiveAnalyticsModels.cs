namespace AutoTestAi.Application.Reports;

// ---------- executive analytics DTOs (Slice 12; all database-backed, deterministic) ----------

/// <summary>One readiness input with its weight and contribution (fully transparent).</summary>
public sealed record ReadinessComponentDto(
    string Component,
    double? Value,
    double Weight,
    double? Contribution,
    string Threshold,
    string Detail);

public sealed record ExecutiveAnalyticsDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    int TerminalExecutions,
    int TotalExecutions,
    double? PassRate,
    double? FailRate,
    double? FlakinessIndex,
    int FlakyTests,
    int EligibleTests,
    double? AutomationCoverage,
    int AutomatedCases,
    int EligibleCases,
    double? ReleaseReadiness,
    string ReadinessStatus,
    IReadOnlyList<ReadinessComponentDto> ReadinessComponents,
    int OpenCriticalHighDefects,
    double? DefectsPer100Executions,
    int DefectsCreated,
    /// <summary>Defects created in the window per automated case (density aid).</summary>
    double? DefectsPerCase,
    double? AverageDurationMs,
    long? TotalDurationMs,
    int DurationSampleCount,
    double? HealingSuccessRate,
    int HealingAttempts,
    int HealingApplied,
    int UnstableExecutions,
    int CancelledExecutions);

public sealed record FlakinessTrendPointDto(
    string Date,
    int EligibleTests,
    int FlakyTests,
    double? Index);

public sealed record FlakinessTrendDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    string Granularity,
    IReadOnlyList<FlakinessTrendPointDto> Points);

public sealed record FlakyTestDto(
    Guid TestCaseId,
    string TestKey,
    string Title,
    string? Module,
    string Priority,
    string? Framework,
    string? Platform,
    int TotalExecutions,
    int Passed,
    int Failed,
    int Other,
    bool IsFlaky,
    double? FlakinessRate,
    string? LastOutcome,
    DateTimeOffset? LastRunAt,
    int HealingAttempts,
    int HealedRuns);

public sealed record FlakyTestsFilters(
    string? Search,
    bool FlakyOnly,
    int MinExecutions,
    string? Module,
    string? Priority,
    string? Framework,
    bool HealedOnly);

public sealed record HealingTrendPointDto(
    string Date,
    int Attempts,
    int Applied);

public sealed record HealingAnalyticsDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Attempts,
    int Applied,
    int Failed,
    int Deterministic,
    int AiAssisted,
    double? SuccessRate,
    int TestsWithHealing,
    int ExecutionsWithHealing,
    int TestsHealedAndFlaky,
    IReadOnlyList<HealingTrendPointDto> Points);

public sealed record DurationTrendPointDto(
    string Date,
    int Count,
    double? AverageMs);

public sealed record AgingBucketDto(string Name, int Count);

public sealed record DurationAnalyticsDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    double? AverageMs,
    long? MinMs,
    long? MaxMs,
    long? TotalMs,
    double? P50Ms,
    double? P90Ms,
    bool SlaConfigured,
    IReadOnlyList<AgingBucketDto> OpenDefectAging,
    IReadOnlyList<DurationTrendPointDto> Points);

public sealed record ReleaseReadinessDto(
    Guid ProjectId,
    DateTimeOffset From,
    DateTimeOffset To,
    double? Score,
    string Status,
    IReadOnlyList<ReadinessComponentDto> Components,
    int SampleSize);

// ---------- query row records (server-side grouped primitives; never leave the store raw) ----------

/// <summary>Per-test verdict counts for one observation window (terminal executions only).</summary>
public sealed record TestOutcomeRow(
    Guid TestCaseId,
    int Passed,
    int Failed,
    int Other,
    DateTimeOffset? LastRunAt);

/// <summary>Per-test UTC-day status counts (terminal executions only).</summary>
public sealed record TestDayOutcomeRow(
    Guid TestCaseId,
    int Year,
    int Month,
    int Day,
    string Status,
    int Count);

/// <summary>Latest execution per test (for last-outcome display).</summary>
public sealed record TestLastRunRow(
    Guid TestCaseId,
    Guid ExecutionId,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>Healing activity per test case within a window.</summary>
public sealed record TestHealingRow(
    Guid TestCaseId,
    int Attempts,
    int Applied);

/// <summary>Coverage primitives: eligible vs. automated test cases.</summary>
public sealed record CoverageCounts(int EligibleCases, int AutomatedCases);

/// <summary>Test-case display metadata for the flakiness report.</summary>
public sealed record TestCaseMetaRow(
    Guid TestCaseId,
    string TestKey,
    string Title,
    string? Module,
    string Priority,
    string? Framework,
    string? Platform);

/// <summary>Duration primitives over valid (non-null, non-negative) samples.</summary>
public sealed record DurationStats(
    int Count,
    double? AverageMs,
    long? MinMs,
    long? MaxMs,
    long? TotalMs);

public sealed record DurationDayRow(
    int Year,
    int Month,
    int Day,
    int Count,
    double? AverageMs);

public sealed record HealingDayRow(
    int Year,
    int Month,
    int Day,
    int Attempts,
    int Applied);

public sealed record HealingStats(
    int Attempts,
    int Applied,
    int Deterministic,
    int AiAssisted,
    int TestsWithHealing,
    int ExecutionsWithHealing);

/// <summary>CSV export envelope (bounded, deterministic order, safe fields only).</summary>
public sealed record FlakyTestsExport(string FileName, string ContentType, byte[] Content);
