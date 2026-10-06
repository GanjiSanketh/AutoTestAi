using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Maintenance;

/// <summary>
/// Deterministic web-locator maintenance detector (Phase 4 Slice 4).
/// Pure functions over normalized evidence: no database, no AI, no clock
/// beyond the caller-supplied instant, no HTTP context. A candidate exists
/// only when the SAME exact original → proposed locator pair was
/// deterministically healed and applied across enough distinct executions.
/// Failed verdicts and flakiness corroborate; they never create proposals.
/// </summary>
public static class MaintenanceDetector
{
    public const int MinOccurrences = 3;
    public const int MinConfidence = 60;
    public static readonly TimeSpan EvidenceWindow = TimeSpan.FromDays(30);
    public const int MaxExecutions = 10;
    public const int MinFailedCorroboration = 2;
    public const double MinHealingSuccessRatio = 0.75;

    public const string SignalHealedLocator = "healed-locator";

    /// <summary>Closed web locator vocabulary (mirrors the Playwright worker
    /// LocatorKind set: css/xpath/role/text/testid). Mobile strategies and
    /// anything else are rejected.</summary>
    public static readonly IReadOnlySet<string> WebStrategies =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "css", "xpath", "role", "text", "testid" };

    private const string RedactedMarker = "[REDACTED]";

    /// <summary>One normalized healing-attempt row joined with its execution verdict.</summary>
    public sealed record EvidenceRow(
        Guid AttemptId,
        Guid ProjectId,
        Guid TestCaseId,
        Guid? TestCaseVersionId,
        int StepOrder,
        string StepAction,
        string? OriginalStrategy,
        string? OriginalValue,
        string? RecoveredStrategy,
        string? RecoveredValue,
        bool IsAiAssisted,
        bool WasApplied,
        SelfHealingStatus Status,
        SelfHealingStrategy HealingStrategy,
        Guid ExecutionId,
        ExecutionStatus ExecutionStatus,
        FailureClassification Classification,
        DateTimeOffset CreatedAt);

    /// <summary>Latest test-case version snapshot for stale-version checks.</summary>
    public sealed record VersionSnapshot(
        Guid VersionId,
        ReviewStatus ReviewStatus,
        IReadOnlyList<VersionStep> Steps);

    public sealed record VersionStep(int Order, string Action, string? Target);

    /// <summary>Failed-verdict corroboration per (test case, version).</summary>
    public sealed record FailedVerdictInfo(int FailedCount, bool HasAppEnvFailure);

    public sealed record Candidate(
        Guid TestCaseId,
        Guid TestCaseVersionId,
        int StepOrder,
        string StepAction,
        string OriginalStrategy,
        string OriginalValue,
        string ProposedStrategy,
        string ProposedValue,
        SelfHealingStrategy HealingStrategy,
        int OccurrenceCount,
        int Confidence,
        IReadOnlyList<string> ConfidenceFactors,
        int FailedCorroborationCount,
        string? ForecastBand,
        double HealingSuccessRatio,
        IReadOnlyList<Guid> ExecutionIds,
        IReadOnlyList<Guid> HealingAttemptIds,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen);

    public sealed record DetectionResult(
        IReadOnlyList<Candidate> Candidates,
        int EvaluatedGroups,
        int SkippedGroups);

    /// <summary>
    /// Runs detection over caller-loaded evidence. Deterministic: identical
    /// inputs always yield identical candidates in identical order
    /// (confidence desc, then test/step/version ids).
    /// </summary>
    public static DetectionResult Detect(
        Guid projectId,
        DateTimeOffset now,
        IReadOnlyList<EvidenceRow> evidence,
        IReadOnlyDictionary<Guid, VersionSnapshot> latestVersions,
        IReadOnlySet<Guid> excludedTestCaseIds,
        IReadOnlyDictionary<(Guid TestCaseId, Guid VersionId), FailedVerdictInfo> failedVerdicts,
        IReadOnlyDictionary<Guid, string?> forecastBands)
    {
        var cutoff = now - EvidenceWindow;
        var rows = evidence
            .Where(r => r.ProjectId == projectId && r.CreatedAt >= cutoff)
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.ExecutionId)
            .ToList();

        var groups = rows
            .GroupBy(r => new
            {
                r.TestCaseId,
                VersionId = r.TestCaseVersionId,
                r.StepOrder,
                Action = (r.StepAction ?? string.Empty).Trim(),
                OriginalStrategy = (r.OriginalStrategy ?? string.Empty).Trim().ToLowerInvariant(),
                OriginalValue = r.OriginalValue ?? string.Empty,
                ProposedStrategy = (r.RecoveredStrategy ?? string.Empty).Trim().ToLowerInvariant(),
                ProposedValue = r.RecoveredValue ?? string.Empty,
            })
            .OrderBy(g => g.Key.TestCaseId)
            .ThenBy(g => g.Key.VersionId)
            .ThenBy(g => g.Key.StepOrder)
            .ThenBy(g => g.Key.OriginalStrategy, StringComparer.Ordinal)
            .ThenBy(g => g.Key.OriginalValue, StringComparer.Ordinal)
            .ThenBy(g => g.Key.ProposedStrategy, StringComparer.Ordinal)
            .ThenBy(g => g.Key.ProposedValue, StringComparer.Ordinal)
            .ToList();

        var candidates = new List<Candidate>();
        var skipped = 0;
        foreach (var group in groups)
        {
            var candidate = EvaluateGroup(
                group.Key.TestCaseId, group.Key.VersionId, group.Key.StepOrder, group.Key.Action,
                group.Key.OriginalStrategy, group.Key.OriginalValue,
                group.Key.ProposedStrategy, group.Key.ProposedValue,
                group.ToList(), latestVersions, excludedTestCaseIds,
                failedVerdicts, forecastBands);
            if (candidate is null) skipped++;
            else candidates.Add(candidate);
        }

        candidates.Sort(static (a, b) =>
        {
            var byConfidence = b.Confidence.CompareTo(a.Confidence);
            if (byConfidence != 0) return byConfidence;
            var byTest = a.TestCaseId.CompareTo(b.TestCaseId);
            if (byTest != 0) return byTest;
            var byStep = a.StepOrder.CompareTo(b.StepOrder);
            if (byStep != 0) return byStep;
            return a.TestCaseVersionId.CompareTo(b.TestCaseVersionId);
        });
        return new DetectionResult(candidates, groups.Count, skipped);
    }

    private static Candidate? EvaluateGroup(
        Guid testCaseId, Guid? versionId, int stepOrder, string stepAction,
        string originalStrategy, string originalValue,
        string proposedStrategy, string proposedValue,
        List<EvidenceRow> rows,
        IReadOnlyDictionary<Guid, VersionSnapshot> latestVersions,
        IReadOnlySet<Guid> excludedTestCaseIds,
        IReadOnlyDictionary<(Guid TestCaseId, Guid VersionId), FailedVerdictInfo> failedVerdicts,
        IReadOnlyDictionary<Guid, string?> forecastBands)
    {
        // Identity and shape: an actionable proposal needs one exact pair.
        if (versionId is null || versionId.Value == Guid.Empty) return null;
        if (stepOrder < 1 || stepOrder > TestStepBounds.MaxOrder) return null;
        if (!SelfHealing.SelfHealingEligibility.IsHealableAction(stepAction)) return null;
        if (string.IsNullOrEmpty(originalValue) || string.IsNullOrEmpty(proposedValue)) return null;
        if (originalValue.Contains(RedactedMarker, StringComparison.Ordinal) ||
            proposedValue.Contains(RedactedMarker, StringComparison.Ordinal)) return null;
        if (!WebStrategies.Contains(originalStrategy) || !WebStrategies.Contains(proposedStrategy)) return null;
        if (string.Equals(originalStrategy, proposedStrategy, StringComparison.Ordinal) &&
            string.Equals(originalValue, proposedValue, StringComparison.Ordinal)) return null;

        // Only deterministic, validated, applied evidence qualifies.
        // Must be a trusted deterministic healing strategy (not None, not Ai).
        var deterministicStrategies = new[]
        {
            SelfHealingStrategy.TestAttribute,
            SelfHealingStrategy.Role,
            SelfHealingStrategy.Text,
            SelfHealingStrategy.Structural,
        };
        var qualifying = rows.Where(r =>
            r.Status == SelfHealingStatus.Applied &&
            r.WasApplied &&
            !r.IsAiAssisted &&
            deterministicStrategies.Contains(r.HealingStrategy) &&
            r.ExecutionStatus is not (ExecutionStatus.Cancelled or ExecutionStatus.TimedOut or ExecutionStatus.Error) &&
            r.Classification is not (FailureClassification.ApplicationDefect or FailureClassification.EnvironmentFailure)).ToList();
        if (qualifying.Count == 0) return null;

        // Distinct executions, most recent first, capped at MaxExecutions.
        var executionIds = qualifying
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.ExecutionId)
            .Select(r => r.ExecutionId)
            .Distinct()
            .Take(MaxExecutions)
            .ToList();
        var windowed = qualifying.Where(r => executionIds.Contains(r.ExecutionId)).ToList();
        if (executionIds.Count < MinOccurrences) return null;

        // Source version must be the latest Approved version with a matching step.
        if (!latestVersions.TryGetValue(testCaseId, out var latest)) return null;
        if (latest.VersionId != versionId.Value) return null;
        if (latest.ReviewStatus != ReviewStatus.Approved) return null;
        var step = latest.Steps.FirstOrDefault(s => s.Order == stepOrder);
        if (step is null) return null;
        if (!string.Equals(step.Action?.Trim(), stepAction.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        var parsed = ParseTarget(step.Target);
        if (parsed is null) return null;
        if (!string.Equals(parsed.Value.Strategy, originalStrategy, StringComparison.Ordinal) ||
            !string.Equals(parsed.Value.Value, originalValue, StringComparison.Ordinal)) return null;

        // Application/environment faults must never become test edits.
        if (excludedTestCaseIds.Contains(testCaseId)) return null;
        failedVerdicts.TryGetValue((testCaseId, versionId.Value), out var verdicts);
        if (verdicts is not null && verdicts.HasAppEnvFailure) return null;

        // Corroboration (never sole triggers).
        var failedCount = verdicts?.FailedCount ?? 0;
        forecastBands.TryGetValue(testCaseId, out var band);
        var stepRows = rows.Where(r =>
            r.Status is SelfHealingStatus.Applied or SelfHealingStatus.Failed).ToList();
        var appliedStep = stepRows.Count(r => r.Status == SelfHealingStatus.Applied && r.WasApplied);
        var ratio = stepRows.Count == 0 ? 0.0 : (double)appliedStep / stepRows.Count;

        var confidence = ComputeConfidence(executionIds.Count, failedCount, band, ratio);
        if (confidence < MinConfidence) return null;

        var factors = new List<string>
        {
            $"{executionIds.Count} successful recoveries of the same locator",
        };
        if (failedCount >= MinFailedCorroboration)
            factors.Add($"{failedCount} related automation failures");
        if (string.Equals(band, Reports.FlakinessForecast.BandHigh, StringComparison.OrdinalIgnoreCase))
            factors.Add("flakiness forecast High");
        if (ratio >= MinHealingSuccessRatio)
            factors.Add($"healing success ratio {ratio:P0} on this step");

        var attemptIds = windowed
            .Select(r => r.AttemptId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        return new Candidate(
            testCaseId, versionId.Value, stepOrder, stepAction.Trim(),
            originalStrategy, originalValue, proposedStrategy, proposedValue,
            MapHealingStrategy(proposedStrategy),
            executionIds.Count, confidence, factors, failedCount, band, ratio,
            executionIds.OrderBy(id => id).ToList(), attemptIds,
            windowed.Min(r => r.CreatedAt), windowed.Max(r => r.CreatedAt));
    }

    /// <summary>
    /// Deterministic confidence: base 40 + 10 per extra occurrence up to 70,
    /// then corroboration bonuses, clamped 0–100. Never bypasses the
    /// minimum-evidence gate (enforced by the caller).
    /// </summary>
    public static int ComputeConfidence(int occurrences, int failedCorroboration, string? forecastBand, double successRatio)
    {
        var score = 40 + 10 * Math.Min(Math.Max(occurrences - MinOccurrences, 0), 3);
        if (failedCorroboration >= MinFailedCorroboration) score += 10;
        if (string.Equals(forecastBand, Reports.FlakinessForecast.BandHigh, StringComparison.OrdinalIgnoreCase)) score += 10;
        if (successRatio >= MinHealingSuccessRatio) score += 10;
        return Math.Clamp(score, 0, 100);
    }

    /// <summary>Mirrors the Playwright worker parseTarget: explicit
    /// css=/xpath=/role=/text=/testid= prefixes (case-insensitive), bare
    /// values are CSS. Role names after '|' belong to the name, not the value.</summary>
    public static (string Strategy, string Value)? ParseTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var text = target.Trim();
        var lower = text.ToLowerInvariant();
        foreach (var prefix in new[] { "css=", "xpath=", "role=", "text=", "testid=" })
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = text[prefix.Length..];
                if (prefix == "role=")
                {
                    var pipe = rest.IndexOf('|');
                    var selector = (pipe >= 0 ? rest[..pipe] : rest).Trim();
                    if (selector.Length == 0) return null;
                    return ("role", selector);
                }
                if (rest.Length == 0) return null;
                return (prefix[..^1], rest);
            }
        }
        return ("css", text);
    }

    public static string FormatTarget(string strategy, string value)
        => $"{strategy.Trim().ToLowerInvariant()}={value}";

    /// <summary>Deterministic strategy label from a validated recovered
    /// strategy (worker strategyLabel semantics without the AI branch).</summary>
    public static SelfHealingStrategy MapHealingStrategy(string? recoveredStrategy)
        => recoveredStrategy?.Trim().ToLowerInvariant() switch
        {
            "testid" => SelfHealingStrategy.TestAttribute,
            "role" => SelfHealingStrategy.Role,
            "text" => SelfHealingStrategy.Text,
            "css" or "xpath" => SelfHealingStrategy.Structural,
            _ => SelfHealingStrategy.None,
        };

    private static class TestStepBounds
    {
        public const int MaxOrder = 500;
    }
}
