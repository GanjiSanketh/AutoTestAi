using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.TestExecution;

namespace AutoTestAi.Infrastructure.Executions;

/// <summary>
/// Endpoint-parameterized Appium worker HTTP transport (Phase 3 Slice 3C-4B-2).
/// Mirrors <see cref="WorkerHttpTransport"/> wire mechanics for the mobile
/// contract: JSON over HTTP, Bearer assignment API token, per-request
/// timeouts, deterministic transport errors. The target worker (base URL +
/// token) is chosen per call by the grid client. ClaimToken never appears
/// here; the assignment token travels only as the request envelope field
/// the worker requires for assignment authentication.
/// </summary>
public sealed class MobileWorkerTransport
{
    private const string HttpClientName = "appium-worker";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClients;

    public MobileWorkerTransport(IHttpClientFactory httpClients)
    {
        _httpClients = httpClients;
    }

    public async Task<string> StartAssignmentAsync(
        string baseUrl, string? apiToken, int timeoutSeconds,
        MobileAssignmentDto assignment, CancellationToken ct,
        Guid? assignmentToken = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            assignment,
            assignmentToken
        }, JsonOptions);
        using var response = await SendAsync(
            HttpMethod.Post, "v1/assignments", body,
            baseUrl, apiToken, timeoutSeconds, ct);
        var payload = await ReadJsonAsync(response, ct);
        if (!payload.TryGetProperty("assignmentId", out var id) || id.ValueKind != JsonValueKind.String)
            throw new WorkerInfrastructureException("The Appium worker returned an invalid assignment response.");
        return id.GetString()!;
    }

    public async Task<MobileAssignmentProgressDto> GetAssignmentAsync(
        string baseUrl, string? apiToken, int timeoutSeconds,
        string assignmentId, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"v1/assignments/{Uri.EscapeDataString(assignmentId)}", null,
            baseUrl, apiToken, timeoutSeconds, ct);
        var payload = await ReadJsonAsync(response, ct);
        return ParseProgress(payload);
    }

    public async Task CancelAssignmentAsync(
        string baseUrl, string? apiToken, int timeoutSeconds,
        string assignmentId, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Delete, $"v1/assignments/{Uri.EscapeDataString(assignmentId)}", null,
                baseUrl, apiToken, timeoutSeconds, ct);
        }
        catch (WorkerInfrastructureException ex) when (ex.HttpStatusCode == 404)
        {
            // Already gone — best effort.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller is already tearing down; swallow best-effort failures.
        }
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? body,
        string baseUrl, string? apiToken, int timeoutSeconds, CancellationToken ct)
    {
        baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw new WorkerInfrastructureException("The Appium worker base URL is not configured correctly.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 300)));

        try
        {
            var client = _httpClients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(method, $"{baseUrl}/{path}");
            if (!string.IsNullOrWhiteSpace(apiToken))
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiToken.Trim());
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new WorkerInfrastructureException("The Appium worker is busy (rate limited).")
                { HttpStatusCode = 429 };
            if ((int)response.StatusCode >= 500)
                throw new WorkerInfrastructureException(
                    $"The Appium worker failed (HTTP {(int)response.StatusCode}).")
                { HttpStatusCode = (int)response.StatusCode };
            if (!response.IsSuccessStatusCode)
                throw new WorkerInfrastructureException(
                    $"The Appium worker rejected the request (HTTP {(int)response.StatusCode}).")
                { IsRetryable = false, HttpStatusCode = (int)response.StatusCode };
            return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WorkerInfrastructureException("The Appium worker request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new WorkerInfrastructureException("The Appium worker is unreachable.", ex);
        }
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new WorkerInfrastructureException("The Appium worker returned malformed JSON.", ex);
        }
    }

    public static MobileAssignmentProgressDto ParseProgress(JsonElement root)
    {
        var assignmentId = root.TryGetProperty("assignmentId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty : string.Empty;
        var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? "running" : "running";
        int? current = root.TryGetProperty("currentStepOrder", out var cur) && cur.ValueKind == JsonValueKind.Number && cur.TryGetInt32(out var n)
            ? n : null;
        var steps = root.TryGetProperty("stepResults", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array
            ? stepsEl.EnumerateArray().Select(ParseStep).ToList()
            : new List<MobileStepResultDto>();
        var logs = root.TryGetProperty("logs", out var logsEl) && logsEl.ValueKind == JsonValueKind.Array
            ? logsEl.EnumerateArray().Select(ParseLog).ToList()
            : new List<MobileLogDto>();
        MobileAssignmentResultDto? result = null;
        if (root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
            result = ParseResult(assignmentId, resultEl);
        var sessionId = root.TryGetProperty("appiumSessionId", out var sess) && sess.ValueKind == JsonValueKind.String
            ? sess.GetString() : null;
        return new MobileAssignmentProgressDto(assignmentId, status, current, steps, logs, result, sessionId);
    }

    private static MobileAssignmentResultDto ParseResult(string assignmentId, JsonElement root)
    {
        var steps = root.TryGetProperty("stepResults", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array
            ? stepsEl.EnumerateArray().Select(ParseStep).ToList()
            : new List<MobileStepResultDto>();
        var logs = root.TryGetProperty("logs", out var logsEl) && logsEl.ValueKind == JsonValueKind.Array
            ? logsEl.EnumerateArray().Select(ParseLog).ToList()
            : new List<MobileLogDto>();
        var shots = root.TryGetProperty("screenshots", out var shotsEl) && shotsEl.ValueKind == JsonValueKind.Array
            ? shotsEl.EnumerateArray().Select(s => new MobileScreenshotDto(
                s.TryGetProperty("stepOrder", out var so) && so.ValueKind == JsonValueKind.Number && so.TryGetInt32(out var n) ? n : null,
                s.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "screenshot.png" : "screenshot.png",
                s.TryGetProperty("contentType", out var ctp) && ctp.ValueKind == JsonValueKind.String ? ctp.GetString() ?? "image/png" : "image/png",
                s.TryGetProperty("base64Content", out var b64) && b64.ValueKind == JsonValueKind.String ? b64.GetString() ?? string.Empty : string.Empty)).ToList()
            : new List<MobileScreenshotDto>();
        var sources = root.TryGetProperty("pageSources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array
            ? sourcesEl.EnumerateArray().Select(s => new MobilePageSourceDto(
                s.TryGetProperty("stepOrder", out var so) && so.ValueKind == JsonValueKind.Number && so.TryGetInt32(out var n) ? n : null,
                s.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "pagesource.xml" : "pagesource.xml",
                s.TryGetProperty("contentType", out var ctp) && ctp.ValueKind == JsonValueKind.String ? ctp.GetString() ?? "text/xml" : "text/xml",
                s.TryGetProperty("xmlContent", out var xml) && xml.ValueKind == JsonValueKind.String ? xml.GetString() ?? string.Empty : string.Empty)).ToList()
            : new List<MobilePageSourceDto>();
        var serverLogs = root.TryGetProperty("serverLogs", out var serverLogsEl) && serverLogsEl.ValueKind == JsonValueKind.Array
            ? serverLogsEl.EnumerateArray().Select(s => new MobileServerLogDto(
                s.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "appium.log" : "appium.log",
                s.TryGetProperty("contentType", out var ctp) && ctp.ValueKind == JsonValueKind.String ? ctp.GetString() ?? "text/plain" : "text/plain",
                s.TryGetProperty("textContent", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty)).ToList()
            : new List<MobileServerLogDto>();
        // Healing attempts are supplementary; absent means no healing ran.
        var healing = root.TryGetProperty("healingAttempts", out var healingEl) && healingEl.ValueKind == JsonValueKind.Array
            ? healingEl.EnumerateArray().Select(ParseHealingAttempt).Where(h => h is not null).Select(h => h!).ToList()
            : new List<WorkerHealingAttemptDto>();
        var sessionId = root.TryGetProperty("appiumSessionId", out var sess) && sess.ValueKind == JsonValueKind.String
            ? sess.GetString() : null;
        return new MobileAssignmentResultDto(
            assignmentId,
            Str(root, "status") ?? "error",
            Str(root, "classification"),
            Str(root, "errorType"),
            Str(root, "errorMessage"),
            Long(root, "durationMs"),
            steps, logs, shots, sessionId, sources, serverLogs, healing);
    }

    private static MobileStepResultDto ParseStep(JsonElement s) => new(
        Int(s, "order"),
        Str(s, "action") ?? string.Empty,
        Str(s, "target"),
        Str(s, "status") ?? "error",
        Long(s, "startedAtUnixMs"),
        Long(s, "completedAtUnixMs"),
        Long(s, "durationMs"),
        Str(s, "errorMessage"),
        Bool(s, "healed"),
        Str(s, "recoveredTarget"),
        Str(s, "healingStrategy"),
        Bool(s, "aiAssisted"));

    private static MobileLogDto ParseLog(JsonElement l) => new(
        Long(l, "seq"), Long(l, "timestampUnixMs"),
        Str(l, "level") ?? "Information", Str(l, "message") ?? string.Empty);

    private static WorkerHealingAttemptDto? ParseHealingAttempt(JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object) return null;
        var order = Int(h, "stepOrder");
        var action = Str(h, "action") ?? Str(h, "stepAction");
        if (order < 1 || string.IsNullOrWhiteSpace(action)) return null;
        return new WorkerHealingAttemptDto(
            order, action,
            Str(h, "originalStrategy"), Str(h, "originalValue"),
            Str(h, "recoveredStrategy"), Str(h, "recoveredValue"),
            Str(h, "healingStrategy") ?? "none",
            Str(h, "status") ?? "Failed",
            Int(h, "candidateCount"),
            Bool(h, "wasApplied") ?? false,
            Bool(h, "isAiAssisted") ?? false,
            Str(h, "errorMessage"));
    }

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
