using System.Text.RegularExpressions;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Basic sensitive-value redaction for generation payloads (Slice 4 §15/§38).
/// Not a full DLP engine: redacts JSON string values and key=value pairs whose
/// key looks like a credential, plus bearer tokens. Applied before persisting
/// generation_request and before writing audit metadata.
/// </summary>
public static partial class SensitiveDataRedactor
{
    [GeneratedRegex(
        """("(?:password|passwd|pwd|api[_-]?key|secret|client[_-]?secret|access[_-]?token|refresh[_-]?token|auth[_-]?token|id[_-]?token|session[_-]?token|private[_-]?key|db[_-]?password|database[_-]?password)"\s*:\s*")([^"\\]*)(")""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretValue();

    [GeneratedRegex(
        """(?i)\b(password|passwd|pwd|api[_-]?key|secret|client[_-]?secret|access[_-]?token|refresh[_-]?token|auth[_-]?token)\b\s*[:=]\s*([^\s,;}"']+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    public const string Mask = "[REDACTED]";

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var redacted = JsonSecretValue().Replace(input, $"$1{Mask}$3");
        redacted = KeyValueSecret().Replace(redacted, m => $"{m.Groups[1].Value}={Mask}");
        redacted = BearerToken().Replace(redacted, $"Bearer {Mask}");
        return redacted;
    }
}
