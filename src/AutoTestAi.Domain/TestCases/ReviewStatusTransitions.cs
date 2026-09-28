using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.TestCases;

/// <summary>
/// Allowed human-review transitions (docs/04: generated code is never trusted
/// implicitly). Same-state transitions are allowed as idempotent no-ops.
/// </summary>
public static class ReviewStatusTransitions
{
    private static readonly IReadOnlyDictionary<ReviewStatus, IReadOnlySet<ReviewStatus>> Allowed =
        new Dictionary<ReviewStatus, IReadOnlySet<ReviewStatus>>
        {
            [ReviewStatus.Pending] = new HashSet<ReviewStatus>
                { ReviewStatus.Pending, ReviewStatus.Approved, ReviewStatus.ChangesRequested, ReviewStatus.Rejected },
            [ReviewStatus.ChangesRequested] = new HashSet<ReviewStatus>
                { ReviewStatus.ChangesRequested, ReviewStatus.Pending, ReviewStatus.Approved, ReviewStatus.Rejected },
            [ReviewStatus.Approved] = new HashSet<ReviewStatus>
                { ReviewStatus.Approved, ReviewStatus.ChangesRequested, ReviewStatus.Rejected },
            [ReviewStatus.Rejected] = new HashSet<ReviewStatus>
                { ReviewStatus.Rejected, ReviewStatus.Pending, ReviewStatus.ChangesRequested },
        };

    /// <summary>
    /// Approved versions may be sent back for changes or rejected, but never
    /// silently back to Pending; rejected versions must be reworked via
    /// Pending/ChangesRequested, never approved directly.
    /// </summary>
    public static bool IsValidTransition(ReviewStatus from, ReviewStatus to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}
