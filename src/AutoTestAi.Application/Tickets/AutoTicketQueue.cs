using System.Threading.Channels;

namespace AutoTestAi.Application.Tickets;

/// <summary>Outbound automation tuning (Phase 2 Slice 10). Token-free.</summary>
public sealed class AutoTicketOptions
{
    public const string SectionName = "AutoTicket";
    /// <summary>How many due tickets each reconciliation pass picks up.</summary>
    public int ReconciliationBatchSize { get; set; } = 25;
    /// <summary>Seconds between background reconciliation passes.</summary>
    public int ReconciliationIntervalSeconds { get; set; } = 30;
    /// <summary>Seconds an automation claim is valid before it becomes reclaimable (crash recovery).</summary>
    public int ClaimLeaseSeconds { get; set; } = 300;
}

public sealed record AutoTicketWorkItem(Guid TicketId);

/// <summary>
/// Non-durable handoff from the defect-creation request path to the
/// background Jira executor. Durability comes from the persisted Pending
/// ticket row, not this channel: a restart loses queued items but the
/// reconciliation pass re-discovers Pending/Failed-due rows.
/// </summary>
public sealed class AutoTicketQueue
{
    private readonly Channel<AutoTicketWorkItem> _channel =
        Channel.CreateUnbounded<AutoTicketWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    public void Enqueue(AutoTicketWorkItem item) => _channel.Writer.TryWrite(item);

    public IAsyncEnumerable<AutoTicketWorkItem> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public bool TryTake(out AutoTicketWorkItem item)
        => _channel.Reader.TryRead(out item!);
}
