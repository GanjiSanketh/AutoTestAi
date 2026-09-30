using System.Net;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.FailureAnalysis;
using AutoTestAi.Application.SelfHealing;
using AutoTestAi.Application.TestGeneration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Infrastructure.AI;

/// <summary>
/// Ollama adapter (Slice 4 §7). Talks to the Ollama HTTP API (/api/chat) with no
/// SDK dependency; the base URL and model come from configuration (never
/// hard-coded). Ollama is optional: nothing else in the platform depends on it.
/// </summary>
public sealed class OllamaAiProvider : IAiProvider
{
    public const string ProviderName = "ollama";
    private const string HttpClientName = "ai-ollama";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClients;
    private readonly IOptions<AiOptions> _options;
    private readonly IAiTestGenerationPromptBuilder _prompts;
    private readonly IAiFailureAnalysisPromptBuilder _analysisPrompts;
    private readonly SelfHealingAiPromptBuilder _healingPrompts;
    private readonly ILogger<OllamaAiProvider> _logger;

    public OllamaAiProvider(
        IHttpClientFactory httpClients,
        IOptions<AiOptions> options,
        IAiTestGenerationPromptBuilder prompts,
        IAiFailureAnalysisPromptBuilder analysisPrompts,
        SelfHealingAiPromptBuilder healingPrompts,
        ILogger<OllamaAiProvider> logger)
    {
        _httpClients = httpClients;
        _options = options;
        _prompts = prompts;
        _analysisPrompts = analysisPrompts;
        _healingPrompts = healingPrompts;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<AiGenerationResult> GenerateTestAsync(
        AiGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (model, baseUrl, settings) = RequireConfigured();

        var prompt = _prompts.Build(request);
        var (content, inputTokens, outputTokens) = await PostChatAsync(
            baseUrl, model, prompt.SystemPrompt, prompt.UserPrompt, settings, cancellationToken);

        var parsed = AiProviderResponseParser.Parse(ProviderName, content, request);
        // Log metadata only — never prompt bodies, responses, or configuration values.
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
            TotalTokens: inputTokens + outputTokens);
    }

    public async Task<AiAnalysisResult> AnalyzeFailureAsync(
        AiFailureAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (model, baseUrl, settings) = RequireConfigured();
        var prompt = _analysisPrompts.Build(request);

        var (content, inputTokens, outputTokens) = await PostChatAsync(
            baseUrl, model, prompt.SystemPrompt, prompt.UserPrompt, settings, cancellationToken);

        // Log metadata only — never prompt bodies, responses, or configuration values.
        _logger.LogInformation("AI failure analysis via {Provider} model {Model} completed.",
            ProviderName, model);

        return AiAnalysisResponseParser.Parse(
            ProviderName, model, content, prompt.PromptVersion,
            inputTokens, outputTokens, inputTokens + outputTokens);
    }

    public async Task<AiHealingResult> SuggestHealingCandidatesAsync(
        AiHealingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (model, baseUrl, settings) = RequireConfigured();
        var prompt = _healingPrompts.Build(request);

        var (content, inputTokens, outputTokens) = await PostChatAsync(
            baseUrl, model, prompt.SystemPrompt, prompt.UserPrompt, settings, cancellationToken);

        // Log metadata only — never prompt bodies, responses, or configuration values.
        _logger.LogInformation("AI healing suggestion via {Provider} model {Model} completed.",
            ProviderName, model);

        var parsed = SelfHealingAiValidator.ParseOrThrow(
            ProviderName, model, content, prompt.PromptVersion);
        return parsed with
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = inputTokens + outputTokens,
        };
    }

    private (string Model, string BaseUrl, AiOptions Settings) RequireConfigured()
    {
        var settings = _options.Value;
        var model = (settings.Model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(model))
            throw AiProviderException.NotConfigured(ProviderName,
                "AI provider 'ollama' is selected but AI:Model is not configured.");
        var baseUrl = (settings.BaseUrl ?? "http://localhost:11434").Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw AiProviderException.NotConfigured(ProviderName,
                "AI provider 'ollama' has an invalid AI:BaseUrl. Set an absolute URL.");
        return (model, baseUrl, settings);
    }

    /// <summary>Shared chat transport: prompt in, content + token usage out.</summary>
    private async Task<(string? Content, long? InputTokens, long? OutputTokens)> PostChatAsync(
        string baseUrl, string model, string systemPrompt, string userPrompt,
        AiOptions settings, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            format = "json",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            options = new
            {
                temperature = settings.Temperature ?? 0.2,
                num_predict = settings.MaxOutputTokens ?? 4096,
            },
        }, JsonOptions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        string responseBody;
        try
        {
            var client = _httpClients.CreateClient(HttpClientName);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeout.Token);
            responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw AiProviderException.RateLimited(ProviderName,
                    "Ollama provider rate limit reached. Retry shortly.");
            if (!response.IsSuccessStatusCode)
                throw AiProviderException.Unavailable(ProviderName,
                    $"Ollama provider returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw AiProviderException.Timeout(ProviderName,
                $"Ollama provider timed out after {(int)settings.Timeout.TotalSeconds}s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw AiProviderException.Unavailable(ProviderName,
                "Ollama provider is unreachable. Verify AI:BaseUrl and that the model is pulled.", ex);
        }
        catch (AiProviderException)
        {
            throw;
        }

        try
        {
            using var outer = JsonDocument.Parse(responseBody);
            var root = outer.RootElement;
            string? content = root.TryGetProperty("message", out var message) &&
                      message.TryGetProperty("content", out var contentEl) &&
                      contentEl.ValueKind == JsonValueKind.String
                ? contentEl.GetString()
                : null;
            long? inputTokens = root.TryGetProperty("prompt_eval_count", out var promptCount) &&
                promptCount.TryGetInt64(out var promptTokens) ? promptTokens : null;
            long? outputTokens = root.TryGetProperty("eval_count", out var evalCount) &&
                evalCount.TryGetInt64(out var completionTokens) ? completionTokens : null;
            return (content, inputTokens, outputTokens);
        }
        catch (JsonException ex)
        {
            throw AiProviderException.Malformed(ProviderName,
                "Ollama provider returned a malformed response envelope.", ex);
        }
    }
}
