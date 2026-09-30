namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// SignalR event names for /hubs/execution (docs/06 §12).
/// Shared between backend and frontend so the contract cannot drift silently.
/// </summary>
public static class ExecutionEvents
{
    public const string ExecutionStarted = "ExecutionStarted";
    public const string ExecutionStatusChanged = "ExecutionStatusChanged";
    public const string ExecutionTestStarted = "ExecutionTestStarted";
    public const string ExecutionStepStarted = "ExecutionStepStarted";
    public const string ExecutionStepCompleted = "ExecutionStepCompleted";
    public const string ExecutionLogReceived = "ExecutionLogReceived";
    public const string ExecutionTestCompleted = "ExecutionTestCompleted";
    public const string FailureAnalysisCompleted = "FailureAnalysisCompleted";
    public const string ExecutionCompleted = "ExecutionCompleted";
    public const string ExecutionFailed = "ExecutionFailed";
    /// <summary>Phase 2 Slice 9: execution is waiting for grid capacity.</summary>
    public const string ExecutionQueued = "ExecutionQueued";
    /// <summary>Phase 2 Slice 9: a grid lease was claimed on a worker.</summary>
    public const string ExecutionAssigned = "ExecutionAssigned";
    /// <summary>Phase 2 Slice 9: worker lifecycle/health changed.</summary>
    public const string WorkerStatusChanged = "WorkerStatusChanged";
    /// <summary>
    /// Phase 2 Slice 11: a step recovered via self-healing. Supplementary to
    /// ExecutionLogReceived (which carries the granular self-healing.* log
    /// stream); the stored test definition is unchanged.
    /// </summary>
    public const string SelfHealingApplied = "SelfHealingApplied";
    /// <summary>Phase 2 Slice 11: healing was attempted but could not recover safely.</summary>
    public const string SelfHealingFailed = "SelfHealingFailed";
}
