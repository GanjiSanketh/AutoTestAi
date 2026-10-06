using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Jira freshness-check orchestrator (Phase 4 Slice 7).
/// Read-only by construction: resolve version → verify Jira baseline →
/// fetch current issue → normalize with the SAME normalizer → compare.
/// Exactly one Jira GET, zero AI calls, nothing persisted, nothing mutated.
/// Failed checks never alter the TestCaseVersion or its provenance.
/// </summary>
public sealed class JiraChangeCheckService : IJiraChangeCheckService
{
    private readonly ITestCaseStore _cases;
    private readonly IAuthorizationService _authorization;
    private readonly IIntegrationStore _integrations;
    private readonly IJiraTicketProvider _jira;
    private readonly JiraStoryImportRateLimiter _readLimiter;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public JiraChangeCheckService(
        ITestCaseStore cases,
        IAuthorizationService authorization,
        IIntegrationStore integrations,
        IJiraTicketProvider jira,
        JiraStoryImportRateLimiter readLimiter,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _cases = cases;
        _authorization = authorization;
        _integrations = integrations;
        _jira = jira;
        _readLimiter = readLimiter;
        _clock = clock;
        _audit = audit;
    }

    public async Task<JiraChangeCheckResult> CheckAsync(
        Guid testCaseId, Guid versionId, CancellationToken cancellationToken)
    {
        var testCase = await _cases.GetByIdAsync(testCaseId, cancellationToken);
        // Same opaque-scope convention as TestCaseService: unknown ids
        // authorize against the id itself (403 for non-admins, never a
        // revealing 404); admins get the true 404 below.
        await _authorization.RequireProjectAccessAsync(
            testCase?.ProjectId ?? testCaseId, Permissions.TestCasesManage, cancellationToken);
        if (testCase is null)
            throw new NotFoundException("Test case not found.");

        var version = await _cases.GetVersionByIdAsync(versionId, cancellationToken);
        if (version is null || version.TestCaseId != testCase.Id)
            throw new NotFoundException("Test case version not found.");

        var stored = JiraStorySnapshotComparer.TryReadStored(version.GenerationRequest);
        var baselineKey = JiraProvenanceReader.TryRead(version.GenerationRequest)?.JiraIssueKey;
        if (stored is null || baselineKey is null)
            throw await FailedAsync(new JiraProvenanceNotFoundException(
                "This version was not generated from a Jira issue."),
                testCase, version, null, cancellationToken);

        await _audit.RecordAsync("jira-change-check.requested", "test_case_version",
            version.Id.ToString(), testCase.ProjectId,
            SafeAuditJson(testCase, version, baselineKey, null, 0, "requested"), cancellationToken);

        var integration = await _integrations.FindByProjectAndProviderAsync(
            testCase.ProjectId, JiraIntegrationConfig.ProviderName, cancellationToken);
        if (integration is null)
            throw await FailedAsync(new ConflictException(
                "Jira is not configured for this project. Configure the Jira integration first."),
                testCase, version, baselineKey, cancellationToken);
        if (integration.Status != IntegrationStatus.Active)
            throw await FailedAsync(new ConflictException(
                "The Jira integration is disabled for this project."),
                testCase, version, baselineKey, cancellationToken);
        if (integration.ProjectId != testCase.ProjectId)
            throw await FailedAsync(new ForbiddenException(
                "The caller has no access to this project."),
                testCase, version, baselineKey, cancellationToken);

        var config = JiraIntegrationConfig.FromJson(integration.Configuration);
        if (string.IsNullOrWhiteSpace(config.BaseUrl) ||
            string.IsNullOrWhiteSpace(config.ProjectKey) ||
            string.IsNullOrWhiteSpace(config.Email) ||
            string.IsNullOrWhiteSpace(integration.SecretReference))
            throw await FailedAsync(new ConflictException(
                "The Jira integration is not fully configured."),
                testCase, version, baselineKey, cancellationToken);

        try
        {
            _readLimiter.CheckOrThrow(testCase.ProjectId);
        }
        catch (JiraProviderException ex) when (ex.Kind == JiraErrorKind.RateLimited)
        {
            throw await FailedAsync(new RateLimitedException(ex.Message),
                testCase, version, baselineKey, cancellationToken);
        }

        JiraIssueDto issue;
        try
        {
            issue = await _jira.GetIssueAsync(
                new JiraIssueRequest(
                    JiraIntegrationConfig.NormalizeBaseUrl(config.BaseUrl),
                    baselineKey),
                config.Email.Trim(),
                integration.SecretReference,
                cancellationToken);
        }
        catch (JiraProviderException ex)
        {
            throw await FailedAsync(ex, testCase, version, baselineKey, cancellationToken);
        }

        // Same-project isolation, mirroring the Slice-5 import trust boundary.
        var configuredProjectKey = JiraIntegrationConfig.NormalizeProjectKey(config.ProjectKey);
        var fetchedProjectKey = (issue.ProjectKey ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.Equals(fetchedProjectKey, configuredProjectKey, StringComparison.Ordinal))
            throw await FailedAsync(new NotFoundException("The requested Jira issue was not found."),
                testCase, version, baselineKey, cancellationToken);

        JiraNormalizedStory fresh;
        try
        {
            fresh = JiraStoryNormalizer.Normalize(issue.Summary, issue.DescriptionAdfJson, issue.IssueTypeName);
        }
        catch (JiraStoryNormalizationException ex) when (ex.Reason == JiraNormalizationFailure.Empty)
        {
            throw await FailedAsync(new ValidationException(ex.Message,
                new[] { new FieldError("issueKey", ex.Message) }),
                testCase, version, baselineKey, cancellationToken);
        }
        catch (JiraStoryNormalizationException ex)
        {
            throw await FailedAsync(new JiraProviderException(JiraErrorKind.MalformedResponse, ex.Message),
                testCase, version, baselineKey, cancellationToken);
        }

        var (status, changedFields) = JiraStorySnapshotComparer.Compare(
            stored, JiraStorySnapshotComparer.FromNormalized(fresh));
        var result = new JiraChangeCheckResult(
            status,
            changedFields,
            baselineKey,
            _clock.UtcNow.ToString("o"));

        await _audit.RecordAsync("jira-change-check.completed", "test_case_version",
            version.Id.ToString(), testCase.ProjectId,
            SafeAuditJson(testCase, version, baselineKey, result, changedFields.Count, "completed"),
            cancellationToken);

        return result;
    }

    private async Task<T> FailedAsync<T>(
        T exception,
        Domain.Entities.TestCase testCase,
        Domain.Entities.TestCaseVersion version,
        string? issueKey,
        CancellationToken cancellationToken) where T : Exception
    {
        // Best-effort failure audit with safe metadata only — never content.
        var kind = exception is JiraProviderException jira
            ? jira.Kind.ToString()
            : exception.GetType().Name;
        await _audit.RecordAsync("jira-change-check.failed", "test_case_version",
            version.Id.ToString(), testCase.ProjectId,
            SafeAuditJson(testCase, version, issueKey, null, 0, "failed:" + kind), cancellationToken);
        return exception;
    }

    private static string SafeAuditJson(
        Domain.Entities.TestCase testCase,
        Domain.Entities.TestCaseVersion version,
        string? issueKey,
        JiraChangeCheckResult? result,
        int changedFieldCount,
        string outcome)
        => TestGeneration.SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            // Jira story text never enters audit metadata: identifiers only.
            checkType = "jira-change-check",
            testCaseId = testCase.Id,
            versionId = version.Id,
            versionNumber = version.VersionNumber,
            jiraIssueKey = issueKey,
            status = result?.Status,
            changedFieldCount,
            outcome,
        }));
}
