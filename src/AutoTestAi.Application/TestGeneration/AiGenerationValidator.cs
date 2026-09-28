using System.Text.RegularExpressions;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;
using AutoTestAi.Domain.TestCases;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Validates normalized AI output before anything is persisted (Slice 4 §13).
/// AI output is untrusted: invalid results raise <see cref="ValidationException"/>
/// and nothing partial is saved.
/// </summary>
public sealed class AiGenerationValidator
{
    private const int MaxTitleLength = 200;
    private const int MaxSourceLength = 200_000;
    private const int MaxFrameworkLength = 100;
    private const int MaxPlatformLength = 100;
    private const int MaxAssumptionLength = 2000;
    private const int MaxItems = 100;

    public void ValidateOrThrow(AiGenerationResult result, AiGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);
        var errors = Validate(result, request);
        ValidationException.ThrowIfInvalid(errors, "AI generation output failed validation and was not saved.");
    }

    public List<FieldError> Validate(AiGenerationResult result, AiGenerationRequest request)
    {
        var errors = new List<FieldError>();

        var title = (result.Title ?? request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            errors.Add(new FieldError("title", "Generated title is required."));
        else if (title.Length > MaxTitleLength)
            errors.Add(new FieldError("title", $"Generated title must be at most {MaxTitleLength} characters."));

        var framework = (result.Framework ?? request.Framework ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(framework))
            errors.Add(new FieldError("framework", "Generated framework is required."));
        else if (framework.Length > MaxFrameworkLength)
            errors.Add(new FieldError("framework", $"Framework must be at most {MaxFrameworkLength} characters."));

        var platform = (result.Platform ?? request.Platform ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(platform))
            errors.Add(new FieldError("platform", "Generated platform is required."));
        else if (platform.Length > MaxPlatformLength)
            errors.Add(new FieldError("platform", $"Platform must be at most {MaxPlatformLength} characters."));

        var steps = result.EffectiveStructuredSteps();
        if (steps.Count == 0)
            errors.Add(new FieldError("structuredSteps", "At least one structured step is required."));
        else if (steps.Count > TestStep.MaxSteps)
            errors.Add(new FieldError("structuredSteps", $"Generated steps must not exceed {TestStep.MaxSteps} steps."));
        else
        {
            var seen = new HashSet<int>();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var label = $"Step {i + 1}";
                if (step.Order < 1)
                    errors.Add(new FieldError("structuredSteps", $"{label}: 'order' must be an integer >= 1."));
                else if (!seen.Add(step.Order))
                    errors.Add(new FieldError("structuredSteps", $"{label}: duplicate step order {step.Order}."));
                if (string.IsNullOrWhiteSpace(step.Action))
                    errors.Add(new FieldError("structuredSteps", $"{label}: 'action' is required."));
                else if (step.Action.Length > TestStep.MaxActionLength)
                    errors.Add(new FieldError("structuredSteps", $"{label}: 'action' must be at most {TestStep.MaxActionLength} characters."));
                if (step.Target is not null && step.Target.Length > TestStep.MaxTargetLength)
                    errors.Add(new FieldError("structuredSteps", $"{label}: 'target' must be at most {TestStep.MaxTargetLength} characters."));
                if (step.Value is not null && step.Value.Length > TestStep.MaxValueLength)
                    errors.Add(new FieldError("structuredSteps", $"{label}: 'value' must be at most {TestStep.MaxValueLength} characters."));
            }
            var ordered = steps.Select(s => s.Order).OrderBy(o => o).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i] != i + 1)
                {
                    errors.Add(new FieldError("structuredSteps", "Step orders must be sequential starting at 1."));
                    break;
                }
            }
        }

        var source = (result.SourceCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source))
            errors.Add(new FieldError("sourceCode", "Generated source code is required."));
        else
        {
            if (source.Length > MaxSourceLength)
                errors.Add(new FieldError("sourceCode", $"Generated source code must be at most {MaxSourceLength} characters."));
            if (source.StartsWith("{", StringComparison.Ordinal) || source.StartsWith("[", StringComparison.Ordinal))
                errors.Add(new FieldError("sourceCode", "Source code must be framework source, not a JSON envelope."));
            if (Regex.IsMatch(source, @"^\s*```", RegexOptions.Multiline))
                errors.Add(new FieldError("sourceCode", "Source code must not contain markdown fences."));
        }

        if (string.IsNullOrWhiteSpace(result.Provider))
            errors.Add(new FieldError("provider", "Generation provider is required."));

        CheckStringList(errors, "assumptions", result.Assumptions);
        CheckStringList(errors, "warnings", result.Warnings);

        return errors;
    }

    private static void CheckStringList(List<FieldError> errors, string field, IReadOnlyList<string>? values)
    {
        if (values is null) return;
        if (values.Count > MaxItems)
            errors.Add(new FieldError(field, $"'{field}' must not exceed {MaxItems} entries."));
        foreach (var value in values)
        {
            if (value is not null && value.Length > MaxAssumptionLength)
            {
                errors.Add(new FieldError(field, $"'{field}' entries must be at most {MaxAssumptionLength} characters."));
                break;
            }
        }
    }
}
