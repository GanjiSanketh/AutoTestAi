using System.Collections.Concurrent;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Minimal application-level guard so AI operations cannot be triggered in an
/// unbounded loop (Slices 4/6). Sliding window per (project, operation); generous
/// defaults. This complements — not replaces — provider timeouts, cancellation,
/// and upstream 429 handling.
/// </summary>
public sealed class AiGenerationRateLimiter
{
    private readonly IOptions<AiOptions> _options;
    private readonly IDateTimeProvider _clock;
    private readonly ConcurrentDictionary<(Guid ProjectId, string Operation), Queue<DateTimeOffset>> _hits = new();

    public AiGenerationRateLimiter(IOptions<AiOptions> options, IDateTimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public void CheckOrThrow(Guid projectId, string provider)
        => CheckOperationOrThrow(projectId, "generation", provider,
            Math.Max(1, _options.Value.MaxGenerationsPerMinutePerProject),
            "AI generation rate limit exceeded for this project");

    /// <summary>Operation-scoped budget (e.g. "analysis") sharing the same window mechanics.</summary>
    public void CheckOperationOrThrow(
        Guid projectId, string operation, string provider, int limitPerMinute, string messagePrefix)
    {
        var limit = Math.Max(1, limitPerMinute);
        var now = _clock.UtcNow;
        var window = _hits.GetOrAdd((projectId, operation), _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && (now - window.Peek()) > TimeSpan.FromMinutes(1))
                window.Dequeue();
            if (window.Count >= limit)
                throw AiProviderException.RateLimited(provider,
                    $"{messagePrefix} ({limit} per minute). Retry shortly.");
            window.Enqueue(now);
        }
    }
}
