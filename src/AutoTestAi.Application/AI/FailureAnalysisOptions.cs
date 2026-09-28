namespace AutoTestAi.Application.AI;

/// <summary>Bounded evidence + provider limits for failure analysis (Slice 6 §9/§64).</summary>
public sealed class FailureAnalysisOptions
{
    public const string SectionName = "AI:FailureAnalysis";

    public int MaxLogLines { get; set; } = 50;
    public int MaxLogCharsPerMessage { get; set; } = 1000;
    public int MaxTotalLogChars { get; set; } = 12000;
    public int MaxErrorChars { get; set; } = 2000;
    public int MaxFailedSteps { get; set; } = 10;
    public int MaxArtifactRefs { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxAnalysesPerMinutePerProject { get; set; } = 10;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 10, 600));
}
