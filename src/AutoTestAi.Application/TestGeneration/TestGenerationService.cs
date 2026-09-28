using System.Diagnostics;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.TestCases;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// AI test-generation orchestrator (Slice 4 §5/§17). The single path from the API
/// to providers: selects the configured provider, invokes it, validates the
/// structured result, redacts sensitive values, and persists through the existing
/// <see cref="ITestCaseService"/> so versioning, review lifecycle, and audit rules
/// stay intact. Generated code is stored as untrusted text and never executed.
/// </summary>
public sealed class TestGenerationService : IAiTestGenerator
{
    private const int MaxRequirements = 50;
    private const int MaxRequirementLength = 2000;
    private const int MaxTextLength = 4000;

    private readonly IAiProviderResolver _resolver;
    private readonly AiGenerationValidator _validator;
    private readonly AiGenerationRateLimiter _rateLimiter;
    private readonly ITestCaseService _testCases;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditService _audit;

    public TestGenerationService(
        IAiProviderResolver resolver,
        AiGenerationValidator validator,
        AiGenerationRateLimiter rateLimiter,
        ITestCaseService testCases,
        IAuthorizationService authorization,
        IAuditService audit)
    {
        _resolver = resolver;
        _validator = validator;
        _rateLimiter = rateLimiter;
        _testCases = testCases;
        _authorization = authorization;
        _audit = audit;
    }

    public AiProviderStatus GetProviderStatus() => _resolver.GetStatus();

    public async Task<GenerateAiTestResult> GenerateAsync(
        GenerateAiTestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = NormalizeAndValidate(command);

        // Authorization boundary: testcases.manage + project membership (admin bypass).
        await _authorization.RequireProjectAccessAsync(
            normalized.ProjectId, Permissions.TestCasesManage, cancellationToken);

        var provider = _resolver.Resolve();
        _rateLimiter.CheckOrThrow(normalized.ProjectId, provider.Name);

        var generationId = Guid.NewGuid();
        await _audit.RecordAsync("test-generation.requested", "ai_generation",
            generationId.ToString(), normalized.ProjectId,
            SafeAuditJson(normalized, provider.Name, null, null, "requested"), cancellationToken);

        var providerRequest = new AiGenerationRequest(
            normalized.Title,
            normalized.Description,
            normalized.TargetUrl,
            normalized.Framework,
            normalized.Platform,
            normalized.Requirements,
            normalized.Module,
            normalized.Priority,
            normalized.AdditionalContext);

        AiGenerationResult result;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // CancellationToken flows HTTP request → service → provider → HTTP client (§31).
            result = await provider.GenerateTestAsync(providerRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _audit.RecordAsync("test-generation.failed", "ai_generation",
                generationId.ToString(), normalized.ProjectId,
                SafeAuditJson(normalized, provider.Name, null, null, "cancelled"), cancellationToken);
            throw;
        }
        catch (AiProviderException ex)
        {
            await _audit.RecordAsync("test-generation.failed", "ai_generation",
                generationId.ToString(), normalized.ProjectId,
                SafeAuditJson(normalized, provider.Name, null, null, $"provider:{ex.Kind}"), cancellationToken);
            throw;
        }
        stopwatch.Stop();
        var latencyMs = stopwatch.ElapsedMilliseconds;

        // Attach server-measured latency (§32) before validation/persistence.
        result = result with { LatencyMs = latencyMs };

        try
        {
            _validator.ValidateOrThrow(result, providerRequest);
        }
        catch (ValidationException)
        {
            await _audit.RecordAsync("test-generation.failed", "ai_generation",
                generationId.ToString(), normalized.ProjectId,
                SafeAuditJson(normalized, result.Provider, result.Model, latencyMs, "validation-failed"), cancellationToken);
            throw;
        }

        var title = (result.Title ?? normalized.Title).Trim();
        var framework = (result.Framework ?? normalized.Framework).Trim();
        var platform = (result.Platform ?? normalized.Platform).Trim();
        var promptVersion = string.IsNullOrWhiteSpace(result.PromptVersion)
            ? AiPromptVersions.TestGenerationV1
            : result.PromptVersion.Trim();

        var steps = result.EffectiveStructuredSteps()
            .OrderBy(s => s.Order)
            .Select(s => new GeneratedTestStepDto(s.Order, s.Action, s.Target, s.Value))
            .ToList();
        var stepsElement = JsonSerializer.SerializeToElement(steps.Select(s => new
        {
            order = s.Order,
            action = s.Action,
            target = s.Target,
            value = s.Value,
        }));

        var redactedRequest = BuildRedactedGenerationRequest(normalized, promptVersion);

        TestCaseDto created = await CreateTestCaseWithUniqueKeyAsync(
            normalized, title, framework, platform,
            result.SourceCode.Trim(), stepsElement, redactedRequest,
            result.Provider, result.Model, latencyMs, cancellationToken);

        var version = (await _testCases.ListVersionsAsync(created.Id, cancellationToken))
            .OrderByDescending(v => v.VersionNumber)
            .First();

        await _audit.RecordAsync("test-generation.completed", "ai_generation",
            generationId.ToString(), normalized.ProjectId,
            SafeAuditJson(normalized, result.Provider, result.Model, latencyMs, "succeeded", created.Id),
            cancellationToken);

        return new GenerateAiTestResult(
            generationId, created.Id, created.TestKey, version.Id, version.VersionNumber,
            "Succeeded", title, result.Description ?? normalized.Description, framework, platform,
            steps, result.SourceCode.Trim(),
            result.Assumptions ?? Array.Empty<string>(),
            result.Warnings ?? Array.Empty<string>(),
            result.Provider, result.Model, promptVersion, latencyMs,
            result.InputTokens, result.OutputTokens, result.TotalTokens,
            version.ReviewStatus);
    }

