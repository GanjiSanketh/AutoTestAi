namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Jira freshness-check result (Phase 4 Slice 7 §6). Metadata only:
/// status plus changed-field names. Never old/new content, never raw Jira.
/// </summary>
public sealed record JiraChangeCheckResult(
    string Status,
    IReadOnlyList<string> ChangedFields,
    string JiraIssueKey,
    string CheckedAt);

/// <summary>
/// No usable Jira provenance on the requested version (Phase 4 Slice 7 §7).
/// Maps to 404 with a dedicated code; never triggers a Jira call.
/// </summary>
public sealed class JiraProvenanceNotFoundException : Exception
{
    public JiraProvenanceNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Transient Jira freshness-check boundary (Phase 4 Slice 7). Read-only:
/// one Jira GET, zero AI calls, nothing persisted. Regeneration (if the
/// user wants it) happens separately through the existing Jira story
/// generation endpoint and save flow.
/// </summary>
public interface IJiraChangeCheckService
{
    Task<JiraChangeCheckResult> CheckAsync(
        Guid testCaseId, Guid versionId, CancellationToken cancellationToken);
}
