namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Bounded retry classification for automatic Jira creation (Phase 2 Slice 10).
/// Transient transport/rate-limit failures are retryable with exponential
/// backoff; validation/auth/permission/config failures are permanent so a
/// Jira outage or misconfiguration can never spin forever.
/// </summary>
public static class AutoTicketRetryPolicy
{
    public const int MaxAttempts = 5;
    private const int MaxDelaySeconds = 3600;

    public static bool IsRetryable(JiraErrorKind kind) => kind switch
    {
        JiraErrorKind.RateLimited => true,
        JiraErrorKind.Unavailable => true,
        JiraErrorKind.Timeout => true,
        // Validation/auth/permission/not-found/malformed need operator action.
        _ => false,
    };

    /// <summary>Exponential backoff with a longer base for rate limiting.</summary>
    /// <summary>
    /// Exponential backoff with a longer base for rate limiting. A server
    /// `Retry-After` hint is honored when present but always bounded, so a
    /// malicious or absurd header can never park automation indefinitely.
    /// </summary>
    public static TimeSpan DelayForAttempt(JiraErrorKind kind, int attemptNumber, TimeSpan? retryAfter = null)
    {
        var baseSeconds = kind == JiraErrorKind.RateLimited ? 60 : 30;
        var shift = Math.Clamp(attemptNumber - 1, 0, 6);
        var seconds = Math.Min((long)baseSeconds << shift, MaxDelaySeconds);
        if (kind == JiraErrorKind.RateLimited && retryAfter.HasValue)
        {
            var hinted = retryAfter.Value.TotalSeconds;
            if (!double.IsNaN(hinted) && !double.IsInfinity(hinted))
                seconds = (long)Math.Clamp(hinted, 1, MaxDelaySeconds);
        }
        return TimeSpan.FromSeconds(seconds);
    }

    public static string FriendlyError(JiraProviderException ex) => ex.Kind switch
    {
        JiraErrorKind.Validation => "Jira rejected the ticket details. Review the integration configuration.",
        JiraErrorKind.Authentication => "Jira rejected the configured credentials. Check the integration secret.",
        JiraErrorKind.Permission => "Jira denied the request. The configured account lacks permission.",
        JiraErrorKind.NotFound => "The Jira project or endpoint could not be found.",
        JiraErrorKind.RateLimited => "Jira rate-limited the request. A retry is scheduled.",
        JiraErrorKind.Timeout => "Jira did not respond in time. A retry is scheduled; the ticket state is ambiguous, check Jira before manually retrying.",
        _ => "Jira is currently unavailable. A retry is scheduled.",
    };
}
