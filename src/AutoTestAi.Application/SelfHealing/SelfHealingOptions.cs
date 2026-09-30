namespace AutoTestAi.Application.SelfHealing;

/// <summary>Server-side bounds for self-healing (Phase 2 Slice 11).</summary>
public sealed class SelfHealingOptions
{
    public const string SectionName = "SelfHealing";

    /// <summary>AI suggestion round trip budget. A timeout is a controlled healing failure.</summary>
    public TimeSpan AiTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum evidence characters accepted from a worker per suggestion.</summary>
    public int MaxEvidenceChars { get; set; } = 4000;

    /// <summary>Maximum candidates returned per suggestion.</summary>
    public int MaxCandidates { get; set; } = 8;
}
