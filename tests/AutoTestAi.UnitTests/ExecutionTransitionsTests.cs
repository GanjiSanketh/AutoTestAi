using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.Executions;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 5 §3/§46: execution lifecycle is a one-way state machine;
/// history is immutable, reruns are new records.</summary>
public sealed class ExecutionTransitionsTests
{
    [Theory]
    [InlineData(ExecutionStatus.Queued, ExecutionStatus.Running, true)]
    [InlineData(ExecutionStatus.Queued, ExecutionStatus.Cancelled, true)]
    [InlineData(ExecutionStatus.Queued, ExecutionStatus.Passed, false)]
    [InlineData(ExecutionStatus.Running, ExecutionStatus.Passed, true)]
    [InlineData(ExecutionStatus.Running, ExecutionStatus.Failed, true)]
    [InlineData(ExecutionStatus.Running, ExecutionStatus.Cancelled, true)]
    [InlineData(ExecutionStatus.Running, ExecutionStatus.TimedOut, true)]
    [InlineData(ExecutionStatus.Running, ExecutionStatus.Queued, false)]
    [InlineData(ExecutionStatus.Passed, ExecutionStatus.Running, false)]
    [InlineData(ExecutionStatus.Failed, ExecutionStatus.Running, false)]
    [InlineData(ExecutionStatus.Cancelled, ExecutionStatus.Running, false)]
    [InlineData(ExecutionStatus.TimedOut, ExecutionStatus.Running, false)]
    [InlineData(ExecutionStatus.Passed, ExecutionStatus.Passed, true)] // idempotent no-op
    [InlineData(ExecutionStatus.Failed, ExecutionStatus.Cancelled, false)]
    public void Execution_Transitions(ExecutionStatus from, ExecutionStatus to, bool allowed)
        => Assert.Equal(allowed, ExecutionTransitions.IsValidTransition(from, to));

    [Theory]
    [InlineData(ExecutionTestStatus.Queued, ExecutionTestStatus.Running, true)]
    [InlineData(ExecutionTestStatus.Queued, ExecutionTestStatus.Cancelled, true)]
    [InlineData(ExecutionTestStatus.Running, ExecutionTestStatus.TimedOut, true)]
    [InlineData(ExecutionTestStatus.Running, ExecutionTestStatus.Queued, false)]
    [InlineData(ExecutionTestStatus.Passed, ExecutionTestStatus.Failed, false)]
    [InlineData(ExecutionTestStatus.Cancelled, ExecutionTestStatus.Running, false)]
    public void ExecutionTest_Transitions(ExecutionTestStatus from, ExecutionTestStatus to, bool allowed)
        => Assert.Equal(allowed, ExecutionTransitions.IsValidTestTransition(from, to));

    [Theory]
    [InlineData(ExecutionStatus.Queued, false)]
    [InlineData(ExecutionStatus.Running, false)]
    [InlineData(ExecutionStatus.Passed, true)]
    [InlineData(ExecutionStatus.Failed, true)]
    [InlineData(ExecutionStatus.Cancelled, true)]
    [InlineData(ExecutionStatus.TimedOut, true)]
    [InlineData(ExecutionStatus.Error, true)]
    public void Execution_Terminal(ExecutionStatus status, bool terminal)
        => Assert.Equal(terminal, ExecutionTransitions.IsTerminal(status));

    [Theory]
    [InlineData(ExecutionTestStatus.Queued, false)]
    [InlineData(ExecutionTestStatus.Running, false)]
    [InlineData(ExecutionTestStatus.Passed, true)]
    [InlineData(ExecutionTestStatus.Skipped, true)]
    [InlineData(ExecutionTestStatus.Cancelled, true)]
    public void ExecutionTest_Terminal(ExecutionTestStatus status, bool terminal)
        => Assert.Equal(terminal, ExecutionTransitions.IsTestTerminal(status));
}
