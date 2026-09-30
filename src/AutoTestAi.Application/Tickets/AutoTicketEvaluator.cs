using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Deterministic auto-ticket eligibility (Phase 2 Slice 10).
/// No LLM is consulted: persisted defect fields plus the configured policy
/// decide eligible/not eligible. AI confidence is an optional numeric
/// threshold only; deterministic classification remains authoritative.
/// </summary>
public static class AutoTicketEvaluator
{
    public static (bool Eligible, string Reason) Evaluate(Defect defect, AutoTicketPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(defect);
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.Enabled)
            return (false, "policy_disabled");

        var severities = Split(policy.Severities);
        if (!severities.Contains(defect.Severity.ToString(), StringComparer.OrdinalIgnoreCase))
            return (false, "severity_not_eligible");

        var statuses = Split(policy.DefectStatuses);
        if (!statuses.Contains(defect.Status.ToString(), StringComparer.OrdinalIgnoreCase))
            return (false, "status_not_eligible");

        var classifications = Split(policy.Classifications);
        // A defect without a determined root cause carries no classification
        // signal; only policies that explicitly allow Unknown accept it.
        var classification = defect.RootCauseType?.ToString() ?? FailureClassification.Unknown.ToString();
        if (!classifications.Contains(classification, StringComparer.OrdinalIgnoreCase))
            return (false, "classification_not_eligible");

        if (policy.MinimumConfidence is not null)
        {
            // Missing confidence never blocks: the threshold only filters
            // defects that actually carry an AI confidence value.
            if (defect.AiConfidence is not null && defect.AiConfidence.Value < policy.MinimumConfidence.Value)
                return (false, "confidence_below_threshold");
        }

        return (true, "eligible");
    }

    public static IReadOnlyList<string> ParseSeverities(string raw) => Split(raw).ToList();

    public static IReadOnlyList<string> ParseStatuses(string raw) => Split(raw).ToList();

    public static IReadOnlyList<string> ParseClassifications(string raw) => Split(raw).ToList();

    public static string Join(IEnumerable<string> values)
        => string.Join(",", values.Select(v => v.Trim()).Where(v => v.Length > 0));

    private static HashSet<string> Split(string raw)
        => new(
            (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}