    private async Task<TestCaseDto> CreateTestCaseWithUniqueKeyAsync(
        GenerateAiTestCommand normalized, string title, string framework, string platform,
        string sourceCode, JsonElement stepsElement, JsonDocument generationRequest,
        string provider, string? model, long latencyMs, CancellationToken cancellationToken)
    {
        var baseKey = BuildTestKey(title);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var key = attempt == 0 ? baseKey : $"{baseKey}-{attempt + 1}";
            try
            {
                return await _testCases.CreateAsync(new CreateTestCaseCommand(
                    normalized.ProjectId, key, title, normalized.Description,
                    normalized.Module, framework, platform, normalized.Priority,
                    Status: null, SourceType: TestCaseSourceTypes.Ai,
                    SourceCode: sourceCode, StructuredSteps: stepsElement,
                    GenerationProvider: provider, GenerationModel: model,
                    GenerationLatencyMs: latencyMs, GenerationRequest: generationRequest),
                    cancellationToken);
            }
            catch (ConflictException) when (attempt < 4)
            {
                // Extremely unlikely (random suffix), but retry rather than fail.
            }
        }
        throw new ConflictException("Could not allocate a unique test key for the generated test.");
    }

    private static JsonDocument BuildRedactedGenerationRequest(
        GenerateAiTestCommand normalized, string promptVersion)
    {
        // Redact user-controlled strings BEFORE serialization: after serialization
        // quotes are escaped and credential patterns no longer match literally.
        static string R(string? value) => SensitiveDataRedactor.Redact(value ?? string.Empty);
        var payload = JsonSerializer.Serialize(new
        {
            title = R(normalized.Title),
            description = R(normalized.Description),
            targetUrl = R(normalized.TargetUrl),
            framework = R(normalized.Framework),
            platform = R(normalized.Platform),
            module = R(normalized.Module),
            priority = R(normalized.Priority),
            additionalContext = R(normalized.AdditionalContext),
            requirements = normalized.Requirements.Select(R).ToList(),
            promptVersion,
            source = "ai",
        });
        return JsonDocument.Parse(SensitiveDataRedactor.Redact(payload));
    }

    private static string SafeAuditJson(
        GenerateAiTestCommand normalized, string provider, string? model,
        long? latencyMs, string outcome, Guid? testCaseId = null)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            // User-controlled title is redacted before serialization so escaped
            // credential patterns cannot slip through.
            title = SensitiveDataRedactor.Redact(normalized.Title),
            framework = normalized.Framework,
            platform = normalized.Platform,
            requirementsCount = normalized.Requirements.Count,
            provider,
            model,
            promptVersion = AiPromptVersions.TestGenerationV1,
            latencyMs,
            outcome,
            testCaseId,
        }));

    /// <summary>Derives a valid, unique-friendly test key from the title (letter start,
    /// letters/digits/_/- only) with a random suffix to avoid collisions.</summary>
    public static string BuildTestKey(string title)
    {
        var upper = new string((title ?? string.Empty).ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (upper.Contains("--", StringComparison.Ordinal))
            upper = upper.Replace("--", "-", StringComparison.Ordinal);
        upper = upper.Trim('-');
        if (upper.Length == 0 || !char.IsLetter(upper[0]))
            upper = "AI-TEST";
        else if (!upper.StartsWith("AI-", StringComparison.Ordinal))
            upper = "AI-" + upper;
        if (upper.Length > 20)
            upper = upper[..20].TrimEnd('-');
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var key = $"{upper}-{suffix}";
        return TestCaseKey.IsValidFormat(key) && key.Length <= TestCaseKey.MaxLength
            ? key
            : $"AI-TEST-{suffix}";
    }

    private static GenerateAiTestCommand NormalizeAndValidate(GenerateAiTestCommand command)
    {
        var errors = new List<FieldError>();
        if (command.ProjectId == Guid.Empty)
            errors.Add(new FieldError("projectId", "Project id is required."));
        var title = command.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
            errors.Add(new FieldError("title", "Title is required."));
        else if (title.Length > 200)
            errors.Add(new FieldError("title", "Title must be at most 200 characters."));
        var framework = command.Framework?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(framework))
            errors.Add(new FieldError("framework", "Framework is required."));
        else if (framework.Length > 100)
            errors.Add(new FieldError("framework", "Framework must be at most 100 characters."));
        var platform = command.Platform?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(platform))
            errors.Add(new FieldError("platform", "Platform is required."));
        else if (platform.Length > 100)
            errors.Add(new FieldError("platform", "Platform must be at most 100 characters."));

        var requirements = (command.Requirements ?? Array.Empty<string>())
            .Select(r => r?.Trim() ?? string.Empty)
            .Where(r => r.Length > 0)
            .ToList();
        if (requirements.Count > MaxRequirements)
            errors.Add(new FieldError("requirements", $"At most {MaxRequirements} requirements are accepted."));
        foreach (var req in requirements)
        {
            if (req.Length > MaxRequirementLength)
            {
                errors.Add(new FieldError("requirements", $"Each requirement must be at most {MaxRequirementLength} characters."));
                break;
            }
        }

        string? targetUrl = command.TargetUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                errors.Add(new FieldError("targetUrl", "Target URL must be an absolute http(s) URL."));
        }
        else targetUrl = null;

        string? priority = command.Priority?.Trim();
        if (!string.IsNullOrWhiteSpace(priority) &&
            !Enum.TryParse<Priority>(priority, ignoreCase: true, out _))
            errors.Add(new FieldError("priority", "Priority must be 'Critical', 'High', 'Medium' or 'Low'."));

        string? description = BlankToNull(command.Description);
        if (description is not null && description.Length > MaxTextLength)
            errors.Add(new FieldError("description", $"Description must be at most {MaxTextLength} characters."));
        string? additionalContext = BlankToNull(command.AdditionalContext);
        if (additionalContext is not null && additionalContext.Length > MaxTextLength)
            errors.Add(new FieldError("additionalContext", $"Additional context must be at most {MaxTextLength} characters."));
        string? module = BlankToNull(command.Module);
        if (module is not null && module.Length > 100)
            errors.Add(new FieldError("module", "Module must be at most 100 characters."));

        ValidationException.ThrowIfInvalid(errors);
        return command with
        {
            Title = title,
            Description = description,
            Requirements = requirements,
            TargetUrl = targetUrl,
            Framework = framework,
            Platform = platform,
            Module = module,
            Priority = string.IsNullOrWhiteSpace(priority) ? null : priority.Trim(),
            AdditionalContext = additionalContext,
        };
    }

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
