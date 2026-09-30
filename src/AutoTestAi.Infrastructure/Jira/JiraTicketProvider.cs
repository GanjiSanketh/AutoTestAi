using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.Tickets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Infrastructure.Jira;

/// <summary>Outbound Jira options (Slice 7 §22). Token-free; the per-project API
/// token arrives per-request from the integration row (server-side only).</summary>
public sealed class JiraOptions
{
    public const string SectionName = "Jira";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxErrorBodyChars { get; set; } = 2000;
}

/// <summary>
/// Infrastructure Jira HTTP adapter (Slice 7 §3/§22). Owns transport, Basic
/// auth, request/response DTOs, and status interpretation. Application code
/// never sees Jira JSON shapes. Credentials are attached per-request and never
/// logged, returned, or stored in ticket rows.
/// </summary>
public sealed class JiraTicketProvider : IJiraTicketProvider
{
    public const string HttpClientName = "jira";
    private const int MaxResponseChars = 200_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClients;
    private readonly IOptions<JiraOptions> _options;
    private readonly ILogger<JiraTicketProvider> _logger;

    public JiraTicketProvider(
        IHttpClientFactory httpClients,
        IOptions<JiraOptions> options,
        ILogger<JiraTicketProvider> logger)
    {
        _httpClients = httpClients;
        _options = options;
        _logger = logger;
    }

    public async Task<JiraCreateResult> CreateIssueAsync(
        JiraCreateRequest request,
        string email,
        string apiToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(apiToken))
            throw JiraProviderException.Authentication("Jira credentials are not configured.");
        if (!JiraUrlValidator.IsValidBaseUrl(request.BaseUrl, out _))
            throw JiraProviderException.Validation("The Jira base URL is invalid.");

        var settings = _options.Value;
        var baseUrl = request.BaseUrl.Trim().TrimEnd('/');
        var endpoint = $"{baseUrl}/rest/api/3/issue";

        var body = JsonSerializer.Serialize(new
        {
            fields = new Dictionary<string, object?>
            {
                ["project"] = new { key = request.ProjectKey },
                ["issuetype"] = new { name = request.IssueType },
                ["summary"] = request.Summary,
                ["description"] = new
                {
                    type = "doc",
                    version = 1,
                    content = new[]
                    {
                        new
                        {
                            type = "paragraph",
                            content = new[] { new { type = "text", text = request.Description } },
                        },
                    },
                },
                ["priority"] = string.IsNullOrWhiteSpace(request.Priority) ? null : new { name = request.Priority },
            }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value),
        }, JsonOptions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 120)));

        string responseBody;
        HttpStatusCode status;
        try
        {
            var client = _httpClients.CreateClient(HttpClientName);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // Basic auth (email:token) attached server-side; never logged.
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email.Trim()}:{apiToken.Trim()}"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeout.Token);
            status = response.StatusCode;
            responseBody = await ReadBoundedAsync(response, Math.Max(1, settings.MaxErrorBodyChars), timeout.Token);
            MapStatus(status, response);
        }
        catch (JiraProviderException)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Ambiguous: Jira may have created the issue before the timeout.
            _logger.LogWarning("Jira issue creation timed out for project {ProjectKey}.", request.ProjectKey);
            throw JiraProviderException.Timeout("Jira did not respond in time. The ticket state is ambiguous; check Jira before retrying.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Jira is unreachable at the configured base URL.");
            throw JiraProviderException.Unavailable("Jira is currently unavailable. Please try again later.", ex);
        }

        // Log outcome only — never request/response bodies that could carry secrets.
        _logger.LogInformation("Jira responded HTTP {Status} to issue creation for project {ProjectKey}.",
            (int)status, request.ProjectKey);

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            var key = root.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() : null;
            var self = root.TryGetProperty("self", out var selfEl) && selfEl.ValueKind == JsonValueKind.String ? selfEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(self))
                throw JiraProviderException.Unavailable("Jira returned a malformed creation response.");
            if (!JiraUrlValidator.IsSafeExternalTicketUrl(self!))
                throw JiraProviderException.Unavailable("Jira returned an unsafe ticket URL.");
            return new JiraCreateResult(id!, key!, self!);
        }
        catch (JsonException ex)
        {
            throw new JiraProviderException(JiraErrorKind.MalformedResponse,
                "Jira returned a malformed creation response.", ex);
        }
    }

    private static void MapStatus(HttpStatusCode status, HttpResponseMessage response)
    {
        if ((int)status is >= 200 and < 300)
            return;
        throw status switch
        {
            HttpStatusCode.BadRequest => JiraProviderException.Validation("Jira rejected the ticket details. Review the integration configuration."),
            HttpStatusCode.Unauthorized => JiraProviderException.Authentication("Jira rejected the configured credentials. Check the integration secret."),
            HttpStatusCode.Forbidden => new JiraProviderException(JiraErrorKind.Permission,
                "Jira denied the request. The configured account lacks permission."),
            HttpStatusCode.NotFound => new JiraProviderException(JiraErrorKind.NotFound,
                "The Jira project or endpoint could not be found."),
            HttpStatusCode.Conflict => new JiraProviderException(JiraErrorKind.Conflict,
                "Jira reported a conflict for this request."),
            HttpStatusCode.TooManyRequests => new JiraProviderException(JiraErrorKind.RateLimited,
                "Jira rate-limited the request. Please try again shortly.", null, ParseRetryAfter(response)),
            _ when (int)status >= 500 => JiraProviderException.Unavailable("Jira is currently unavailable. Please try again later."),
            _ => JiraProviderException.Unavailable($"Jira returned HTTP {(int)status}."),
        };
    }

    /// <summary>
    /// Best-effort `Retry-After` extraction (delta-seconds or HTTP-date).
    /// Returns null when absent or unparsable; callers bound the value.
    /// Headers only — never bodies — so nothing sensitive is captured.
    /// </summary>
    internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        try
        {
            var value = response.Headers.RetryAfter;
            if (value is null)
            {
                if (!response.Headers.TryGetValues("Retry-After", out var rawValues))
                    return null;
                var raw = rawValues.FirstOrDefault()?.Trim() ?? string.Empty;
                if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 3600));
                if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return date - DateTimeOffset.UtcNow;
                return null;
            }
            if (value.Delta.HasValue)
                return TimeSpan.FromSeconds(Math.Clamp(value.Delta.Value.TotalSeconds, 0, 3600));
            if (value.Date.HasValue)
                return value.Date.Value - DateTimeOffset.UtcNow;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int maxChars, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length > Math.Min(MaxResponseChars, Math.Max(maxChars * 20, maxChars)))
            text = text[..Math.Min(MaxResponseChars, Math.Max(maxChars * 20, maxChars))];
        return text;
    }
}
