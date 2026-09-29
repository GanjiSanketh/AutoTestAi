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

/// <summary>Application-facing Jira boundary (Slice 7 §8).</summary>
public interface IJiraTicketProvider
{
    Task<JiraCreateResult> CreateIssueAsync(
        JiraCreateRequest request,
        string Email,
        string ApiToken,
        CancellationToken cancellationToken);
}

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

    public JiraProviderException(JiraErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
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
