namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Provider-neutral Jira create request. Application code never touches Jira
/// HTTP details; Infrastructure owns transport, auth, and DTOs.
/// </summary>
public sealed record JiraCreateRequest(
    string BaseUrl,
    string ProjectKey,
    string IssueType,
    string Summary,
    string Description,
    string? Priority);

/// <summary>Normalized provider response (no Jira SDK types leak out).</summary>
public sealed record JiraCreateResult(
    string ExternalId,
    string ExternalKey,
    string ExternalUrl);

/// <summary>Application-facing Jira boundary (Slice 7 §8, extended Slice 5 §3).</summary>
public interface IJiraTicketProvider
{
    Task<JiraCreateResult> CreateIssueAsync(
        JiraCreateRequest request,
        string Email,
        string ApiToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transient single-issue read for story import (Phase 4 Slice 5).
    /// Fetches one issue by key with a narrow fields projection; never
    /// searches, never bulk-fetches. Infrastructure owns HTTP/DTOs.
    /// </summary>
    Task<JiraIssueDto> GetIssueAsync(
        JiraIssueRequest request,
        string Email,
        string ApiToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral Jira single-issue read request (Phase 4 Slice 5).
/// The Jira server comes from the project integration configuration —
/// never from caller-supplied URLs.
/// </summary>
public sealed record JiraIssueRequest(
    string BaseUrl,
    string IssueKey);

/// <summary>
/// Minimal normalized Jira issue (Phase 4 Slice 5 §6). Only the fields the
/// story-import slice needs; comments, attachments, links, custom fields,
/// and all other metadata are never projected here.
/// DescriptionAdfJson is the raw Atlassian Document Format subtree for the
/// description field only (null when absent), serialized as JSON text for
/// the Application-level ADF normalizer. The Jira issue envelope itself
/// never crosses this boundary.
/// </summary>
public sealed record JiraIssueDto(
    string IssueKey,
    string Summary,
    string? DescriptionAdfJson,
    string IssueTypeName,
    string ProjectKey);

/// <summary>Machine-readable Jira failure kinds for error mapping.</summary>
public enum JiraErrorKind
{
    Validation,
    Authentication,
    Permission,
    NotFound,
    Conflict,
    RateLimited,
    Unavailable,
    Timeout,
    MalformedResponse,
    Cancelled,
}

/// <summary>
/// Controlled Jira failure. Carries no secrets or raw provider bodies —
/// safe to map into the API ProblemDetails envelope.
/// </summary>
public sealed class JiraProviderException : Exception
{
    public JiraErrorKind Kind { get; }

    /// <summary>
    /// Server-requested retry delay from the Jira `Retry-After` response
    /// header (rate limiting only). Null when absent, unparsable, or not
    /// applicable. Consumers must bound it before scheduling.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public JiraProviderException(JiraErrorKind kind, string message, Exception? inner = null, TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public static JiraProviderException Validation(string message)
        => new(JiraErrorKind.Validation, message);

    public static JiraProviderException Authentication(string message)
        => new(JiraErrorKind.Authentication, message);

    public static JiraProviderException Permission(string message)
        => new(JiraErrorKind.Permission, message);

    public static JiraProviderException NotFound(string message)
        => new(JiraErrorKind.NotFound, message);

    public static JiraProviderException Unavailable(string message, Exception? inner = null)
        => new(JiraErrorKind.Unavailable, message, inner);

    public static JiraProviderException Timeout(string message, Exception? inner = null)
        => new(JiraErrorKind.Timeout, message, inner);
}
