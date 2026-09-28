using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Defects;

/// <summary>
/// Explicit human-driven defect lifecycle (Slice 6 §21). Only the statuses in
/// the existing <see cref="DefectStatus"/> enum are used. Same-state transitions
/// are allowed as idempotent no-ops; history is never rewritten.
/// </summary>
public static class DefectTransitions
{
    private static readonly IReadOnlyDictionary<DefectStatus, IReadOnlySet<DefectStatus>> Allowed =
        new Dictionary<DefectStatus, IReadOnlySet<DefectStatus>>
        {
            [DefectStatus.Open] = new HashSet<DefectStatus>
                { DefectStatus.Open, DefectStatus.InProgress, DefectStatus.Resolved, DefectStatus.Closed, DefectStatus.Rejected },
            [DefectStatus.InProgress] = new HashSet<DefectStatus>
                { DefectStatus.InProgress, DefectStatus.Open, DefectStatus.Resolved, DefectStatus.Closed },
            [DefectStatus.Resolved] = new HashSet<DefectStatus>
                { DefectStatus.Resolved, DefectStatus.Closed, DefectStatus.Open },
            [DefectStatus.Closed] = new HashSet<DefectStatus>
                { DefectStatus.Closed, DefectStatus.Open },
            [DefectStatus.Rejected] = new HashSet<DefectStatus>
                { DefectStatus.Rejected, DefectStatus.Open },
        };

    public static bool IsValidTransition(DefectStatus from, DefectStatus to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(DefectStatus status)
        => status is DefectStatus.Closed or DefectStatus.Rejected;
}
