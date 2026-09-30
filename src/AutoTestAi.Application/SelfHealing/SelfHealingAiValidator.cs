using System.Text.Json;
using System.Text.RegularExpressions;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.SelfHealing;

/// <summary>
/// Schema + safety validation for AI locator output (Slice 11 §13).
/// The AI suggests locator DATA only; this validator decides whether a
/// candidate may proceed to live-DOM validation. Anything resembling code,
/// an unsupported strategy, or an out-of-bounds value is rejected.
/// Never throws for provider content — it throws ValidationException only
/// for malformed result envelopes.
/// </summary>
public static partial class SelfHealingAiValidator
{
    public static readonly IReadOnlySet<string> AllowedStrategies =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "css", "xpath", "role", "text", "testid" };

    [GeneratedRegex(
        """javascript:|<script|eval\s*\(|function\s*\(|child_process|process\.env|require\s*\(|import\s*\(|settimeout\s*\(|setinterval\s*\(|__proto__|constructor""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeSignal();

    /// <summary>
    /// Validates one normalized provider result against policy. Returns the
    /// accepted candidates; rejections are described, never executed.
    /// </summary>
    public static (IReadOnlyList<HealingCandidateDto> Accepted, IReadOnlyList<string> Rejected) Validate(
        AiHealingResult result,
        IReadOnlyList<string> allowedStrategies,
        decimal? minConfidence,
        int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(result);
        var allowed = allowedStrategies.Count == 0
            ? AllowedStrategies
            : new HashSet<string>(allowedStrategies, StringComparer.OrdinalIgnoreCase);
        var accepted = new List<HealingCandidateDto>();
        var rejected = new List<string>();
        foreach (var candidate in result.Candidates.Take(Math.Clamp(maxCandidates, 1, 8)))
        {
            var strategy = (candidate.Strategy ?? string.Empty).Trim().ToLowerInvariant();
            var value = (candidate.Value ?? string.Empty).Trim();
            if (!AllowedStrategies.Contains(strategy) || !allowed.Contains(strategy))
            {
                rejected.Add($"Unsupported AI strategy '{strategy}'.");
                continue;
            }
            if (value.Length == 0 || value.Length > 2000)
            {
                rejected.Add("AI candidate value was empty or exceeded bounds.");
                continue;
            }
            if (CodeSignal().IsMatch(value) || CodeSignal().IsMatch(strategy))
            {
                rejected.Add("AI candidate contained executable content and was rejected.");
                continue;
            }
            if (minConfidence.HasValue && candidate.Confidence.HasValue &&
                candidate.Confidence.Value < minConfidence.Value)
            {
                rejected.Add($"AI candidate confidence {candidate.Confidence} below policy minimum.");
                continue;
            }
            var normalized = strategy == "xpath" && value.StartsWith("xpath=", StringComparison.OrdinalIgnoreCase)
                ? value["xpath=".Length..]
                : value;
            accepted.Add(new HealingCandidateDto(
                strategy, normalized,
                string.IsNullOrWhiteSpace(candidate.Reason) ? "AI-suggested locator." : candidate.Reason.Trim(),
                candidate.Confidence));
        }
        return (accepted, rejected);
    }

    /// <summary>Validates raw provider JSON (shared by provider adapters).</summary>
    public static AiHealingResult ParseOrThrow(string provider, string? model, string? content, string promptVersion)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ValidationException("The provider returned empty healing output.",
                new[] { new FieldError("candidates", "The provider returned empty healing output.") });
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            throw new ValidationException("The provider returned malformed healing output.",
                new[] { new FieldError("candidates", "The provider returned malformed healing output.") });
        }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("candidates", out var array) ||
                array.ValueKind != JsonValueKind.Array)
                throw new ValidationException("The provider returned malformed healing output.",
                    new[] { new FieldError("candidates", "The provider output did not contain a candidates array.") });
            var candidates = new List<AiHealingCandidate>();
            foreach (var entry in array.EnumerateArray().Take(8))
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                var strategy = entry.TryGetProperty("strategy", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? string.Empty : string.Empty;
                var value = entry.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? string.Empty : string.Empty;
                var reason = entry.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString() : null;
                decimal? confidence = entry.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var d)
                    ? d : null;
                candidates.Add(new AiHealingCandidate(strategy, value, reason, confidence));
            }
            return new AiHealingResult(provider, model, candidates, promptVersion);
        }
    }
}
