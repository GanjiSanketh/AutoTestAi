using System.Diagnostics.Metrics;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Slice 3C-3 mobile slot metrics (OTEL-compatible System.Diagnostics.Metrics,
/// mirroring WebhookMetrics). Labels are closed sets only (platform, result):
/// never tokens, UDIDs, pool names, capabilities, or secrets.
/// </summary>
public static class MobileMetrics
{
    public const string MeterName = "AutoTestAi.Mobile";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Claims = Meter.CreateCounter<long>("mobile_slot_claim_total");
    private static readonly Counter<long> ClaimConflicts = Meter.CreateCounter<long>("mobile_slot_claim_conflict_total");
    private static readonly Counter<long> Expired = Meter.CreateCounter<long>("mobile_slot_expired_total");
    private static readonly Counter<long> Released = Meter.CreateCounter<long>("mobile_slot_released_total");
    private static readonly Counter<long> Renewed = Meter.CreateCounter<long>("mobile_slot_renewed_total");
    private static readonly Counter<long> Recovered = Meter.CreateCounter<long>("mobile_slot_recovered_total");

    private static KeyValuePair<string, object?> Tag(string key, string value)
        => new(key, value);

    public static void SlotClaimed(string platform) => Claims.Add(1, Tag("platform", platform), Tag("result", "claimed"));
    public static void SlotClaimConflict(string platform) => ClaimConflicts.Add(1, Tag("platform", platform));
    public static void SlotExpired(string platform, string reason)
        => Expired.Add(1, Tag("platform", platform), Tag("reason", reason));
    public static void SlotReleased(string platform) => Released.Add(1, Tag("platform", platform));
    public static void SlotRenewed(string platform) => Renewed.Add(1, Tag("platform", platform));
    public static void SlotRecovered(string platform) => Recovered.Add(1, Tag("platform", platform));
}
