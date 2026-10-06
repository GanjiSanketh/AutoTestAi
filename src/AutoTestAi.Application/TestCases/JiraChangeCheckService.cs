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

    /// <summary>
    /// Bulk Jira freshness check (Phase 4 Slice 8).
    /// Sequential, rate-limited, per-item results. No background processing.
    /// </summary>
    public async Task<JiraBulkCheckResult> CheckBulkAsync(
        Guid projectId, IReadOnlyList<Guid> versionIds, CancellationToken cancellationToken)
    {
        const int MaxBulkSize = 25;

        // Validate input
        if (versionIds is null || versionIds.Count == 0)
            throw new ValidationException("At least one version ID is required.",
                new[] { new FieldError("versionIds", "At least one version ID is required.") });

        var uniqueIds = versionIds.Distinct().ToList();
        if (uniqueIds.Count != versionIds.Count)
        {
            // Duplicates were present - they've been removed
        }
        if (uniqueIds.Count > MaxBulkSize)
            throw new ValidationException($"Maximum {MaxBulkSize} version IDs allowed per bulk request.",
                new[] { new FieldError("versionIds", $"Maximum {MaxBulkSize} version IDs allowed per bulk request.") });
        if (uniqueIds.Count == 0)
            throw new ValidationException("At least one version ID is required after deduplication.",
                new[] { new FieldError("versionIds", "At least one version ID is required after deduplication.") });

        // Authorize once for the project
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, cancellationToken);

        // Resolve all versions and validate they belong to the project
        var versions = new List<Domain.Entities.TestCaseVersion>(uniqueIds.Count);
        var testCases = new Dictionary<Guid, Domain.Entities.TestCase>();
        foreach (var versionId in uniqueIds)
        {
            var version = await _cases.GetVersionByIdAsync(versionId, cancellationToken);
            if (version is null)
            {
                // Version not found - will produce per-item error
                continue;
            }

            var testCase = await _cases.GetByIdAsync(version.TestCaseId, cancellationToken);
            if (testCase is null || testCase.ProjectId != projectId)
            {
                // Version doesn't belong to the authorized project - skip
                continue;
            }

            // Verify this is the current/latest version for its test case
            var latestVersions = await _cases.GetLatestVersionsAsync(new[] { version.TestCaseId }, cancellationToken);
            var latestVersion = latestVersions.GetValueOrDefault(version.TestCaseId);
            if (latestVersion is null || latestVersion.Id != version.Id)
            {
                // Not the current version - will produce per-item error
                continue;
            }

            // Verify Jira provenance
            var provenance = JiraProvenanceReader.TryRead(version.GenerationRequest);
            if (provenance is null || string.IsNullOrWhiteSpace(provenance.JiraIssueKey))
            {
                // Not a Jira-origin version - will produce per-item error
                continue;
            }

            versions.Add(version);
            testCases[version.Id] = testCases.GetValueOrDefault(version.TestCaseId) ?? (await _cases.GetByIdAsync(version.TestCaseId, cancellationToken))!;
        }

        // Audit bulk request
        await _audit.RecordAsync("jira-change-check.bulk-requested", "test_case_version",
            string.Join(",", uniqueIds), projectId,
            SafeBulkAuditJson(projectId, uniqueIds.Count, "requested"), cancellationToken);

        var results = new List<JiraBulkCheckItemResult>();
        int currentCount = 0, changedCount = 0, errorCount = 0;

        // Process sequentially
        foreach (var versionId in uniqueIds)
        {
            var version = versions.FirstOrDefault(v => v.Id == versionId);
            if (version is null)
            {
                // Version not found, not in project, not current, or no Jira provenance
                results.Add(new JiraBulkCheckItemResult(
                    versionId,
                    null, null, null, null,
                    new JiraBulkCheckError("NOT_CURRENT_VERSION", "Version not found, not accessible, not the current version, or lacks Jira provenance.")));
                errorCount++;
                continue;
            }

            var testCase = testCases[version.TestCaseId];

            // Check rate limiter before each call
            try
            {
                _readLimiter.CheckOrThrow(testCase.ProjectId);
            }
            catch (JiraProviderException ex) when (ex.Kind == JiraErrorKind.RateLimited)
            {
                results.Add(new JiraBulkCheckItemResult(
                    version.Id,
                    null, null, null, null,
                    new JiraBulkCheckError("RATE_LIMITED", "Jira freshness check rate limit reached.")));
                errorCount++;
                continue;
            }

            try
            {
                // Perform the check using the existing single-check logic
                var checkResult = await CheckVersionInternalAsync(testCase, version, cancellationToken);
                results.Add(new JiraBulkCheckItemResult(
                    version.Id,
                    checkResult.Status,
                    checkResult.ChangedFields,
                    checkResult.JiraIssueKey,
                    checkResult.CheckedAt,
                    null));
                if (checkResult.Status == "current")
                    currentCount++;
                else if (checkResult.Status == "changed")
                    changedCount++;
            }
            catch (Exception ex)
            {
                var errorCode = MapErrorCode(ex);
                results.Add(new JiraBulkCheckItemResult(
                    version.Id,
                    null, null, null, null,
                    new JiraBulkCheckError(errorCode, GetSafeErrorMessage(ex))));
                errorCount++;
            }
        }

        var summary = new JiraBulkCheckSummary(currentCount, changedCount, errorCount);
        var result = new JiraBulkCheckResult(results, summary);

        // Audit bulk completion
        await _audit.RecordAsync("jira-change-check.bulk-completed", "test_case_version",
            string.Join(",", uniqueIds), projectId,
            SafeBulkAuditJson(projectId, uniqueIds.Count, "completed", currentCount, changedCount, errorCount), cancellationToken);

        return result;
    }

    private async Task<JiraChangeCheckResult> CheckVersionInternalAsync(
        Domain.Entities.TestCase testCase,
        Domain.Entities.TestCaseVersion version,
        CancellationToken cancellationToken)
    {
        var stored = JiraStorySnapshotComparer.TryReadStored(version.GenerationRequest);
        var baselineKey = JiraProvenanceReader.TryRead(version.GenerationRequest)?.JiraIssueKey;
        if (stored is null || baselineKey is null)
            throw new JiraProvenanceNotFoundException("This version was not generated from a Jira issue.");

        await _audit.RecordAsync("jira-change-check.requested", "test_case_version",
            version.Id.ToString(), testCase.ProjectId,
            SafeAuditJson(testCase, version, baselineKey, null, 0, "requested"), cancellationToken);

        var integration = await _integrations.FindByProjectAndProviderAsync(
            testCase.ProjectId, JiraIntegrationConfig.ProviderName, cancellationToken);
        if (integration is null)
            throw new ConflictException("Jira is not configured for this project. Configure the Jira integration first.");
        if (integration.Status != IntegrationStatus.Active)
            throw new ConflictException("The Jira integration is disabled for this project.");
        if (integration.ProjectId != testCase.ProjectId)
            throw new ForbiddenException("The caller has no access to this project.");

        var config = JiraIntegrationConfig.FromJson(integration.Configuration);
        if (string.IsNullOrWhiteSpace(config.BaseUrl) ||
            string.IsNullOrWhiteSpace(config.ProjectKey) ||
            string.IsNullOrWhiteSpace(config.Email) ||
            string.IsNullOrWhiteSpace(integration.SecretReference))
            throw new ConflictException("The Jira integration is not fully configured.");

        try
        {
            _readLimiter.CheckOrThrow(testCase.ProjectId);
        }
        catch (JiraProviderException ex) when (ex.Kind == JiraErrorKind.RateLimited)
        {
            throw new RateLimitedException(ex.Message);
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

        // Same-project isolation
        var configuredProjectKey = JiraIntegrationConfig.NormalizeProjectKey(config.ProjectKey);
        var fetchedProjectKey = (issue.ProjectKey ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.Equals(fetchedProjectKey, configuredProjectKey, StringComparison.Ordinal))
            throw new NotFoundException("The requested Jira issue was not found.");

        JiraNormalizedStory fresh;
        try
        {
            fresh = JiraStoryNormalizer.Normalize(issue.Summary, issue.DescriptionAdfJson, issue.IssueTypeName);
        }
        catch (JiraStoryNormalizationException ex) when (ex.Reason == JiraNormalizationFailure.Empty)
        {
            throw new ValidationException(ex.Message, new[] { new FieldError("issueKey", ex.Message) });
        }
        catch (JiraStoryNormalizationException ex)
        {
            throw new JiraProviderException(JiraErrorKind.MalformedResponse, ex.Message);
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

    private static string MapErrorCode(Exception ex)
    {
        return ex switch
        {
            JiraProviderException jira => jira.Kind switch
            {
                JiraErrorKind.NotFound => "JIRA_NOT_FOUND",
                JiraErrorKind.RateLimited => "RATE_LIMITED",
                JiraErrorKind.Timeout => "JIRA_TIMEOUT",
                JiraErrorKind.Unavailable => "JIRA_UNAVAILABLE",
                JiraErrorKind.MalformedResponse => "JIRA_MALFORMED",
                _ => "CHECK_FAILED"
            },
            JiraProvenanceNotFoundException => "INVALID_PROVENANCE",
            NotFoundException => "JIRA_NOT_FOUND",
            ConflictException => "CONFIGURATION_ERROR",
            ForbiddenException => "FORBIDDEN",
            ValidationException => "VALIDATION_ERROR",
            _ => "CHECK_FAILED"
        };
    }

    private static string GetSafeErrorMessage(Exception ex)
    {
        return ex switch
        {
            JiraProviderException jira => jira.Message,
            JiraProvenanceNotFoundException => ex.Message,
            NotFoundException => ex.Message,
            ConflictException => ex.Message,
            ForbiddenException => ex.Message,
            ValidationException => ex.Message,
            _ => "An unexpected error occurred during the Jira freshness check."
        };
    }

    private static string SafeBulkAuditJson(
        Guid projectId,
        int requestCount,
        string outcome,
        int currentCount = 0,
        int changedCount = 0,
        int errorCount = 0)
        => TestGeneration.SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            checkType = "jira-change-check-bulk",
            projectId,
            requestCount,
            outcome,
            currentCount,
            changedCount,
            errorCount,
        }));
}
