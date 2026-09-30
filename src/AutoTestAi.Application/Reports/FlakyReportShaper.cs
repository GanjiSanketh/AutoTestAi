namespace AutoTestAi.Application.Reports;

/// <summary>
/// Pure in-memory shaping for the test-level flakiness report (Slice 12).
/// Inputs are server-side grouped aggregates; filtering, deterministic
/// sorting (explicit allowlist, nulls last), and paging happen here so the
/// rules are unit-testable without a database. No AI ranking.
/// </summary>
public sealed record FlakyCandidate(
    Guid TestCaseId,
    string TestKey,
    string Title,
    string? Module,
    string Priority,
    string? Framework,
    string? Platform,
    int Passed,
    int Failed,
    int Other,
    DateTimeOffset? LastRunAt,
    int HealingAttempts,
    int HealedRuns)
{
    public int TotalExecutions => Passed + Failed + Other;
    public bool IsFlaky => AnalyticsCalculations.IsFlaky(Passed, Failed);
    public double? FlakinessRate => AnalyticsCalculations.TestFlakinessRate(Passed, Failed);
};

public static class FlakyReportShaper
{
    public static readonly IReadOnlySet<string> SortKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "testKey", "title", "executions", "flakinessRate", "lastRun" };

    public const string DefaultSort = "flakinessRate";

    public static IReadOnlyList<FlakyCandidate> ApplyFilters(
        IEnumerable<FlakyCandidate> candidates, FlakyTestsFilters filters)
    {
        var query = candidates;
        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var term = filters.Search.Trim().ToLowerInvariant();
            query = query.Where(c =>
                c.TestKey.ToLowerInvariant().Contains(term) ||
                c.Title.ToLowerInvariant().Contains(term));
        }
        if (filters.FlakyOnly)
            query = query.Where(c => c.IsFlaky);
        if (filters.MinExecutions > 0)
            query = query.Where(c => c.TotalExecutions >= filters.MinExecutions);
        if (!string.IsNullOrWhiteSpace(filters.Module))
            query = query.Where(c => string.Equals(
                c.Module?.Trim(), filters.Module.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filters.Priority))
            query = query.Where(c => string.Equals(
                c.Priority, filters.Priority.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filters.Framework))
            query = query.Where(c => string.Equals(
                c.Framework?.Trim(), filters.Framework.Trim(), StringComparison.OrdinalIgnoreCase));
        if (filters.HealedOnly)
            query = query.Where(c => c.HealingAttempts > 0);
        return query.ToList();
    }

    /// <summary>Deterministic ordering: requested key, nulls last, TestKey tiebreak.</summary>
    public static IReadOnlyList<FlakyCandidate> ApplySort(
        IEnumerable<FlakyCandidate> candidates, string sort, bool descending)
    {
        var key = (sort ?? DefaultSort).Trim();
        if (!SortKeys.Contains(key)) key = DefaultSort;
        IOrderedEnumerable<FlakyCandidate> ordered = (key.ToLowerInvariant(), descending) switch
        {
            ("title", false) => candidates.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase),
            ("title", true) => candidates.OrderByDescending(c => c.Title, StringComparer.OrdinalIgnoreCase),
            ("executions", false) => candidates.OrderBy(c => c.TotalExecutions),
            ("executions", true) => candidates.OrderByDescending(c => c.TotalExecutions),
            ("flakinessrate", false) => candidates
                .OrderBy(c => c.FlakinessRate is null).ThenBy(c => c.FlakinessRate),
            ("flakinessrate", true) => candidates
                .OrderBy(c => c.FlakinessRate is null).ThenByDescending(c => c.FlakinessRate),
            ("lastrun", false) => candidates
                .OrderBy(c => c.LastRunAt is null).ThenBy(c => c.LastRunAt),
            ("lastrun", true) => candidates
                .OrderBy(c => c.LastRunAt is null).ThenByDescending(c => c.LastRunAt),
            ("testkey", true) => candidates.OrderByDescending(c => c.TestKey, StringComparer.OrdinalIgnoreCase),
            _ => candidates.OrderBy(c => c.TestKey, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ThenBy(c => c.TestKey, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Pure daily/weekly flakiness trend shaping (null index = no data, never zero).</summary>
public static class FlakinessTrendBuilder
{
    public static IReadOnlyList<FlakinessTrendPointDto> BuildDaily(
        ReportDateRange range, IReadOnlyList<TestDayOutcomeRow> rows)
    {
        var byDay = rows
            .GroupBy(r => new DateTime(r.Year, r.Month, r.Day, 0, 0, 0, DateTimeKind.Utc))
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.TestCaseId)
                    .Select(t => (Passed: t.Where(x => x.Status == "Passed").Sum(x => x.Count),
                        Failed: t.Where(x => x.Status == "Failed").Sum(x => x.Count)))
                    .ToList());
        var points = new List<FlakinessTrendPointDto>();
        var start = StartOfDayUtc(range.From);
        for (var day = start; day <= StartOfDayUtc(range.To); day = day.AddDays(1))
        {
            if (!byDay.TryGetValue(day, out var tests) || tests.Count == 0)
            {
                points.Add(new FlakinessTrendPointDto(day.ToString("yyyy-MM-dd"), 0, 0, null));
                continue;
            }
            var eligible = tests.Count(t => t.Passed + t.Failed >= AnalyticsCalculations.MinVerdictsForFlakiness);
            var flaky = tests.Count(t => AnalyticsCalculations.IsFlaky(t.Passed, t.Failed)
                && t.Passed + t.Failed >= AnalyticsCalculations.MinVerdictsForFlakiness);
            points.Add(new FlakinessTrendPointDto(
                day.ToString("yyyy-MM-dd"), eligible, flaky,
                AnalyticsCalculations.FlakinessIndex(flaky, eligible)));
        }
        return points;
    }

    /// <summary>Monday-start weekly rollup of daily points (index recomputed from counts).</summary>
    public static IReadOnlyList<FlakinessTrendPointDto> RollupWeekly(
        IReadOnlyList<FlakinessTrendPointDto> daily)
    {
        return daily
            .GroupBy(p => StartOfWeekMonday(DateOnly.Parse(p.Date)))
            .OrderBy(g => g.Key)
            .Select(g => new FlakinessTrendPointDto(
                g.Key.ToString("yyyy-MM-dd"),
                g.Sum(p => p.EligibleTests),
                g.Sum(p => p.FlakyTests),
                AnalyticsCalculations.FlakinessIndex(g.Sum(p => p.FlakyTests), g.Sum(p => p.EligibleTests))))
            .ToList();
    }

    private static DateTime StartOfDayUtc(DateTimeOffset value)
        => new(value.UtcDateTime.Year, value.UtcDateTime.Month, value.UtcDateTime.Day,
            0, 0, 0, DateTimeKind.Utc);

    private static DateOnly StartOfWeekMonday(DateOnly day)
        => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}

/// <summary>Minimal RFC-4180 CSV writer for bounded analytics exports (pure, tested).</summary>
public static class CsvExporter
{
    public static byte[] ExportFlakyTests(IReadOnlyList<FlakyTestDto> rows)
    {
        var lines = new List<string>(rows.Count + 1)
        {
            "testKey,title,module,priority,framework,platform,executions,passed,failed,other,isFlaky,flakinessRate,lastOutcome,lastRunAt,healingAttempts,healedRuns",
        };
        foreach (var r in rows)
        {
            lines.Add(string.Join(",", new[]
            {
                Cell(r.TestKey), Cell(r.Title), Cell(r.Module), Cell(r.Priority),
                Cell(r.Framework), Cell(r.Platform),
                r.TotalExecutions.ToString(), r.Passed.ToString(), r.Failed.ToString(), r.Other.ToString(),
                r.IsFlaky ? "true" : "false",
                r.FlakinessRate.HasValue ? r.FlakinessRate.Value.ToString("F1") : string.Empty,
                Cell(r.LastOutcome),
                r.LastRunAt.HasValue ? r.LastRunAt.Value.UtcDateTime.ToString("o") : string.Empty,
                r.HealingAttempts.ToString(), r.HealedRuns.ToString(),
            }));
        }
        return System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Any(c => c is ',' or '"' or '\r' or '\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}
