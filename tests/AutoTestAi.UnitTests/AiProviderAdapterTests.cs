using System.Net;
using System.Net;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 4: Ollama/OpenAI adapters over mocked HTTP — parsing, token
/// usage, failure mapping, cancellation, and secret hygiene. No live providers.</summary>
public sealed class AiProviderAdapterTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Responder { get; set; }
            = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);
        public readonly List<HttpRequestMessage> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request, ct));
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private static AiGenerationRequest ValidRequest() => new(
        "Successful user login", "Verify login.", "https://example.test/login",
        "playwright", "web", new[] { "Validate successful login" }, "Auth", "High", null);

    private static string ModelJson(object payload)
        => JsonSerializer.Serialize(payload);

    private static string ChatContent() => ModelJson(new
    {
        title = "Successful user login",
        description = "Verify that a valid user can log in.",
        framework = "playwright",
        platform = "web",
        structuredSteps = new[]
        {
            new { order = 1, action = "navigate", target = "https://example.test/login", value = (string?)null },
            new { order = 2, action = "fill", target = "#username", value = (string?)"{{username}}" },
        },
        sourceCode = "import { test } from '@playwright/test';\ntest('login', async () => {});",
        assumptions = new[] { "Username field uses #username." },
        warnings = new[] { "Selector inferred, not verified." },
    });

    private static OllamaAiProvider Ollama(FakeHttpHandler handler, AiOptions options) => new(
        new FakeFactory(new HttpClient(handler)),
        Options.Create(options),
        new AiTestGenerationPromptBuilder(),
        NullLogger<OllamaAiProvider>.Instance);

    private static OpenAiAiProvider OpenAi(FakeHttpHandler handler, AiOptions options) => new(
        new FakeFactory(new HttpClient(handler)),
        Options.Create(options),
        new AiTestGenerationPromptBuilder(),
        NullLogger<OpenAiAiProvider>.Instance);

    // ---------- Ollama ----------

    [Fact]
    public async Task Ollama_Success_ReturnsNormalizedResult_WithTokens()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ModelJson(new
            {
                message = new { content = ChatContent() },
                prompt_eval_count = 120,
                eval_count = 340,
            })),
        };
        var provider = Ollama(handler, new AiOptions { Provider = "ollama", Model = "qwen3:8b" });

        var result = await provider.GenerateTestAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal("ollama", result.Provider);
        Assert.Equal("qwen3:8b", result.Model);
        Assert.Equal("Successful user login", result.Title);
        Assert.Equal("test-generation-v1", result.PromptVersion);
        Assert.Equal(2, result.EffectiveStructuredSteps().Count);
        Assert.Contains("@playwright/test", result.SourceCode, StringComparison.Ordinal);
        Assert.Single(result.Assumptions!);
        Assert.Single(result.Warnings!);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(340, result.OutputTokens);
        Assert.Equal(460, result.TotalTokens);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/api/chat", sent.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Ollama_MissingModel_ThrowsNotConfigured()
    {
        var provider = Ollama(new FakeHttpHandler(), new AiOptions { Provider = "ollama" });
        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateTestAsync(ValidRequest(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.NotConfigured, ex.Kind);
    }

    [Fact]
    public async Task Ollama_RateLimited_MapsTo429Kind()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage((HttpStatusCode)429);
        var provider = Ollama(handler, new AiOptions { Provider = "ollama", Model = "qwen3:8b" });

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateTestAsync(ValidRequest(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task Ollama_Unreachable_MapsToUnavailable()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => throw new HttpRequestException("connection refused");
        var provider = Ollama(handler, new AiOptions { Provider = "ollama", Model = "qwen3:8b" });

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateTestAsync(ValidRequest(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task Ollama_MalformedModelOutput_MapsToMalformed()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ModelJson(new { message = new { content = "not json at all {{{" } })),
        };
        var provider = Ollama(handler, new AiOptions { Provider = "ollama", Model = "qwen3:8b" });

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateTestAsync(ValidRequest(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.MalformedResponse, ex.Kind);
    }

    [Fact]
    public async Task Ollama_CancelledToken_AbortsWithoutTimeoutMapping()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var provider = Ollama(handler, new AiOptions { Provider = "ollama", Model = "qwen3:8b" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // HttpClient surfaces cancellation as TaskCanceledException (derived);
        // the key property is that it is NOT mapped to a provider Timeout.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GenerateTestAsync(ValidRequest(), cts.Token));
    }

    // ---------- OpenAI ----------

    [Fact]
    public async Task OpenAi_Success_ReturnsNormalizedResult_WithUsage()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (request, _) =>
        {
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelJson(new
                {
                    choices = new[] { new { message = new { content = ChatContent() } } },
                    usage = new { prompt_tokens = 200, completion_tokens = 400, total_tokens = 600 },
                })),
            };
        };
        var provider = OpenAi(handler, new AiOptions
        {
            Provider = "openai",
            Model = "gpt-4o-mini",
            ApiKey = "sk-test-key",
        });

        var result = await provider.GenerateTestAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal("openai", result.Provider);
        Assert.Equal("gpt-4o-mini", result.Model);
        Assert.Equal(2, result.EffectiveStructuredSteps().Count);
        Assert.Equal(200, result.InputTokens);
        Assert.Equal(400, result.OutputTokens);
        Assert.Equal(600, result.TotalTokens);
    }

    [Fact]
    public async Task OpenAi_MissingKey_ThrowsNotConfigured_WithoutLeakingAnything()
    {
        var provider = OpenAi(new FakeHttpHandler(), new AiOptions { Provider = "openai", Model = "gpt-4o-mini" });
        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateTestAsync(ValidRequest(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.NotConfigured, ex.Kind);
    }

    [Fact]
    public async Task OpenAi_RejectedKey_MapsToUnavailable_WithoutLeakingKey()
    {
        const string key = "sk-live-secret-value";
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var provider = OpenAi(handler, new AiOptions { Provider = "openai", Model = "gpt-4o-mini", ApiKey = key });

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateTestAsync(ValidRequest(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.Unavailable, ex.Kind);
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_RateLimited_MapsTo429Kind()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage((HttpStatusCode)429);
        var provider = OpenAi(handler, new AiOptions { Provider = "openai", Model = "gpt-4o-mini", ApiKey = "sk-test" });

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateTestAsync(ValidRequest(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task ProviderAnalyzers_RemainUnimplemented_InThisSlice()
    {
        var ollama = Ollama(new FakeHttpHandler(), new AiOptions());
        var openai = OpenAi(new FakeHttpHandler(), new AiOptions());
        await Assert.ThrowsAsync<NotSupportedException>(() => ollama.AnalyzeFailureAsync(
            new AiFailureAnalysisRequest(Guid.NewGuid(), null, null, null), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => openai.AnalyzeFailureAsync(
            new AiFailureAnalysisRequest(Guid.NewGuid(), null, null, null), CancellationToken.None));
    }
}
