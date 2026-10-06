using System.Collections.Concurrent;
using AutoTestAi.Application.Tickets;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Narrow process-local guard for Jira story-import reads (Phase 4 Slice 5
/// §19). Separate from the AI generation budget: Jira GETs never consume
/// AI generation calls. Sliding window per project; generous default.
/// Throws the Jira rate-limited failure so the API maps it to 429.
/// </summary>
public sealed class JiraStoryImportRateLimiter
{
    public const int MaxReadsPerMinutePerProject = 30;

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    public void CheckOrThrow(Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var window = _hits.GetOrAdd(projectId, _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && (now - window.Peek()) > TimeSpan.FromMinutes(1))
                window.Dequeue();
            if (window.Count >= MaxReadsPerMinutePerProject)
                throw new JiraProviderException(JiraErrorKind.RateLimited,
                    "Jira import rate limit exceeded for this project. Retry shortly.");
            window.Enqueue(now);
        }
    }
}
