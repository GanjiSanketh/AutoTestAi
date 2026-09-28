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
    public const string ExecutionLogReceived = "ExecutionLogReceived";
    public const string ExecutionTestCompleted = "ExecutionTestCompleted";
    public const string FailureAnalysisCompleted = "FailureAnalysisCompleted";
    public const string ExecutionCompleted = "ExecutionCompleted";
    public const string ExecutionFailed = "ExecutionFailed";
}
