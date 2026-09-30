using AutoTestAi.Application.TestExecution;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Guards the SignalR event contract shared with the React frontend
/// (frontend lib/realtime must use the same names).
/// </summary>
public sealed class ExecutionEventsTests
{
    [Theory]
    [InlineData("ExecutionStarted", "ExecutionStarted")]
    [InlineData("ExecutionStatusChanged", "ExecutionStatusChanged")]
    [InlineData("ExecutionTestStarted", "ExecutionTestStarted")]
    [InlineData("ExecutionStepStarted", "ExecutionStepStarted")]
    [InlineData("ExecutionStepCompleted", "ExecutionStepCompleted")]
    [InlineData("ExecutionLogReceived", "ExecutionLogReceived")]
    [InlineData("ExecutionTestCompleted", "ExecutionTestCompleted")]
    [InlineData("FailureAnalysisCompleted", "FailureAnalysisCompleted")]
    [InlineData("ExecutionCompleted", "ExecutionCompleted")]
    [InlineData("ExecutionFailed", "ExecutionFailed")]
    [InlineData("SelfHealingApplied", "SelfHealingApplied")]
    [InlineData("SelfHealingFailed", "SelfHealingFailed")]
    public void EventNames_MatchApiContract(string expected, string actualField)
    {
        var value = typeof(ExecutionEvents)
            .GetField(actualField)?.GetValue(null) as string;
        Assert.Equal(expected, value);
    }
}
