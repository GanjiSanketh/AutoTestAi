namespace AutoTestAi.Application.Reports;

/// <summary>
/// Deterministic analytics formulas (Slice 12). Pure functions over grouped
/// primitives — no database, no AI, no clock. Every public metric documents
/// its numerator, denominator, exclusions, and insufficient-data behavior so
/// another engineer can reproduce it independently.
/// </summary>
public static class AnalyticsCalculations
{
    /// <summary>Minimum eligible verdict executions before a test counts toward flakiness.</summary>
    public const int MinVerdictsForFlakiness = 2;

    /// <summary>
    /// Pass rate: Passed ÷ terminal executions (Passed/Failed/Cancelled/
    /// TimedOut/Error; Queued/Running excluded). Null when nothing is terminal.
    /// </summary>
    public static double? PassRate(int passed, int terminal)
        => terminal <= 0 ? null : (double)passed / terminal;

    /// <summary>
    /// A test is flaky when it shows inconsistent terminal verdicts: at least
    /// one Passed AND at least one Failed within the window. Always-failing is
    /// failure-prone (not flaky); always-passing is not flaky. Cancelled runs
    /// are excluded; TimedOut/Error (unstable) neither create nor block flakiness.
    /// </summary>
    public static bool IsFlaky(int passed, int failed)
        => passed > 0 && failed > 0;

    /// <summary>
    /// Test flakiness rate: minority-verdict share of eligible verdicts,
    /// 100·min(P,F)/(P+F), range 0–50. Null when fewer than 2 verdicts.
    /// </summary>
    public static double? TestFlakinessRate(int passed, int failed)
    {
        var total = passed + failed;
        return total < MinVerdictsForFlakiness ? null : 100.0 * Math.Min(passed, failed) / total;
    }

    /// <summary>
    /// Project flakiness index: 100·(flaky tests)/(tests with ≥2 eligible
    /// verdicts). Null (insufficient data — never zero) when no test qualifies.
    /// A single isolated failure cannot move the index without a second verdict.
    /// </summary>
    public static double? FlakinessIndex(int flakyTests, int eligibleTests)
        => eligibleTests <= 0 ? null : 100.0 * flakyTests / eligibleTests;

    /// <summary>
    /// Automation coverage: 100·(non-archived cases whose latest version is
    /// Approved)/(non-archived cases). Null when there are no eligible cases.
    /// Draft/never-approved cases are not automated yet; archived cases are
    /// excluded from both sides.
    /// </summary>
    public static double? AutomationCoverage(int automated, int eligible)
        => eligible <= 0 ? null : 100.0 * automated / eligible;

    /// <summary>
    /// Defect density proxy: 100·(defects created in window)/(terminal
    /// executions in window). Null when there are no terminal executions.
    /// Creation-date based; defects may reference older executions.
    /// </summary>
    public static double? DefectsPer100Executions(int defectsCreated, int terminalExecutions)
        => terminalExecutions <= 0 ? null : 100.0 * defectsCreated / terminalExecutions;

    /// <summary>
    /// Healing success rate: 100·(applied attempts)/(all attempts), where
    /// applied means Slice 11 status Applied with WasApplied. Null when empty.
    /// </summary>
    public static double? HealingSuccessRate(int applied, int attempts)
        => attempts <= 0 ? null : 100.0 * applied / attempts;

    /// <summary>Nearest-rank percentile over a pre-sorted sample. Null when empty.</summary>
    public static double? Percentile(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        var rank = Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count), 1, sorted.Count);
        return sorted[rank - 1];
    }

    /// <summary>
    /// Deterministic release-readiness score (0–100, informational only — never
    /// an autonomous release decision). Inputs (all window-scoped, all nullable):
    /// passRate 0–1 (weight 35), flakinessIndex 0–100 (health = 100−index, weight 20),
    /// coverage 0–100 (weight 15), openCriticalHighDefects ≥0 (health = max(0,100−25·n),
    /// weight 15), completionRate 0–1 terminal÷total (weight 15). Components with
    /// unknown values are excluded and remaining weights renormalize. Null when
    /// passRate is unknown (no terminal executions).
    /// Bands: ≥80 Ready, ≥60 Caution, else NeedsAttention.
    /// </summary>
    public static (double? Score, string Status, IReadOnlyList<ReadinessComponentDto> Components) ReleaseReadiness(
        double? passRate,
        double? flakinessIndex,
        double? coverage,
        int openCriticalHighDefects,
        double? completionRate,
        int sampleSize)
    {
        var components = new List<ReadinessComponentDto>();
        if (passRate.HasValue)
            components.Add(new("PassRate", passRate.Value * 100, 35, null,
                "Terminal pass rate ≥ 80%", $"Passed share of {sampleSize} terminal executions."));
        if (flakinessIndex.HasValue)
            components.Add(new("FlakinessHealth", 100 - flakinessIndex.Value, 20, null,
                "Flakiness index ≤ 10%", "100 minus the project flakiness index."));
        if (coverage.HasValue)
            components.Add(new("AutomationCoverage", coverage.Value, 15, null,
                "Coverage ≥ 70%", "Approved automated cases over eligible cases."));
        components.Add(new("DefectHealth", Math.Max(0, 100 - 25 * openCriticalHighDefects), 15, null,
            "Zero open Critical/High defects", $"{openCriticalHighDefects} open Critical/High defects (−25 each)."));
        if (completionRate.HasValue)
            components.Add(new("CompletionHealth", completionRate.Value * 100, 15, null,
                "Terminal share of started executions", "Terminal executions over all executions in the window."));

        if (!passRate.HasValue)
            return (null, "InsufficientData", components);

        var weight = components.Sum(c => c.Weight);
        if (weight <= 0) return (null, "InsufficientData", components);
        var score = components.Sum(c => c.Value!.Value * c.Weight) / weight;
        var filled = components.Select(c => c with { Contribution = c.Value!.Value * c.Weight / weight }).ToList();
        var status = score >= 80 ? "Ready" : score >= 60 ? "Caution" : "NeedsAttention";
        return (score, status, filled);
    }
}
