using System.Text.Json;
using System.Text.RegularExpressions;
using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Validated Jira integration configuration (Slice 7 §5/§7).
/// Stored as JSONB in integrations.configuration; the API token lives in
/// integrations.secret_reference and is never serialized into config JSON.
/// </summary>
public sealed record JiraIntegrationConfig(
    string BaseUrl,
    string ProjectKey,
    string Email,
    string IssueType,
    IReadOnlyDictionary<string, string> PriorityMapping,
    string? AppBaseUrl)
{
    public const string ProviderName = "jira";
    public const string DefaultIssueType = "Bug";

    private static readonly IReadOnlyDictionary<string, string> DefaultPriorityMapping =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Critical"] = "Highest",
            ["High"] = "High",
            ["Medium"] = "Medium",
            ["Low"] = "Low",
        };

    public static string NormalizeBaseUrl(string baseUrl)
        => baseUrl.Trim().TrimEnd('/');

    public static string NormalizeProjectKey(string projectKey)
        => projectKey.Trim().ToUpperInvariant();

    public static void ValidateFields(
        string baseUrl, string projectKey, string email, string? issueType, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            errors.Add(new FieldError("baseUrl", "Jira base URL is required."));
        else if (!JiraUrlValidator.IsValidBaseUrl(baseUrl.Trim(), out var reason))
            errors.Add(new FieldError("baseUrl", reason));

        if (string.IsNullOrWhiteSpace(projectKey))
            errors.Add(new FieldError("projectKey", "Jira project key is required."));
        else if (!Regex.IsMatch(projectKey.Trim(), @"^[A-Za-z0-9]{2,10}$"))
            errors.Add(new FieldError("projectKey", "Jira project key must be 2-10 alphanumeric characters."));

        if (string.IsNullOrWhiteSpace(email))
            errors.Add(new FieldError("email", "Jira account email is required."));
        else if (!Regex.IsMatch(email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            errors.Add(new FieldError("email", "Jira account email is invalid."));

        if (!string.IsNullOrWhiteSpace(issueType) && issueType.Trim().Length > 60)
            errors.Add(new FieldError("issueType", "Issue type must be at most 60 characters."));
    }

    public static IReadOnlyDictionary<string, string> NormalizePriorityMapping(
        IReadOnlyDictionary<string, string>? mapping)
    {
        if (mapping is null || mapping.Count == 0)
            return DefaultPriorityMapping;
        var result = new Dictionary<string, string>(DefaultPriorityMapping, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in mapping)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            result[key.Trim()] = value.Trim();
        }
        return result;
    }

    public static JiraIntegrationConfig FromJson(JsonDocument? document)
    {
        if (document is null)
            return new JiraIntegrationConfig(string.Empty, string.Empty, string.Empty, DefaultIssueType, DefaultPriorityMapping, null);
        var root = document.RootElement;
        static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("priorityMapping", out var pm) && pm.ValueKind == JsonValueKind.Object)
            foreach (var prop in pm.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    mapping[prop.Name] = prop.Value.GetString() ?? string.Empty;
        string? appBase = null;
        if (root.TryGetProperty("appBaseUrl", out var ab) && ab.ValueKind == JsonValueKind.String)
            appBase = string.IsNullOrWhiteSpace(ab.GetString()) ? null : ab.GetString();
        return new JiraIntegrationConfig(
            Str(root, "baseUrl"),
            Str(root, "projectKey"),
            Str(root, "email"),
            string.IsNullOrWhiteSpace(Str(root, "issueType")) ? DefaultIssueType : Str(root, "issueType"),
            NormalizePriorityMapping(mapping.Count == 0 ? null : mapping),
            appBase);
    }

    public string ToJson()
        => JsonSerializer.Serialize(new
        {
            baseUrl = BaseUrl,
            projectKey = ProjectKey,
            email = Email,
            issueType = IssueType,
            priorityMapping = PriorityMapping,
            appBaseUrl = AppBaseUrl,
        });
}

/// <summary>
/// SSRF guard for the configured Jira base URL (Slice 7 §44).
/// Only http(s) hosts are accepted; loopback/link-local/metadata targets are
/// rejected unless explicitly allowed by development configuration.
/// </summary>
public static class JiraUrlValidator
{
    public static bool IsValidBaseUrl(string value, out string reason)
    {
        reason = "Jira base URL must be a valid http(s) URL.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;
        if (!string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
        {
            reason = "Jira base URL must not contain a query string or fragment.";
            return false;
        }
        var host = uri.Host.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            reason = "Jira base URL must not target localhost in this configuration.";
            return false;
        }
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
            {
                reason = "Jira base URL must not target a loopback address.";
                return false;
            }
            var bytes = ip.GetAddressBytes();
            // 169.254.0.0/16 link-local (incl. cloud metadata 169.254.169.254).
            if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
            {
                reason = "Jira base URL must not target a link-local address.";
                return false;
            }
            // 10/8, 172.16/12, 192.168/16 private ranges.
            if (bytes.Length == 4 &&
                (bytes[0] == 10 ||
                 (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                 (bytes[0] == 192 && bytes[1] == 168)))
            {
                reason = "Jira base URL must not target a private network address.";
                return false;
            }
            // ::1 already covered by IsLoopback; fe80::/10 link-local.
            if (bytes.Length == 16 && bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                reason = "Jira base URL must not target a link-local address.";
                return false;
            }
        }
        // Cloud metadata hostname.
        if (string.Equals(host, "metadata.google.internal", StringComparison.Ordinal) ||
            string.Equals(host, "169.254.169.254", StringComparison.Ordinal))
        {
            reason = "Jira base URL must not target a cloud metadata endpoint.";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            // Plain HTTP is accepted only for .local/test hosts (dev); the
            // error message guides production toward HTTPS.
            if (!(host.EndsWith(".local", StringComparison.Ordinal) ||
                  host.EndsWith(".test", StringComparison.Ordinal) ||
                  host.StartsWith("192.0.2.", StringComparison.Ordinal)))
            {
                reason = "Jira base URL must use HTTPS.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    public static bool IsSafeExternalTicketUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;
        return true;
    }
}
