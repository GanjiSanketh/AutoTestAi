using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.TestCases;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 4: provider resolution, prompt, validation, redaction,
/// rate limiting, and the generation orchestrator (docs/08 FR-3.2).</summary>
public sealed class AiTestGeneratorTests
{
    private static readonly Guid ProjectA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ---------- fakes ----------

    private sealed class FakeProvider : IAiProvider
    {
        public string Name { get; init; } = "stub";
        public AiGenerationResult? Result { get; set; }
        public Exception? Failure { get; set; }
        public AiGenerationRequest? SeenRequest { get; private set; }
        public int Calls { get; private set; }

        public Task<AiGenerationResult> GenerateTestAsync(AiGenerationRequest request, CancellationToken ct)
        {
            Calls++;
            SeenRequest = request;
            ct.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Task.FromResult(Result!);
        }

        public Task<AiAnalysisResult> AnalyzeFailureAsync(AiFailureAnalysisRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        public IAiProvider Provider { get; set; } = null!;
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        public IAiProvider Resolve()
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Provider;
        }

        public AiProviderStatus GetStatus()
            => new(Provider?.Name ?? "stub", "stub-1.0", true, null, AiPromptVersions.TestGenerationV1);
    }

    private sealed class FakeAuthorization : IAuthorizationService
    {
        public bool Allowed { get; set; } = true;
        public bool HasPermission(string permission) => Allowed;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(Allowed);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct)
            => Allowed
                ? Task.CompletedTask
                : throw new ForbiddenException("The caller has no access to this project.");
    }

    private sealed record AuditEvent(string Action, string? MetadataJson);

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<AuditEvent> Events = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        {
            Events.Add(new AuditEvent(action, metadataJson));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTestCaseService : ITestCaseService
    {
        public CreateTestCaseCommand? SeenCreate { get; private set; }
        public TestCaseDto Created { get; set; } = null!;
        public TestCaseVersionDto Version { get; set; } = null!;

        public Task<TestCaseDto> CreateAsync(CreateTestCaseCommand command, CancellationToken ct)
        {
            SeenCreate = command;
            return Task.FromResult(Created);
        }

        public Task<IReadOnlyList<TestCaseVersionDto>> ListVersionsAsync(Guid testCaseId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TestCaseVersionDto>>(new[] { Version });

        public Task<PagedResult<TestCaseListItemDto>> ListAsync(Guid p, int a, int b, TestCaseFilters f, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseDto> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseDto> UpdateAsync(Guid id, UpdateTestCaseCommand c, CancellationToken ct) => throw new NotImplementedException();
        public Task ArchiveAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseVersionDto> GetVersionAsync(Guid a, Guid b, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseVersionDto> ReviewAsync(Guid a, ReviewTestCaseCommand b, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    // ---------- builders ----------

    private static GenerateAiTestCommand ValidCommand() => new(
        ProjectA, "Successful user login", "Verify login.",
        new[] { "Validate successful login", "Validate dashboard navigation" },
        "https://example.test/login", "playwright", "web", "Authentication", "High", null);

    private static AiGenerationResult ValidResult(string provider = "stub") => new(
        Provider: provider,
        Model: "stub-1.0",
        LatencyMs: 5,
        Steps: new[] { new AiGeneratedStep("1", "navigate", "Page loads") },
        SourceCode: "import { test } from '@playwright/test';\ntest('login', async () => {});",
        Title: "Successful user login",
        Framework: "playwright",
        Platform: "web",
        StructuredSteps: new[] { new AiStructuredStep(1, "navigate", "https://example.test/login", null) },
        Assumptions: new[] { "Login page exists." },
        Warnings: Array.Empty<string>(),
        PromptVersion: AiPromptVersions.TestGenerationV1);

    private static (TestGenerationService Service, FakeProvider Provider, FakeAuthorization Auth, FakeAudit Audit, FakeTestCaseService Cases)
        CreateService(AiOptions? options = null, int rateLimit = 1000)
    {
        var provider = new FakeProvider { Result = ValidResult() };
        var resolver = new FakeResolver { Provider = provider };
        var auth = new FakeAuthorization();
        var audit = new FakeAudit();
        var cases = new FakeTestCaseService();
        var caseId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        cases.Created = new TestCaseDto(caseId, ProjectA, "AI-LOGIN-ABCDEF", "Successful user login",
            "Verify login.", "Authentication", "playwright", "web", "High", "Draft", "ai",
            1, "Pending", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        cases.Version = new TestCaseVersionDto(versionId, caseId, 1, "code",
            new[] { new TestStepDto(1, "navigate", "https://example.test/login", null) },
            "stub", "stub-1.0", 5, "Pending", null, DateTimeOffset.UtcNow);
        var service = new TestGenerationService(
            resolver,
            new AiGenerationValidator(),
            new AiGenerationRateLimiter(
                Options.Create(options ?? new AiOptions { MaxGenerationsPerMinutePerProject = rateLimit }),
                new FixedClock(DateTimeOffset.UtcNow)),
            cases, auth, audit);
        return (service, provider, auth, audit, cases);
    }

    // ---------- provider resolution ----------

    [Fact]
    public void Resolver_DefaultsToStub()
    {
        var resolver = new AiProviderResolver(
            new[] { new StubAiProvider(new FixedClock(DateTimeOffset.UnixEpoch)) },
            Options.Create(new AiOptions()));
        Assert.Equal("stub", resolver.Resolve().Name);
    }

    [Fact]
    public void Resolver_SelectsConfiguredProvider()
    {
        var stub = new StubAiProvider(new FixedClock(DateTimeOffset.UnixEpoch));
        var ollama = new FakeProvider { Name = "ollama", Result = ValidResult("ollama") };
        var resolver = new AiProviderResolver(
            new IAiProvider[] { stub, ollama },
            Options.Create(new AiOptions { Provider = "Ollama" }));
        Assert.Equal("ollama", resolver.Resolve().Name);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("gemini")]
    public void Resolver_UnsupportedProvider_ThrowsWithoutFallback(string name)
    {
        var resolver = new AiProviderResolver(
            new[] { new StubAiProvider(new FixedClock(DateTimeOffset.UnixEpoch)) },
            Options.Create(new AiOptions { Provider = name }));
        var ex = Assert.Throws<AiProviderException>(() => resolver.Resolve());
        Assert.Equal(AiProviderErrorKind.UnsupportedProvider, ex.Kind);
    }

    [Fact]
    public void Resolver_OpenAiWithoutKey_ThrowsNotConfigured()
    {
        var openai = new FakeProvider { Name = "openai", Result = ValidResult("openai") };
        var resolver = new AiProviderResolver(
            new IAiProvider[] { openai },
            Options.Create(new AiOptions { Provider = "openai", Model = "gpt-4o-mini" }));
        var ex = Assert.Throws<AiProviderException>(() => resolver.Resolve());
        Assert.Equal(AiProviderErrorKind.NotConfigured, ex.Kind);
        Assert.DoesNotContain("sk-", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_OpenAiWithKey_Resolves()
    {
        var openai = new FakeProvider { Name = "openai", Result = ValidResult("openai") };
        var resolver = new AiProviderResolver(
            new IAiProvider[] { openai },
            Options.Create(new AiOptions { Provider = "openai", Model = "gpt-4o-mini", ApiKey = "sk-test" }));
        Assert.Equal("openai", resolver.Resolve().Name);
    }

    [Fact]
    public void Resolver_Status_NeverContainsSecrets()
    {
        var resolver = new AiProviderResolver(
            Array.Empty<IAiProvider>(),
            Options.Create(new AiOptions { Provider = "openai", Model = "gpt-4o-mini", ApiKey = "sk-live-secret" }));
        var status = resolver.GetStatus();
        Assert.Equal("openai", status.Provider);
        Assert.Equal("gpt-4o-mini", status.Model);
        Assert.False(status.Configured);
        Assert.Equal(AiPromptVersions.TestGenerationV1, status.PromptVersion);
    }

    // ---------- prompt builder ----------

    [Fact]
    public void PromptBuilder_ContainsContextConstraintsAndVersion()
    {
        var builder = new AiTestGenerationPromptBuilder();
        var prompt = builder.Build(new AiGenerationRequest(
            "Login works", "Verify login.", "https://example.test/login",
            "playwright", "web", new[] { "Validate successful login" }, "Auth", "High", null));

        Assert.Equal(AiPromptVersions.TestGenerationV1, builder.PromptVersion);
        Assert.Equal("test-generation-v1", prompt.PromptVersion);
        Assert.Contains("playwright", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("web", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Validate successful login", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("https://example.test/login", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("JSON", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("placeholder", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never execute", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assumptions", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-generation-v1", prompt.UserPrompt, StringComparison.Ordinal);
    }

    // ---------- response validation ----------

    [Fact]
    public void Validator_AcceptsWellFormedResult()
    {
        var validator = new AiGenerationValidator();
        var request = new AiGenerationRequest("T", null, null, "playwright", "web", new[] { "r" });
        Assert.Empty(validator.Validate(ValidResult(), request));
    }

    [Fact]
    public void Validator_RejectsMissingTitleStepsAndSourceEnvelope()
    {
        var validator = new AiGenerationValidator();
        var request = new AiGenerationRequest("", null, null, "", "", Array.Empty<string>());
        var bad = ValidResult() with
        {
            Title = "  ",
            Framework = "",
            Platform = "",
            StructuredSteps = Array.Empty<AiStructuredStep>(),
            Steps = Array.Empty<AiGeneratedStep>(),
            SourceCode = """{"title": "oops"}""",
        };
        var errors = validator.Validate(bad, request);
        Assert.Contains(errors, e => e.Field == "title");
        Assert.Contains(errors, e => e.Field == "framework");
        Assert.Contains(errors, e => e.Field == "platform");
        Assert.Contains(errors, e => e.Field == "structuredSteps");
        Assert.Contains(errors, e => e.Field == "sourceCode");
        Assert.Throws<ValidationException>(() => validator.ValidateOrThrow(bad, request));
    }

    [Fact]
    public void Validator_RejectsNonSequentialStepOrdering()
    {
        var validator = new AiGenerationValidator();
        var request = new AiGenerationRequest("T", null, null, "playwright", "web", new[] { "r" });
        var bad = ValidResult() with
        {
            StructuredSteps = new[]
            {
                new AiStructuredStep(1, "navigate", null, null),
                new AiStructuredStep(3, "click", "#x", null),
            },
        };
        Assert.Contains(validator.Validate(bad, request), e => e.Field == "structuredSteps");
    }

    // ---------- redaction ----------

    [Theory]
    [InlineData("""{"password": "supersecret", "title": "ok"}""", "supersecret")]
    [InlineData("api_key=live123, title=ok", "live123")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "abcdefgh12345678")]
    public void Redactor_RemovesSecrets_KeepingShape(string input, string secret)
    {
        var redacted = SensitiveDataRedactor.Redact(input);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    // ---------- rate limiter ----------

    [Fact]
    public void RateLimiter_AllowsUpToLimit_ThenThrowsRateLimited()
    {
        var limiter = new AiGenerationRateLimiter(
            Options.Create(new AiOptions { MaxGenerationsPerMinutePerProject = 2 }),
            new FixedClock(DateTimeOffset.UtcNow));
        limiter.CheckOrThrow(ProjectA, "stub");
        limiter.CheckOrThrow(ProjectA, "stub");
        var ex = Assert.Throws<AiProviderException>(() => limiter.CheckOrThrow(ProjectA, "stub"));
        Assert.Equal(AiProviderErrorKind.RateLimited, ex.Kind);
        // Other projects are unaffected.
        limiter.CheckOrThrow(Guid.NewGuid(), "stub");
    }

    // ---------- orchestrator ----------

    [Fact]
    public async Task Generate_Success_PersistsAiVersionPending_WithMetadataAndAudit()
    {
        var (service, _, _, audit, cases) = CreateService();

        var result = await service.GenerateAsync(ValidCommand(), CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("stub", result.Provider);
        Assert.Equal("test-generation-v1", result.PromptVersion);
        Assert.True(result.LatencyMs >= 0);
        Assert.Equal("Pending", result.ReviewStatus);
        Assert.Single(result.StructuredSteps);
        Assert.NotEqual(Guid.Empty, result.GenerationId);

        Assert.NotNull(cases.SeenCreate);
        var create = cases.SeenCreate!;
        Assert.Equal("ai", create.SourceType);
        Assert.True(TestCaseKey.IsValidFormat(create.TestKey));
        Assert.Equal("stub", create.GenerationProvider);
        Assert.Equal("stub-1.0", create.GenerationModel);
        Assert.NotNull(create.GenerationLatencyMs);
        Assert.NotNull(create.GenerationRequest);

        var actions = audit.Events.Select(e => e.Action).ToList();
        Assert.Contains("test-generation.requested", actions);
        Assert.Contains("test-generation.completed", actions);
        Assert.DoesNotContain("test-generation.failed", actions);
        Assert.All(audit.Events, e => Assert.DoesNotContain("sk-", e.MetadataJson ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generate_RedactsSecrets_BeforePersistence()
    {
        var (service, _, _, _, cases) = CreateService();
        var command = ValidCommand() with
        {
            AdditionalContext = """{"password": "supersecret", "api_key": "live123"}""",
        };

        await service.GenerateAsync(command, CancellationToken.None);

        var stored = cases.SeenCreate!.GenerationRequest!.RootElement.GetRawText();
        Assert.DoesNotContain("supersecret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("live123", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_InvalidInput_ThrowsValidation_WithoutCallingProvider()
    {
        var (service, provider, _, _, cases) = CreateService();
        var bad = ValidCommand() with { Title = "  ", Framework = "", TargetUrl = "not-a-url" };

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.GenerateAsync(bad, CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "title");
        Assert.Contains(ex.Errors, e => e.Field == "framework");
        Assert.Contains(ex.Errors, e => e.Field == "targetUrl");
        Assert.Equal(0, provider.Calls);
        Assert.Null(cases.SeenCreate);
    }

    [Fact]
    public async Task Generate_Forbidden_WhenNoProjectAccess()
    {
        var (service, provider, auth, _, _) = CreateService();
        auth.Allowed = false;

        await Assert.ThrowsAsync<ForbiddenException>(
            () => service.GenerateAsync(ValidCommand(), CancellationToken.None));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Generate_ProviderFailure_PropagatesWithFailedAudit_AndSavesNothing()
    {
        var (service, provider, _, audit, cases) = CreateService();
        provider.Failure = AiProviderException.Unavailable("stub", "boom");

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => service.GenerateAsync(ValidCommand(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.Unavailable, ex.Kind);
        Assert.Null(cases.SeenCreate);
        Assert.Contains(audit.Events, e => e.Action == "test-generation.failed");
    }

    [Fact]
    public async Task Generate_InvalidProviderOutput_ThrowsValidation_AndSavesNothing()
    {
        var (service, provider, _, audit, cases) = CreateService();
        provider.Result = ValidResult() with { SourceCode = "   " };

        await Assert.ThrowsAsync<ValidationException>(
            () => service.GenerateAsync(ValidCommand(), CancellationToken.None));
        Assert.Null(cases.SeenCreate);
        Assert.Contains(audit.Events, e => e.Action == "test-generation.failed");
    }

    [Fact]
    public async Task Generate_UnsupportedProvider_SurfacesConfigurationError()
    {
        var cases = new FakeTestCaseService();
        var audit = new FakeAudit();
        var resolver = new FakeResolver { Failure = AiProviderException.Unsupported("gemini", "nope") };
        var service = new TestGenerationService(resolver, new AiGenerationValidator(),
            new AiGenerationRateLimiter(Options.Create(new AiOptions()), new FixedClock(DateTimeOffset.UtcNow)),
            cases, new FakeAuthorization(), audit);

        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => service.GenerateAsync(ValidCommand(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.UnsupportedProvider, ex.Kind);
        Assert.Null(cases.SeenCreate);
        Assert.DoesNotContain(audit.Events, e => e.Action == "test-generation.completed");
    }

    [Fact]
    public async Task Generate_CancelledToken_AbortsWithoutSaving()
    {
        var (service, _, _, audit, cases) = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GenerateAsync(ValidCommand(), cts.Token));
        Assert.Null(cases.SeenCreate);
        Assert.Contains(audit.Events, e => e.Action == "test-generation.failed");
    }

    [Fact]
    public async Task Generate_RateLimited_WhenProjectExceedsBudget()
    {
        var (service, _, _, _, _) = CreateService(rateLimit: 1);
        await service.GenerateAsync(ValidCommand(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AiProviderException>(
            () => service.GenerateAsync(ValidCommand(), CancellationToken.None));
        Assert.Equal(AiProviderErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public void BuildTestKey_AlwaysProducesValidKeys()
    {
        foreach (var title in new[] { "Successful user login", "123 starts with digits", "!!!", "", "x" })
        {
            var key = TestGenerationService.BuildTestKey(title);
            Assert.True(TestCaseKey.IsValidFormat(key), $"Key '{key}' for title '{title}' is invalid.");
        }
    }
}
