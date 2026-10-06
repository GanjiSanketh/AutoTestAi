using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Transient Jira story-import orchestrator (Phase 4 Slice 5).
/// A thin input adapter: fetch → validate → normalize → delegate. It owns
/// no AI logic — the existing <see cref="IAiStoryTestGenerator"/> pipeline
/// (prompt, budget, validation, redaction, proposal shaping, generation
/// audit) remains authoritative. Nothing is persisted here; saving reuses
/// the existing TestCase creation path.
/// </summary>
public sealed class JiraStoryImportService : IJiraStoryImportService
{
    private readonly IAuthorizationService _authorization;
    private readonly IIntegrationStore _integrations;
    private readonly IJiraTicketProvider _jira;
    private readonly IAiStoryTestGenerator _storyGenerator;
    private readonly JiraStoryImportRateLimiter _readLimiter;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public JiraStoryImportService(
        IAuthorizationService authorization,
        IIntegrationStore integrations,
        IJiraTicketProvider jira,
        IAiStoryTestGenerator storyGenerator,
        JiraStoryImportRateLimiter readLimiter,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _authorization = authorization;
        _integrations = integrations;
        _jira = jira;
        _storyGenerator = storyGenerator;
        _readLimiter = readLimiter;
        _clock = clock;
        _audit = audit;
    }

    public async Task<StoryTestGenerationResult> ImportAndGenerateAsync(
        JiraStoryImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ProjectId == Guid.Empty)
            throw new ValidationException("Project id is required.",
                new[] { new FieldError("projectId", "Project id is required.") });

        var issueKey = JiraIssueKey.NormalizeOrThrow(command.IssueKey);

        // Test-authoring operation: testcases.manage + project membership.
        // tickets.read is NOT required for the internal config lookup.
        await _authorization.RequireProjectAccessAsync(
            command.ProjectId, Permissions.TestCasesManage, cancellationToken);

        ValidateOverrides(command);

        // Narrow Jira-read guard — never the shared AI generation budget.
        try
        {
            _readLimiter.CheckOrThrow(command.ProjectId);
        }
        catch (JiraProviderException ex) when (ex.Kind == JiraErrorKind.RateLimited)
        {
            throw new RateLimitedException(ex.Message);
        }

        await _audit.RecordAsync("jira-story-import.requested", "ai_generation",
            issueKey, command.ProjectId,
            SafeAuditJson(command, issueKey, null, null, 0, 0, "requested"), cancellationToken);

        var integration = await _integrations.FindByProjectAndProviderAsync(
            command.ProjectId, JiraIntegrationConfig.ProviderName, cancellationToken);
        if (integration is null)
            throw await FailedAsync(new ConflictException(
                "Jira is not configured for this project. Configure the Jira integration first."),
                command, issueKey, null, cancellationToken);
        if (integration.Status != IntegrationStatus.Active)
            throw await FailedAsync(new ConflictException(
                "The Jira integration is disabled for this project."),
                command, issueKey, null, cancellationToken);
        if (integration.ProjectId != command.ProjectId)
            throw await FailedAsync(new ForbiddenException(
                "The caller has no access to this project."),
                command, issueKey, null, cancellationToken);

        var config = JiraIntegrationConfig.FromJson(integration.Configuration);
        if (string.IsNullOrWhiteSpace(config.BaseUrl) ||
            string.IsNullOrWhiteSpace(config.ProjectKey) ||
            string.IsNullOrWhiteSpace(config.Email) ||
            string.IsNullOrWhiteSpace(integration.SecretReference))
            throw await FailedAsync(new ConflictException(
                "The Jira integration is not fully configured."),
                command, issueKey, null, cancellationToken);

        JiraIssueDto issue;
        try
        {
            issue = await _jira.GetIssueAsync(
                new JiraIssueRequest(
                    JiraIntegrationConfig.NormalizeBaseUrl(config.BaseUrl),
                    issueKey),
                config.Email.Trim(),
                integration.SecretReference,
                cancellationToken);
        }
        catch (JiraProviderException ex)
        {
            throw await FailedAsync(ex, command, issueKey, null, cancellationToken);
        }

        // Same-project isolation: the fetched Jira project must equal the
        // configured Jira project. Mismatches are opaque — no disclosure of
        // whether another Jira project contains the issue.
        var configuredProjectKey = JiraIntegrationConfig.NormalizeProjectKey(config.ProjectKey);
        var fetchedProjectKey = (issue.ProjectKey ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.Equals(fetchedProjectKey, configuredProjectKey, StringComparison.Ordinal))
            throw await FailedAsync(new NotFoundException("The requested Jira issue was not found."),
                command, issueKey, issue.IssueTypeName, cancellationToken);

