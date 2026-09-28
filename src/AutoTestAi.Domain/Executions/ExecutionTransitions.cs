using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Executions;

/// <summary>
/// Legal execution state transitions (Slice 5 §3). Terminal states are final:
/// a rerun creates a NEW execution instead of mutating history. Same-state
/// transitions are allowed as idempotent no-ops (safe retries/reconciliation).
/// </summary>
public static class ExecutionTransitions
{
    private static readonly IReadOnlyDictionary<ExecutionStatus, IReadOnlySet<ExecutionStatus>> Allowed =
        new Dictionary<ExecutionStatus, IReadOnlySet<ExecutionStatus>>
        {
            [ExecutionStatus.Queued] = new HashSet<ExecutionStatus>
                { ExecutionStatus.Queued, ExecutionStatus.Running, ExecutionStatus.Cancelled },
            [ExecutionStatus.Running] = new HashSet<ExecutionStatus>
                { ExecutionStatus.Running, ExecutionStatus.Passed, ExecutionStatus.Failed,
                  ExecutionStatus.Cancelled, ExecutionStatus.TimedOut, ExecutionStatus.Error },
            [ExecutionStatus.Passed] = new HashSet<ExecutionStatus> { ExecutionStatus.Passed },
            [ExecutionStatus.Failed] = new HashSet<ExecutionStatus> { ExecutionStatus.Failed },
            [ExecutionStatus.Cancelled] = new HashSet<ExecutionStatus> { ExecutionStatus.Cancelled },
            [ExecutionStatus.TimedOut] = new HashSet<ExecutionStatus> { ExecutionStatus.TimedOut },
            [ExecutionStatus.Error] = new HashSet<ExecutionStatus> { ExecutionStatus.Error },
        };

    private static readonly IReadOnlyDictionary<ExecutionTestStatus, IReadOnlySet<ExecutionTestStatus>> AllowedTests =
        new Dictionary<ExecutionTestStatus, IReadOnlySet<ExecutionTestStatus>>
        {
            [ExecutionTestStatus.Queued] = new HashSet<ExecutionTestStatus>
                { ExecutionTestStatus.Queued, ExecutionTestStatus.Running,
                  ExecutionTestStatus.Cancelled, ExecutionTestStatus.Skipped },
            [ExecutionTestStatus.Running] = new HashSet<ExecutionTestStatus>
                { ExecutionTestStatus.Running, ExecutionTestStatus.Passed, ExecutionTestStatus.Failed,
                  ExecutionTestStatus.Cancelled, ExecutionTestStatus.TimedOut, ExecutionTestStatus.Error },
            [ExecutionTestStatus.Passed] = new HashSet<ExecutionTestStatus> { ExecutionTestStatus.Passed },
            [ExecutionTestStatus.Failed] = new HashSet<ExecutionTestStatus> { ExecutionTestStatus.Failed },
            [ExecutionTestStatus.Skipped] = new HashSet<ExecutionTestStatus> { ExecutionTestStatus.Skipped },
            [ExecutionTestStatus.Cancelled] = new HashSet<ExecutionTestStatus> { ExecutionTestStatus.Cancelled },
            [ExecutionTestStatus.TimedOut] = new HashSet<ExecutionTestStatus> { ExecutionTestStatus.TimedOut },
            [ExecutionTestStatus.Error] = new HashSet<ExecutionTestStatus> { ExecutionTestStatus.Error },
        };

    public static bool IsValidTransition(ExecutionStatus from, ExecutionStatus to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsValidTestTransition(ExecutionTestStatus from, ExecutionTestStatus to)
        => AllowedTests.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(ExecutionStatus status)
        => status is ExecutionStatus.Passed or ExecutionStatus.Failed
            or ExecutionStatus.Cancelled or ExecutionStatus.TimedOut or ExecutionStatus.Error;

    public static bool IsTestTerminal(ExecutionTestStatus status)
        => status != ExecutionTestStatus.Queued && status != ExecutionTestStatus.Running;
}
