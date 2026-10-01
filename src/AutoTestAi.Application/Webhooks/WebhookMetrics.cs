using System.Diagnostics.Metrics;

namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Slice 3B webhook metrics (OTEL-compatible System.Diagnostics.Metrics).
/// Never attach secret values, headers, payloads, signatures, or credentials.
/// </summary>
public static class WebhookMetrics
{
    public const string MeterName = "AutoTestAi.Webhooks";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Received = Meter.CreateCounter<long>("webhook_received_total");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("webhook_rejected_total");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("webhook_duplicate_total");
    private static readonly Counter<long> Triggered = Meter.CreateCounter<long>("webhook_triggered_total");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("webhook_failed_total");
    private static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("webhook_processing_duration", unit: "ms");

    private static KeyValuePair<string, object?> Tag(string key, string value)
        => new(key, value);

    public static void DeliveryReceived(string provider) => Received.Add(1, Tag("provider", provider));
    public static void DeliveryRejected(string provider, string reason)
    {
        Rejected.Add(1, Tag("provider", provider), Tag("reason", reason));
    }
    public static void DeliveryDuplicate(string provider) => Duplicates.Add(1, Tag("provider", provider));
    public static void DeliveryTriggered(string provider, int count)
        => Triggered.Add(count, Tag("provider", provider));
    public static void DeliveryFailed(string provider, string reason)
    {
        Failed.Add(1, Tag("provider", provider), Tag("reason", reason));
    }
    public static void RecordProcessingDuration(string provider, double milliseconds)
        => ProcessingDuration.Record(milliseconds, Tag("provider", provider));
}
