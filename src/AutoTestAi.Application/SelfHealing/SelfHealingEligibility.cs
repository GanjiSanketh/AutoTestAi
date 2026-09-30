using System.Text.RegularExpressions;

namespace AutoTestAi.Application.SelfHealing;

/// <summary>
/// Narrowly scoped locator-failure predicate (Slice 11 §4). Mirrors the worker
/// heuristic so the backend can validate worker-reported attempts without
/// trusting them: only plausible locator/DOM-mutation failures qualify.
/// Assertion, navigation, network, auth, environment and cancellation signals
/// never qualify.
/// </summary>
public static partial class SelfHealingEligibility
{
    private static readonly IReadOnlySet<string> HealableActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "click", "fill", "type", "select", "check", "uncheck", "press", "assertvisible",
        };

    [GeneratedRegex(
        """timeout|waiting for|locat|no element|element not found|could not find|strict mode|resolved to 0|selector|target closed|not visible|not enabled|detached|stale""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocatorSignal();

    [GeneratedRegex(
        """expected text|expected value|assertion|http\s+\d{3}|status code|net::|navigation|401|unauthori[sz]ed|403|forbidden|cancelled|canceled|aborted|execution timeout|unsupported action|requires a (target|value)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonHealableSignal();

    public static bool IsHealingEligible(string? action, string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(action) || !HealableActions.Contains(action.Trim()))
            return false;
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;
        if (NonHealableSignal().IsMatch(errorMessage))
            return false;
        return LocatorSignal().IsMatch(errorMessage);
    }

    /// <summary>Actions the worker may heal. Anything else is rejected at record time.</summary>
    public static bool IsHealableAction(string? action)
        => !string.IsNullOrWhiteSpace(action) && HealableActions.Contains(action.Trim());
}
