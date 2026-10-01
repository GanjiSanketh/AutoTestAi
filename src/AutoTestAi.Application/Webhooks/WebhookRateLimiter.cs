using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Process-local per-project webhook rate guard (Phase 3 Slice 3B).
/// Sliding window, mirroring AiGenerationRateLimiter conventions.
/// Correctness never depends on this limiter: the (IntegrationId, DeliveryId)
/// unique constraint is the duplicate boundary. Distributed rate limiting is
/// explicitly deferred (documented limitation).
/// </summary>
public sealed class WebhookRateLimiter
{
    private readonly IOptions<WebhookOptions> _options;
    private readonly Common.IDateTimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    public WebhookRateLimiter(IOptions<WebhookOptions> options, Common.IDateTimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public void CheckOrThrow(Guid projectId)
    {
        var limit = Math.Max(1, _options.Value.MaxDeliveriesPerMinutePerProject);
        var now = _clock.UtcNow;
        var window = _hits.GetOrAdd(projectId, _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && (now - window.Peek()) > TimeSpan.FromMinutes(1))
                window.Dequeue();
            if (window.Count >= limit)
                throw new WebhookRateLimitedException(
                    $"Webhook rate limit exceeded for this project ({limit} per minute). Retry shortly.");
            window.Enqueue(now);
        }
    }
}
