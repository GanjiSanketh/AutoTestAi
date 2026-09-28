namespace AutoTestAi.Application.TestExecution;

/// <summary>Strongly typed execution control-plane settings (Slice 5 §59).</summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    public int DefaultExecutionTimeoutSeconds { get; set; } = 300;
    public int MaxExecutionTimeoutSeconds { get; set; } = 1800;
    public int DefaultStepTimeoutSeconds { get; set; } = 30;

    /// <summary>Worker polling cadence used while tracking a running assignment.</summary>
    public int WorkerPollIntervalSeconds { get; set; } = 2;

    /// <summary>Maximum log rows returned per logs request (bounded reads).</summary>
    public int MaxLogPageSize { get; set; } = 500;

    /// <summary>Presigned artifact download URL lifetime.</summary>
    public int ArtifactDownloadExpirySeconds { get; set; } = 900;

    public TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(Math.Clamp(DefaultExecutionTimeoutSeconds, 30, MaxExecutionTimeoutSeconds));
    public TimeSpan StepTimeout => TimeSpan.FromSeconds(Math.Clamp(DefaultStepTimeoutSeconds, 5, 300));
}

/// <summary>Server-side Playwright worker connectivity (Slice 5 §59).
/// The API token is server-side only — never exposed to browsers.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public string BaseUrl { get; set; } = "http://localhost:8090";
    public string? ApiToken { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 30;

    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl);
}
