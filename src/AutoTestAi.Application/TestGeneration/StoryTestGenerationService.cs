using System.Diagnostics;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Manual story to test proposals orchestrator (Phase 4 Slice 3).
/// An additional consumer of proven AI infrastructure: provider resolution,
/// the shared per-project generation budget, output validation, redaction,
/// and audit behave exactly like generic generation. Differences: the
/// story-to-tests-v1 prompt contract, sequential fan-out (at most 10
/// provider calls), per-proposal results, and NO persistence — proposals
/// are returned for user selection and saved through TestCaseService.
/// </summary>
public sealed class StoryTestGenerationService : IAiStoryTestGenerator
{
    public const int MaxProposalsPerRequest = 10;
    public const int DefaultProposals = 10;

    private const int MaxAcceptanceCriteria = 50;
    private const int MaxAcceptanceCriterionLength = 2000;
    private const int MaxTextLength = 4000;

    private const string StatusSucceeded = "Succeeded";
    private const string StatusFailed = "Failed";

    private readonly IAiProviderResolver _resolver;
    private readonly AiGenerationValidator _validator;
    private readonly AiGenerationRateLimiter _rateLimiter;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditService _audit;

    public StoryTestGenerationService(
        IAiProviderResolver resolver,
        AiGenerationValidator validator,
        AiGenerationRateLimiter rateLimiter,
        IAuthorizationService authorization,
        IAuditService audit)
    {
        _resolver = resolver;
        _validator = validator;
        _rateLimiter = rateLimiter;
        _authorization = authorization;
        _audit = audit;
    }

    public async Task<StoryTestGenerationResult> GenerateStoryProposalsAsync(
        GenerateStoryTestsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = NormalizeAndValidate(command);

        // Authorization boundary: testcases.manage + project membership (admin bypass).
        await _authorization.RequireProjectAccessAsync(
            normalized.ProjectId, Permissions.TestCasesManage, cancellationToken);

        var provider = _resolver.Resolve();

        var generationId = Guid.NewGuid();
        var count = normalized.MaxProposals;
        await _audit.RecordAsync("story-generation.requested", "ai_generation",
            generationId.ToString(), normalized.ProjectId,
            SafeAuditJson(normalized, provider.Name, null, count, 0, 0, "requested"), cancellationToken);

        var proposals = new List<StoryTestProposal>(count);
        var covered = new List<int>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var focusIndex = i % normalized.AcceptanceCriteria.Count;
                var proposal = await GenerateOneAsync(
                    normalized, provider, generationId, i + 1, focusIndex, covered,
                    cancellationToken);
                proposals.Add(proposal);
                if (proposal.Status == StatusSucceeded)
                {
                    if (!covered.Contains(focusIndex))
                        covered.Add(focusIndex);
                }
                else if (proposal.ErrorCode == "RATE_LIMITED")
                {
                    // Further calls would fail identically: mark the remainder
                    // failed with the same budget error instead of calling out.
                    for (var j = i + 1; j < count; j++)
                    {
                        var remainderFocus = j % normalized.AcceptanceCriteria.Count;
                        proposals.Add(FailedProposal(
                            j + 1, remainderFocus,
                            normalized.AcceptanceCriteria[remainderFocus],
                            "RATE_LIMITED", proposal.ErrorMessage));
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _audit.RecordAsync("story-generation.failed", "ai_generation",
                generationId.ToString(), normalized.ProjectId,
                SafeAuditJson(normalized, provider.Name, null, count,
                    proposals.Count(p => p.Status == StatusSucceeded),
                    proposals.Count(p => p.Status == StatusFailed), "cancelled"), cancellationToken);
            throw;
        }

        var succeeded = proposals.Count(p => p.Status == StatusSucceeded);
        var failed = proposals.Count(p => p.Status == StatusFailed);
        await _audit.RecordAsync("story-generation.completed", "ai_generation",
            generationId.ToString(), normalized.ProjectId,
            SafeAuditJson(normalized, provider.Name, null, count, succeeded, failed, "completed"),
            cancellationToken);

        return new StoryTestGenerationResult(
            generationId, AiPromptVersions.StoryToTestsV1, proposals.Count, succeeded, failed, proposals);
    }

    private async Task<StoryTestProposal> GenerateOneAsync(
        GenerateStoryTestsCommand normalized, IAiProvider provider, Guid generationId,
        int index, int focusIndex, IReadOnlyList<int> covered, CancellationToken cancellationToken)
    {
        var focus = normalized.AcceptanceCriteria[focusIndex];
        var proposalId = Guid.NewGuid().ToString("N");

        // Shared generation budget: each proposal consumes one call from the
        // same per-project 20/minute bucket as generic generation, so a
        // 10-proposal request can never exceed half the budget.
        try
        {
            _rateLimiter.CheckOrThrow(normalized.ProjectId, provider.Name);
        }
        catch (AiProviderException ex) when (ex.Kind == AiProviderErrorKind.RateLimited)
        {
            return FailedProposal(index, focusIndex, focus, "RATE_LIMITED", ex.Message);
        }

        var providerRequest = new AiGenerationRequest(
            normalized.StoryTitle,
            normalized.StoryDescription,
            normalized.TargetUrl,
            normalized.Framework,
            normalized.Platform,
            normalized.AcceptanceCriteria,
            normalized.Module,
            normalized.Priority,
            normalized.AdditionalContext,
            new AiStoryContext(
                normalized.StoryTitle,
                normalized.StoryDescription,
                normalized.AcceptanceCriteria,
                focusIndex,
                focus,
                covered.ToList()));

        AiGenerationResult result;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            result = await provider.GenerateTestAsync(providerRequest, cancellationToken);
        }
        catch (AiProviderException ex)
        {
            return FailedProposal(index, focusIndex, focus, MapErrorCode(ex.Kind), ex.Message);
        }
        stopwatch.Stop();
        var latencyMs = stopwatch.ElapsedMilliseconds;
        result = result with { LatencyMs = latencyMs };

        try
        {
            _validator.ValidateOrThrow(result, providerRequest);
        }
        catch (ValidationException ex)
        {
            return FailedProposal(index, focusIndex, focus, "PROVIDER_RESPONSE_ERROR", ex.Message);
        }

        var promptVersion = string.IsNullOrWhiteSpace(result.PromptVersion)
            ? AiPromptVersions.StoryToTestsV1
            : result.PromptVersion.Trim();
        var steps = result.EffectiveStructuredSteps()
            .OrderBy(s => s.Order)
            .Select(s => new GeneratedTestStepDto(s.Order, s.Action, s.Target, s.Value))
            .ToList();
        var provenance = BuildRedactedStoryProvenance(
            normalized, focusIndex, proposalId, generationId).RootElement;

        return new StoryTestProposal(
            proposalId, index, StatusSucceeded,
            (result.Title ?? normalized.StoryTitle).Trim(),
            result.Description ?? normalized.StoryDescription,
            (result.Framework ?? normalized.Framework).Trim(),
            (result.Platform ?? normalized.Platform).Trim(),
            normalized.Module,
            normalized.Priority,
            focus, focusIndex,
            steps,
            result.SourceCode.Trim(),
            result.Assumptions ?? Array.Empty<string>(),
            result.Warnings ?? Array.Empty<string>(),
            result.Provider, result.Model, promptVersion, latencyMs,
            result.InputTokens, result.OutputTokens, result.TotalTokens,
            provenance, null, null);
    }

