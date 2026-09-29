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
}
