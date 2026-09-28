using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.FailureAnalysis;

/// <summary>
/// Validates normalized AI analysis output before persistence (Slice 6 §6).
/// Malformed output is rejected — never persisted, never returned raw.
/// </summary>
public sealed class AiAnalysisValidator
{
    private const int MaxSummaryLength = 500;
    private const int MaxCauseLength = 2000;
    private const int MaxActionLength = 500;
    private const int MaxEvidenceItems = 20;
    private const int MaxEvidenceLength = 1000;
    private const int MaxListItems = 20;
    private const int MaxListLength = 1000;

    private static readonly IReadOnlySet<string> AllowedClassifications =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(FailureClassification.TestFailure),
            nameof(FailureClassification.ApplicationDefect),
            nameof(FailureClassification.EnvironmentFailure),
            nameof(FailureClassification.AutomationFailure),
            nameof(FailureClassification.Unknown),
        };

    public void ValidateOrThrow(AiAnalysisResult result)
    {
        var errors = Validate(result);
        ValidationException.ThrowIfInvalid(errors, "AI analysis output failed validation and was not saved.");
    }

    public List<FieldError> Validate(AiAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var errors = new List<FieldError>();

        if (string.IsNullOrWhiteSpace(result.Classification) ||
            !AllowedClassifications.Contains(result.Classification.Trim()))
            errors.Add(new FieldError("classification",
                "Classification must be 'TestFailure', 'ApplicationDefect', 'EnvironmentFailure', 'AutomationFailure' or 'Unknown'."));

        var summary = (result.Summary ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(summary))
            errors.Add(new FieldError("summary", "Summary is required."));
        else if (summary.Length > MaxSummaryLength)
            errors.Add(new FieldError("summary", $"Summary must be at most {MaxSummaryLength} characters."));

        var cause = (result.RootCause ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cause))
            errors.Add(new FieldError("probableCause", "Probable cause is required."));
        else if (cause.Length > MaxCauseLength)
            errors.Add(new FieldError("probableCause", $"Probable cause must be at most {MaxCauseLength} characters."));

        if (result.Confidence < 0m || result.Confidence > 1m)
            errors.Add(new FieldError("confidence", "Confidence must be within 0..1."));

        if (!string.IsNullOrWhiteSpace(result.RecommendedAction) &&
            result.RecommendedAction.Trim().Length > MaxActionLength)
            errors.Add(new FieldError("recommendedAction",
                $"Recommended action must be at most {MaxActionLength} characters."));

        if (result.Evidence is not null)
        {
            if (result.Evidence.Count > MaxEvidenceItems)
                errors.Add(new FieldError("evidence",
                    $"'evidence' must not exceed {MaxEvidenceItems} entries."));
            foreach (var item in result.Evidence)
            {
                if (item is not null && item.Length > MaxEvidenceLength)
                {
                    errors.Add(new FieldError("evidence",
                        $"'evidence' entries must be at most {MaxEvidenceLength} characters."));
                    break;
                }
            }
        }

        CheckList(errors, "assumptions", result.Assumptions);
        CheckList(errors, "warnings", result.Warnings);

        if (string.IsNullOrWhiteSpace(result.Provider))
            errors.Add(new FieldError("provider", "Provider is required."));

        return errors;
    }

    private static void CheckList(List<FieldError> errors, string field, IReadOnlyList<string>? values)
    {
        if (values is null) return;
        if (values.Count > MaxListItems)
            errors.Add(new FieldError(field, $"'{field}' must not exceed {MaxListItems} entries."));
        foreach (var value in values)
        {
            if (value is not null && value.Length > MaxListLength)
            {
                errors.Add(new FieldError(field, $"'{field}' entries must be at most {MaxListLength} characters."));
                break;
            }
        }
    }
}
