namespace AutoTestAi.Application.Reports;

/// <summary>Advisory-only flakiness risk forecast for one test (Phase 4
/// Slice 1). Null score/band with empty factors means insufficient history
/// — never a low-risk claim. Informational only: never gates, mutates, or
/// quarantines anything.</summary>
public sealed record FlakinessRiskForecast(
    int? RiskScore,
    string? RiskBand,
    IReadOnlyList<string> RiskFactors);

/// <summary>
/// Deterministic flakiness risk forecasting (Phase 4 Slice 1). Pure
/// functions over ordered verdict history — no database, no AI, no clock.
/// Formula (documented for independent reproduction):
///
/// Input: pass/fail verdicts newest-first, plus window totals (passed,
/// failed) for the minimum-history rule. Other statuses are excluded
/// upstream, exactly like the flakiness aggregates.
///
/// Minimum history: passed + failed &lt;
/// <see cref="AnalyticsCalculations.MinVerdictsForFlakiness"/> (2) yields
/// null score/band and empty factors.
///
/// Signals (each 0–100):
/// - Recent failure share: 100 * fails in the newest min(5, N) verdicts /
///   min(5, N).
/// - Trend deterioration: newest ceil(N/2) verdicts vs. the older remainder
///   (failure shares); deterioration = max(0, recentShare - olderShare).
/// - Consecutive failure streak from the newest verdict: 0→0, 1→25, 2→50,
///   3→75, ≥4→100.
///
/// Combination: score = round_half_away(0.50 * recent + 0.30 * trend +
/// 0.20 * streak), clamped to 0–100. Bands: 0–39 Low, 40–69 Medium,
/// 70–100 High.
///
/// Factors (deterministic rules, in fixed order):
/// - recent share &gt;= 50 → "Recent failure rate is elevated"
/// - trend deterioration &gt; 0 → "Failure trend is deteriorating"
/// - streak &gt;= 2 → "Consecutive failures detected"
/// - no rule fired and score &gt; 0 → "Intermittent failures in history"
///
/// Worked example (newest-first [Fail, Fail, Pass, Fail, Pass]):
/// recent = 100*3/5 = 60; recent half [F,F,P] = 66.67 vs older [F,P] = 50,
/// deterioration = 16.67; streak = 2 → 50.
/// score = round(0.5*60 + 0.3*16.67 + 0.2*50) = round(45.0) = 45 (Medium),
/// all three factors fire.
/// </summary>
public static class FlakinessForecast
{
    /// <summary>Newest verdicts considered for the recency signal.</summary>
    public const int RecentWindowSize = 5;

    /// <summary>Most verdicts retained per test for forecasting (bounded memory).</summary>
    public const int MaxVerdictsPerTest = 30;

    public const string BandLow = "Low";
    public const string BandMedium = "Medium";
    public const string BandHigh = "High";

    public static FlakinessRiskForecast Forecast(
        IReadOnlyList<bool> newestFirstPass, int passed, int failed)
    {
        if (passed + failed < AnalyticsCalculations.MinVerdictsForFlakiness)
            return new FlakinessRiskForecast(null, null, Array.Empty<string>());

        var verdicts = newestFirstPass;
        var window = verdicts.Take(RecentWindowSize).ToList();
        // Defensive: verdict rows mirror the window totals; an empty
        // sequence with sufficient totals cannot score.
        if (window.Count == 0)
            return new FlakinessRiskForecast(null, null, Array.Empty<string>());

        var recentShare = 100.0 * window.Count(v => !v) / window.Count;

        var recentCount = (verdicts.Count + 1) / 2;
        var recentHalf = verdicts.Take(recentCount).ToList();
        var olderHalf = verdicts.Skip(recentCount).ToList();
        var recentHalfShare = 100.0 * recentHalf.Count(v => !v) / recentHalf.Count;
        var olderHalfShare = olderHalf.Count == 0
            ? recentHalfShare
            : 100.0 * olderHalf.Count(v => !v) / olderHalf.Count;
        var deterioration = Math.Max(0.0, recentHalfShare - olderHalfShare);

        var streak = 0;
        foreach (var pass in verdicts)
        {
            if (pass) break;
            streak++;
        }
        var streakScore = Math.Min(streak * 25, 100);

        var score = (int)Math.Round(
            0.50 * recentShare + 0.30 * deterioration + 0.20 * streakScore,
            MidpointRounding.AwayFromZero);
        score = Math.Clamp(score, 0, 100);

        var band = score <= 39 ? BandLow : score <= 69 ? BandMedium : BandHigh;
        var factors = new List<string>();
        if (recentShare >= 50) factors.Add("Recent failure rate is elevated");
        if (deterioration > 0) factors.Add("Failure trend is deteriorating");
        if (streak >= 2) factors.Add("Consecutive failures detected");
        if (factors.Count == 0 && score > 0) factors.Add("Intermittent failures in history");
        return new FlakinessRiskForecast(score, band, factors);
    }

    public static string BandFor(int score)
        => score <= 39 ? BandLow : score <= 69 ? BandMedium : BandHigh;
}
