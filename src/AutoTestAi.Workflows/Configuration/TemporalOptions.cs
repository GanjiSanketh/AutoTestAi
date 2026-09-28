namespace AutoTestAi.Workflows.Configuration;

/// <summary>Temporal connection settings (docs/04 §7, ADR-004). Environment-driven.</summary>
public sealed class TemporalOptions
{
    public const string SectionName = "Temporal";
    public string Address { get; set; } = "localhost:7233";
    public string Namespace { get; set; } = "default";
    public string TaskQueue { get; set; } = "autotestai-execution";
    public bool Enabled { get; set; } = true;
    public bool Configured => Enabled && !string.IsNullOrWhiteSpace(Address);
}
