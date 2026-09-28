using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.TestGeneration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Infrastructure.AI;

/// <summary>
/// OpenAI adapter (Slice 4 §8). Clean HTTP wrapper kept inside Infrastructure —
/// no OpenAI SDK types leak into Application or Domain. API keys come from
/// server-side configuration only and are never logged, returned, or stored in
/// generated content.
/// </summary>
public sealed class OpenAiAiProvider : IAiProvider
{
    public const string ProviderName = "openai";
    private const string HttpClientName = "ai-openai";
    private const string DefaultEndpoint = "https://api.openai.com/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClients;
    private readonly IOptions<AiOptions> _options;
    private readonly IAiTestGenerationPromptBuilder _prompts;
    private readonly ILogger<OpenAiAiProvider> _logger;

    public OpenAiAiProvider(
        IHttpClientFactory httpClients,
        IOptions<AiOptions> options,
        IAiTestGenerationPromptBuilder prompts,
        ILogger<OpenAiAiProvider> logger)
    {
        _httpClients = httpClients;
        _options = options;
        _prompts = prompts;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<AiGenerationResult> GenerateTestAsync(
        AiGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = _options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw AiProviderException.NotConfigured(ProviderName,
                "AI provider 'openai' is selected but AI:ApiKey is not configured. " +
                "Set the server-side API key; it is never exposed to clients.");
        var model = (settings.Model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(model))
            throw AiProviderException.NotConfigured(ProviderName,
                "AI provider 'openai' is selected but AI:Model is not configured.");

        // An explicit non-default BaseUrl allows OpenAI-compatible gateways;
        // otherwise the public endpoint is used.
        var baseUrl = (settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.Equals(baseUrl, "http://localhost:11434", StringComparison.OrdinalIgnoreCase))
            baseUrl = DefaultEndpoint;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw AiProviderException.NotConfigured(ProviderName,
                "AI provider 'openai' has an invalid AI:BaseUrl. Set an absolute URL.");

        var prompt = _prompts.Build(request);
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt },
            },
            response_format = new { type = "json_object" },
            temperature = settings.Temperature ?? 0.2,
            max_tokens = settings.MaxOutputTokens ?? 4096,
        }, JsonOptions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        string responseBody;
        try
        {
            var client = _httpClients.CreateClient(HttpClientName);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // Authorization header is set per-request and never logged.
            httpRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeout.Token);
            responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw AiProviderException.RateLimited(ProviderName,
                    "OpenAI provider rate limit reached. Retry shortly.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw AiProviderException.Unavailable(ProviderName,
                    "OpenAI provider rejected the server-side API key. Check server configuration. No test was saved.");
            if (!response.IsSuccessStatusCode)
                throw AiProviderException.Unavailable(ProviderName,
                    $"OpenAI provider returned HTTP {(int)response.StatusCode}. No test was saved.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw AiProviderException.Timeout(ProviderName,
                $"OpenAI provider timed out after {(int)settings.Timeout.TotalSeconds}s. No test was saved.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw AiProviderException.Unavailable(ProviderName,
                "OpenAI provider is unreachable. Check network and endpoint configuration.", ex);
        }
        catch (AiProviderException)
        {
            throw;
        }

        string? content;
        long? inputTokens = null, outputTokens = null, totalTokens = null;
        try
        {
            using var outer = JsonDocument.Parse(responseBody);
            var root = outer.RootElement;
            content = root.TryGetProperty("choices", out var choices) &&
                      choices.ValueKind == JsonValueKind.Array &&
                      choices.GetArrayLength() > 0 &&
                      choices[0].TryGetProperty("message", out var message) &&
                      message.TryGetProperty("content", out var contentEl) &&
                      contentEl.ValueKind == JsonValueKind.String
                ? contentEl.GetString()
                : null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pv)) inputTokens = pv;
                if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt64(out var cv)) outputTokens = cv;
                if (usage.TryGetProperty("total_tokens", out var t) && t.TryGetInt64(out var tv)) totalTokens = tv;
            }
        }
        catch (JsonException ex)
        {
            throw AiProviderException.Malformed(ProviderName,
                "OpenAI provider returned a malformed response envelope. No test was saved.", ex);
        }

        var parsed = AiProviderResponseParser.Parse(ProviderName, content, request);
        // Log metadata only — never prompt bodies, responses, or the API key.
        _logger.LogInformation("AI generation via {Provider} model {Model} produced {StepCount} steps.",
            ProviderName, model, parsed.Steps.Count);

        return new AiGenerationResult(
            Provider: ProviderName,
            Model: model,
            LatencyMs: 0, // Overwritten with server-measured latency by the orchestrator.
            Steps: parsed.Steps.Select(s => new AiGeneratedStep(
                s.Order.ToString(), s.Action, s.Value ?? string.Empty)).ToList(),
            SourceCode: parsed.SourceCode,
            Title: parsed.Title,
            Description: parsed.Description,
            Framework: parsed.Framework,
            Platform: parsed.Platform,
            StructuredSteps: parsed.Steps,
            Assumptions: parsed.Assumptions,
            Warnings: parsed.Warnings,
            PromptVersion: prompt.PromptVersion,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens ?? inputTokens + outputTokens);
    }

    public Task<AiAnalysisResult> AnalyzeFailureAsync(
        AiFailureAnalysisRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Failure analysis is not implemented in this slice (planned for Phase 1 Slice 6).");
}
