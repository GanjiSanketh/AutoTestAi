using System.Threading.Channels;

namespace AutoTestAi.Application.Webhooks;

public sealed record WebhookWorkItem(Guid DeliveryId);

/// <summary>
/// Non-durable handoff from the webhook request path to the background
/// processor. Durability comes from the persisted Accepted delivery row, not
/// this channel: a restart loses queued items but reconciliation re-discovers
/// stale Accepted rows (mirrors AutoTicketQueue).
/// </summary>
public sealed class WebhookQueue
{
    private readonly Channel<WebhookWorkItem> _channel =
        Channel.CreateUnbounded<WebhookWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    public void Enqueue(WebhookWorkItem item) => _channel.Writer.TryWrite(item);

    public IAsyncEnumerable<WebhookWorkItem> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public bool TryTake(out WebhookWorkItem item)
        => _channel.Reader.TryRead(out item!);
}
