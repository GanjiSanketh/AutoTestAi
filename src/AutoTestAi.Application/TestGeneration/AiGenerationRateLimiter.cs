using System.Collections.Concurrent;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.TestGeneration;

/// <summary>
/// Minimal application-level guard so generation cannot be triggered in an
/// unbounded loop (Slice 4 §30). Sliding window per project; generous default
/// (AiOptions.MaxGenerationsPerMinutePerProject). This complements — not replaces —
/// provider timeouts, cancellation, and upstream 429 handling.
/// </summary>
public sealed class AiGenerationRateLimiter
{
    private readonly IOptions<AiOptions> _options;
    private readonly IDateTimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    public AiGenerationRateLimiter(IOptions<AiOptions> options, IDateTimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public void CheckOrThrow(Guid projectId, string provider)
    {
        var limit = Math.Max(1, _options.Value.MaxGenerationsPerMinutePerProject);
        var now = _clock.UtcNow;
        var window = _hits.GetOrAdd(projectId, _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && (now - window.Peek()) > TimeSpan.FromMinutes(1))
                window.Dequeue();
            if (window.Count >= limit)
                throw AiProviderException.RateLimited(provider,
                    $"AI generation rate limit exceeded for this project ({limit} per minute). Retry shortly.");
            window.Enqueue(now);
        }
    }
}