    private static StoryTestProposal FailedProposal(
        int index, int focusIndex, string? focus, string errorCode, string? errorMessage)
        => new(Guid.NewGuid().ToString("N"), index, StatusFailed,
            null, null, null, null, null, null, focus, focusIndex,
            Array.Empty<GeneratedTestStepDto>(), null,
            Array.Empty<string>(), Array.Empty<string>(),
            null, null, AiPromptVersions.StoryToTestsV1, 0, null, null, null,
            null, errorCode, errorMessage);

    private static string MapErrorCode(AiProviderErrorKind kind) => kind switch
    {
        AiProviderErrorKind.RateLimited => "RATE_LIMITED",
        AiProviderErrorKind.NotConfigured => "PROVIDER_NOT_CONFIGURED",
        AiProviderErrorKind.UnsupportedProvider => "PROVIDER_NOT_SUPPORTED",
        AiProviderErrorKind.Unavailable => "PROVIDER_UNAVAILABLE",
        AiProviderErrorKind.Timeout => "PROVIDER_TIMEOUT",
        _ => "PROVIDER_RESPONSE_ERROR",
    };

    private static JsonDocument BuildRedactedStoryProvenance(
        GenerateStoryTestsCommand normalized, int focusIndex, string proposalId, Guid generationId)
    {
        // Redact user-controlled strings BEFORE serialization (mirrors the
        // generic generation provenance path).
        static string R(string? value) => SensitiveDataRedactor.Redact(value ?? string.Empty);
        // Phase 4 Slice 5: Jira import origin is additive. The base shape is
        // built first so that when Jira context is absent the serialized
        // bytes are unchanged from Slice 3; Jira fields append after.
        var payload = new Dictionary<string, object?>
        {
            ["storyTitle"] = R(normalized.StoryTitle),
            ["storyDescription"] = R(normalized.StoryDescription),
            ["acceptanceCriteria"] = normalized.AcceptanceCriteria.Select(R).ToList(),
            ["focusCriterionIndex"] = focusIndex,
            ["promptVersion"] = AiPromptVersions.StoryToTestsV1,
            ["source"] = "story-ai",
        };
        var jira = normalized.JiraImport;
        if (jira is not null)
        {
            payload["origin"] = "jira-import";
            payload["jiraIssueKey"] = R(jira.IssueKey);
            payload["jiraIssueType"] = R(jira.IssueType);
            payload["jiraBaseUrlHost"] = R(jira.BaseUrlHost);
            payload["jiraFetchedAt"] = R(jira.FetchedAt);
            // Phase 4 Slice 7 §15: informational comparison metadata for
            // future normalization contracts. Legacy rows without it remain
            // fully checkable; never backfilled, never required.
            if (!string.IsNullOrWhiteSpace(jira.NormalizerVersion))
                payload["normalizerVersion"] = R(jira.NormalizerVersion);
        }
        payload["generationId"] = generationId.ToString();
        payload["proposalId"] = proposalId;
        return JsonDocument.Parse(SensitiveDataRedactor.Redact(JsonSerializer.Serialize(payload)));
    }

