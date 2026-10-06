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
/// Per-item result for bulk Jira freshness check (Phase 4 Slice 8).
/// </summary>
public sealed record JiraBulkCheckItemResult(
    Guid VersionId,
    string? Status,
    IReadOnlyList<string>? ChangedFields,
    string? JiraIssueKey,
    string? CheckedAt,
    JiraBulkCheckError? Error);

/// <summary>
/// Error detail for a failed bulk check item (Phase 4 Slice 8).
/// </summary>
public sealed record JiraBulkCheckError(
    string Code,
    string Message);

/// <summary>
/// Bulk Jira freshness-check response (Phase 4 Slice 8).
/// </summary>
public sealed record JiraBulkCheckResult(
    IReadOnlyList<JiraBulkCheckItemResult> Results,
    JiraBulkCheckSummary Summary);

/// <summary>
/// Aggregate summary of bulk freshness check (Phase 4 Slice 8).
/// </summary>
public sealed record JiraBulkCheckSummary(
    int Current,
    int Changed,
    int Errors);

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
/// Transient Jira freshness-check boundary (Phase 4 Slice 7/8). Read-only:
/// one Jira GET per item, zero AI calls, nothing persisted.
/// </summary>
public interface IJiraChangeCheckService
{
    Task<JiraChangeCheckResult> CheckAsync(
        Guid testCaseId, Guid versionId, CancellationToken cancellationToken);

    Task<JiraBulkCheckResult> CheckBulkAsync(
        Guid projectId, IReadOnlyList<Guid> versionIds, CancellationToken cancellationToken);
}