        JiraNormalizedStory normalized;
        try
        {
            normalized = JiraStoryNormalizer.Normalize(issue.Summary, issue.DescriptionAdfJson, issue.IssueTypeName);
        }
        catch (JiraStoryNormalizationException ex) when (ex.Reason == JiraNormalizationFailure.Empty)
        {
            throw await FailedAsync(new ValidationException(ex.Message,
                new[] { new FieldError("issueKey", ex.Message) }),
                command, issueKey, issue.IssueTypeName, cancellationToken);
        }
        catch (JiraStoryNormalizationException ex)
        {
            throw await FailedAsync(new JiraProviderException(JiraErrorKind.MalformedResponse, ex.Message),
                command, issueKey, issue.IssueTypeName, cancellationToken);
        }

        var fetchedAt = _clock.UtcNow;
        var storyCommand = new GenerateStoryTestsCommand(
            command.ProjectId,
            normalized.StoryTitle,
            normalized.StoryDescription,
            normalized.AcceptanceCriteria,
            command.TargetUrl?.Trim(),
            command.Framework.Trim(),
            command.Platform.Trim(),
            string.IsNullOrWhiteSpace(command.Module) ? null : command.Module.Trim(),
            string.IsNullOrWhiteSpace(command.Priority) ? null : command.Priority.Trim(),
            string.IsNullOrWhiteSpace(command.AdditionalContext) ? null : command.AdditionalContext.Trim(),
            command.MaxProposals,
            new JiraImportMetadata(
                issue.IssueKey.Trim().ToUpperInvariant(),
                normalized.IssueType,
                ExtractHost(config.BaseUrl),
                fetchedAt.ToString("o"),
                JiraStoryNormalizer.NormalizerVersion));

        StoryTestGenerationResult result;
        try
        {
            result = await _storyGenerator.GenerateStoryProposalsAsync(storyCommand, cancellationToken);
        }
        catch (Exception ex) when (ex is ValidationException or RateLimitedException)
        {
            throw await FailedAsync(ex, command, issueKey, normalized.IssueType, cancellationToken);
        }

        await _audit.RecordAsync("jira-story-import.completed", "ai_generation",
            issueKey, command.ProjectId,
            SafeAuditJson(command, issueKey, normalized.IssueType, normalized,
                result.ProposalCount, result.SuccessCount, "completed"), cancellationToken);

        return result;
    }

    private static void ValidateOverrides(JiraStoryImportCommand command)
    {
        var errors = new List<FieldError>();
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
        if (command.MaxProposals < 1 || command.MaxProposals > StoryTestGenerationService.MaxProposalsPerRequest)
            errors.Add(new FieldError("maxProposals",
                $"Max proposals must be between 1 and {StoryTestGenerationService.MaxProposalsPerRequest}."));
        var targetUrl = command.TargetUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(targetUrl) &&
            (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            errors.Add(new FieldError("targetUrl", "Target URL must be an absolute http(s) URL."));
        var priority = command.Priority?.Trim();
        if (!string.IsNullOrWhiteSpace(priority) &&
            !Enum.TryParse<Priority>(priority, ignoreCase: true, out _))
            errors.Add(new FieldError("priority", "Priority must be 'Critical', 'High', 'Medium' or 'Low'."));
        if (!string.IsNullOrWhiteSpace(command.Module) && command.Module.Trim().Length > 100)
            errors.Add(new FieldError("module", "Module must be at most 100 characters."));
        if (!string.IsNullOrWhiteSpace(command.AdditionalContext) && command.AdditionalContext.Trim().Length > 4000)
            errors.Add(new FieldError("additionalContext", "Additional context must be at most 4000 characters."));
        ValidationException.ThrowIfInvalid(errors);
    }

    private async Task<T> FailedAsync<T>(
        T exception, JiraStoryImportCommand command, string issueKey, string? issueType,
        CancellationToken cancellationToken) where T : Exception
    {
        // Best-effort failure audit with safe metadata only — never story text.
        var kind = exception is JiraProviderException jira
            ? jira.Kind.ToString()
            : exception.GetType().Name;
        await _audit.RecordAsync("jira-story-import.failed", "ai_generation",
            issueKey, command.ProjectId,
            SafeAuditJson(command, issueKey, issueType, null, 0, 0, "failed:" + kind), cancellationToken);
        return exception;
    }

    private static string SafeAuditJson(
        JiraStoryImportCommand command, string issueKey, string? issueType,
        JiraNormalizedStory? normalized, int proposalCount, int successCount, string outcome)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            // Jira story text never enters audit metadata: lengths/counts only.
            generationType = "story-from-jira",
            jiraIssueKey = issueKey,
            jiraIssueType = issueType,
            storyTitleLength = normalized?.StoryTitle.Length ?? 0,
            acceptanceCriteriaCount = normalized?.AcceptanceCriteria.Count ?? 0,
            framework = command.Framework?.Trim(),
            platform = command.Platform?.Trim(),
            proposalCount,
            successCount,
            maxProposals = command.MaxProposals,
            promptVersion = AI.AiPromptVersions.StoryToTestsV1,
            outcome,
        }));

    private static string ExtractHost(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host.Trim().ToLowerInvariant();
        return string.Empty;
    }
}