    private static string SafeAuditJson(
        GenerateStoryTestsCommand normalized, string provider, string? model,
        int proposalCount, int successCount, int failureCount, string outcome)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            // Story text never enters audit metadata: counts only.
            generationType = "story",
            storyTitleLength = normalized.StoryTitle.Length,
            acceptanceCriteriaCount = normalized.AcceptanceCriteria.Count,
            framework = normalized.Framework,
            platform = normalized.Platform,
            proposalCount,
            successCount,
            failureCount,
            provider,
            model,
            promptVersion = AiPromptVersions.StoryToTestsV1,
            outcome,
        }));

    private static GenerateStoryTestsCommand NormalizeAndValidate(GenerateStoryTestsCommand command)
    {
        var errors = new List<FieldError>();
        if (command.ProjectId == Guid.Empty)
            errors.Add(new FieldError("projectId", "Project id is required."));
        var storyTitle = command.StoryTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(storyTitle))
            errors.Add(new FieldError("storyTitle", "Story title is required."));
        else if (storyTitle.Length > 200)
            errors.Add(new FieldError("storyTitle", "Story title must be at most 200 characters."));
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

        var criteria = (command.AcceptanceCriteria ?? Array.Empty<string>())
            .Select(r => r?.Trim() ?? string.Empty)
            .Where(r => r.Length > 0)
            .ToList();
        if (criteria.Count == 0)
            errors.Add(new FieldError("acceptanceCriteria", "At least one acceptance criterion is required."));
        else if (criteria.Count > MaxAcceptanceCriteria)
            errors.Add(new FieldError("acceptanceCriteria", $"At most {MaxAcceptanceCriteria} acceptance criteria are accepted."));
        foreach (var criterion in criteria)
        {
            if (criterion.Length > MaxAcceptanceCriterionLength)
            {
                errors.Add(new FieldError("acceptanceCriteria", $"Each acceptance criterion must be at most {MaxAcceptanceCriterionLength} characters."));
                break;
            }
        }

        if (command.MaxProposals < 1 || command.MaxProposals > MaxProposalsPerRequest)
            errors.Add(new FieldError("maxProposals", $"Max proposals must be between 1 and {MaxProposalsPerRequest}."));

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

        string? storyDescription = BlankToNull(command.StoryDescription);
        if (storyDescription is not null && storyDescription.Length > MaxTextLength)
            errors.Add(new FieldError("storyDescription", $"Story description must be at most {MaxTextLength} characters."));
        string? additionalContext = BlankToNull(command.AdditionalContext);
        if (additionalContext is not null && additionalContext.Length > MaxTextLength)
            errors.Add(new FieldError("additionalContext", $"Additional context must be at most {MaxTextLength} characters."));
        string? module = BlankToNull(command.Module);
        if (module is not null && module.Length > 100)
            errors.Add(new FieldError("module", "Module must be at most 100 characters."));

        ValidationException.ThrowIfInvalid(errors);
        return command with
        {
            StoryTitle = storyTitle,
            StoryDescription = storyDescription,
            AcceptanceCriteria = criteria,
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
