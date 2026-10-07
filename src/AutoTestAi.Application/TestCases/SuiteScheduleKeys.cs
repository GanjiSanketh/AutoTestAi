using System.Text.RegularExpressions;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Deterministic idempotency-basis derivation for scheduled suite runs
/// (Phase 4 Slice 9B). The basis is constant within one schedule-action run
/// (activity retries converge) and distinct across ticks (each tick is a new
/// run). Fits the 60-char suite base budget: "sched-" (6) + 32 (schedule) +
/// "-" + 20 (run prefix) = 59.
/// </summary>
public static class SuiteScheduleKeys
{
    public const string Prefix = "sched-";

    public static string ForActionRun(Guid scheduleId, string actionRunId)
    {
        ArgumentNullException.ThrowIfNull(actionRunId);
        var run = actionRunId.Replace("-", string.Empty, StringComparison.Ordinal);
        if (run.Length > 20)
            run = run[..20];
        return $"{Prefix}{scheduleId:N}-{run}";
    }

    public static string ForManualTrigger(Guid scheduleId, string idempotencyKey)
        => $"{Prefix}{scheduleId:N}-{idempotencyKey}";
}

/// <summary>
/// Deterministic Temporal schedule/action identifiers (Phase 4 Slice 9B).
/// One remote schedule per row; the tick action reuses a fixed base id and
/// the server uniquifies per tick under AllowAll / enforces single-open
/// under Skip.
/// </summary>
public static class SuiteScheduleIds
{
    public static string ForSchedule(Guid scheduleId) => $"suite-schedule-{scheduleId:N}";
}

/// <summary>
/// Lightweight cron shape validation (Phase 4 Slice 9B). Accepts 5-field
/// minute-first or 6-field (leading seconds) expressions using digits, names,
/// ranges, steps, and lists. Temporal is authoritative: this rejects obvious
/// garbage locally so malformed input is a 400 without a Temporal round-trip.
/// </summary>
public static partial class CronValidation
{
    [GeneratedRegex(@"^[A-Za-z0-9\*/,\-?LW#]+$")]
    private static partial Regex FieldPattern();

    public static bool IsPlausible(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;
        var fields = expression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (5 or 6))
            return false;
        return fields.All(f => f.Length <= 64 && FieldPattern().IsMatch(f));
    }
}
