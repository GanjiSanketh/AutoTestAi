using System.Text.RegularExpressions;

namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Step-value hygiene (Slice 5 §16): password-like targets never persist or
/// stream plaintext values. Applied by the engine on persist and mirrored by
/// the worker in its own logs.
/// </summary>
public static partial class ExecutionValueRedactor
{
    [GeneratedRegex("passw|passwd|pwd|secret|token|private[_-]?key", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveTarget();

    public const string Mask = "[REDACTED]";

    public static bool IsSensitiveTarget(string? target)
        => !string.IsNullOrWhiteSpace(target) && SensitiveTarget().IsMatch(target);

    /// <summary>Returns the mask when the action writes a value into a sensitive target.</summary>
    public static string? RedactStepValue(string action, string? target, string? value)
    {
        if (value is null) return null;
        if (!IsSensitiveTarget(target)) return value;
        return action.Trim().ToLowerInvariant() switch
        {
            "fill" or "type" or "select" or "press" => Mask,
            _ => value,
        };
    }
}
