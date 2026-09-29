namespace AutoTestAi.Domain.Enums;

/// <summary>
/// Domain state enums. Terminology follows docs/05-Database-Design.md
/// and status language from docs/02-Design-System.md.
/// </summary>
public enum ProjectStatus
{
    Active = 0,
    Archived = 1
}

public enum TestCaseStatus
{
    Draft = 0,
    Active = 1,
    Deprecated = 2,
    Archived = 3
}

/// <summary>Human review state for an AI-generated test version (FR-3.2).</summary>
public enum ReviewStatus
{
    Pending = 0,
    Approved = 1,
    ChangesRequested = 2,
    Rejected = 3
}

public enum ExecutionStatus
{
    Queued = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
    Cancelled = 4,
    Error = 5,
    TimedOut = 6
}

public enum ExecutionTestStatus
{
    Queued = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
    Skipped = 4,
    Error = 5,
    TimedOut = 6,
    Cancelled = 7
}

public enum DefectStatus
{
    Open = 0,
    InProgress = 1,
    Resolved = 2,
    Closed = 3,
    Rejected = 4
}

public enum Severity
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3
}

public enum TicketSyncStatus
{
    Pending = 0,
    Synced = 1,
    Failed = 2
}

public enum Priority
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3
}

public enum TriggerType
{
    Manual = 0,
    Schedule = 1,
    Ci = 2
}

/// <summary>
/// Failure classification for the bug triage view (docs/02 §12).
/// Distinguishes test failure vs application defect vs environment vs automation failure.
/// </summary>
public enum FailureClassification
{
    Unknown = 0,
    ApplicationDefect = 1,
    EnvironmentFailure = 2,
    AutomationFailure = 3,
    TestFailure = 4
}

/// <summary>
/// AI failure-analysis attempt lifecycle (Slice 6). Absence of rows means
/// NotAnalyzed. Attempts are immutable; retries create new attempt rows.
/// </summary>
public enum AnalysisStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

public enum IntegrationStatus
{
    Active = 0,
    Disabled = 1,
    Error = 2
}

/// <summary>
/// Administrative worker lifecycle (Phase 2 Slice 9). Liveness states
/// (Unhealthy/Offline) are derived from heartbeat freshness at read time;
/// the stored value tracks the admin lifecycle only.
/// </summary>
public enum GridWorkerStatus
{
    Registered = 0,
    Available = 1,
    Busy = 2,
    Draining = 3,
    Unhealthy = 4,
    Offline = 5,
    Disabled = 6
}

/// <summary>
/// Assignment lease lifecycle (Phase 2 Slice 9). Terminal states are final;
/// a lease row is never reused for a new claim.
/// </summary>
public enum GridAssignmentStatus
{
    Pending = 0,
    Claimed = 1,
    Running = 2,
    Completed = 3,
    Released = 4,
    Expired = 5,
    Cancelled = 6
}
