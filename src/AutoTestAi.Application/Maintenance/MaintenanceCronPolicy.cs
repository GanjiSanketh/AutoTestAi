using AutoTestAi.Application.TestCases;

namespace AutoTestAi.Application.Maintenance;

/// <summary>
/// Minimum-cadence policy for maintenance scan schedules (Phase 4 Slice 10).
/// The scan aggregates up to 2000 evidence rows and 2000 verdict rows across
/// up to 200 tests, so schedules must fire at most once per hour. This is a
/// conservative static floor over the minute (and seconds, for 6-field)
/// fields: a schedule fires at most hourly iff its minute field is a single
/// literal minute. Temporal remains authoritative for actual firing; this
/// rejects obvious sub-hourly cadences locally so they are a 400 without a
/// Temporal round-trip. Reuses <see cref="CronValidation"/> shape checks.
/// </summary>
public static class MaintenanceCronPolicy
{
    /// <summary>
    /// True when the expression can fire more often than once per hour.
    /// Callers must run <see cref="CronValidation.IsPlausible"/> first;
    /// implausible expressions are treated as too frequent (invalid either way).
    /// </summary>
    public static bool IsMoreFrequentThanHourly(string? expression)
    {
        if (!CronValidation.IsPlausible(expression))
            return true;
        var fields = expression!.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // 5-field: minute hour day month weekday. 6-field: leading seconds.
        var minute = fields.Length == 6 ? fields[1] : fields[0];
        if (fields.Length == 6 && !IsSingleLiteral(fields[0]))
            return true;
        return !IsSingleLiteral(minute);
    }

    private static bool IsSingleLiteral(string field)
    {
        if (string.IsNullOrEmpty(field))
            return false;
        foreach (var ch in field)
        {
            if (ch is '*' or '/' or ',' or '-' or '#' or '?' or 'L' or 'W')
                return false;
            if (!char.IsLetterOrDigit(ch))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Deterministic Temporal schedule identifiers (Phase 4 Slice 10). One
/// remote schedule per row in its own namespace so maintenance and suite
/// schedules can never collide.
/// </summary>
public static class MaintenanceScanScheduleIds
{
    public static string ForSchedule(Guid scheduleId) => $"maintenance-scan-{scheduleId:N}";
}
