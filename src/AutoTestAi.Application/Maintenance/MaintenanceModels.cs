using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Maintenance;

/// <summary>
/// Filters for maintenance proposal listing (Phase 4 Slice 4).
/// All filtering happens server-side; queries are always project-scoped.
/// </summary>
public sealed record MaintenanceProposalFilters(
    string? Status,
    string? Signal,
    string? Search);

/// <summary>One persisted maintenance proposal for list display.</summary>
public sealed record MaintenanceProposalDto(
    Guid Id,
    Guid ProjectId,
    Guid TestCaseId,
    string TestKey,
    string TestTitle,
    Guid TestCaseVersionId,
    int TestCaseVersionNumber,
    int StepOrder,
    string StepAction,
    string? OriginalStrategy,
    string? OriginalValue,
    string? ProposedStrategy,
    string? ProposedValue,
    string HealingStrategy,
    string SignalType,
    int Confidence,
    int OccurrenceCount,
    string Status,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? RejectionReason,
    Guid? CreatedVersionId,
    int? CreatedVersionNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Evidence bundle for proposal review (re-derived, bounded).</summary>
public sealed record MaintenanceEvidenceDto(
    int OccurrenceCount,
    int FailedCorroborationCount,
    double HealingSuccessRatio,
    string? ForecastBand,
    IReadOnlyList<string> ConfidenceFactors,
    IReadOnlyList<Guid> ExecutionIds,
    IReadOnlyList<Guid> HealingAttemptIds,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>Proposal detail: metadata plus evidence for human review.</summary>
public sealed record MaintenanceProposalDetailDto(
    MaintenanceProposalDto Proposal,
    MaintenanceEvidenceDto Evidence);

/// <summary>Bounded scan outcome (Phase 4 Slice 4).</summary>
public sealed record MaintenanceScanResult(
    int CandidatesDetected,
    int ProposalsCreated,
    int ProposalsAlreadyExisting,
    int ProposalsSkipped,
    DateTimeOffset ScannedAt);

/// <summary>Approve outcome: the proposal plus the created Pending version.</summary>
public sealed record MaintenanceApproveResult(
    Guid ProposalId,
    string Status,
    Guid CreatedVersionId,
    int CreatedVersionNumber);

/// <summary>Proposal row joined with test/version display fields.</summary>
public sealed record MaintenanceProposalRow(
    Guid Id,
    Guid ProjectId,
    Guid TestCaseId,
    string TestKey,
    string TestTitle,
    Guid TestCaseVersionId,
    int TestCaseVersionNumber,
    int StepOrder,
    string StepAction,
    string? OriginalStrategy,
    string? OriginalValue,
    string? ProposedStrategy,
    string? ProposedValue,
    string HealingStrategy,
    string SignalType,
    int Confidence,
    int OccurrenceCount,
    string Status,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? RejectionReason,
    Guid? CreatedVersionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Healing evidence row joined with its execution verdict.</summary>
public sealed record HealingEvidenceRow(
    Guid AttemptId,
    Guid ProjectId,
    Guid TestCaseId,
    Guid? TestCaseVersionId,
    int StepOrder,
    string StepAction,
    string? OriginalStrategy,
    string? OriginalValue,
    string? RecoveredStrategy,
    string? RecoveredValue,
    bool IsAiAssisted,
    bool WasApplied,
    Domain.Enums.SelfHealingStatus Status,
    Domain.Enums.SelfHealingStrategy HealingStrategy,
    Guid ExecutionId,
    Domain.Enums.ExecutionStatus ExecutionStatus,
    Domain.Enums.FailureClassification Classification,
    DateTimeOffset CreatedAt);

/// <summary>Failed terminal verdict for corroboration/exclusion.</summary>
public sealed record FailedVerdictRow(
    Guid TestCaseId,
    Guid? TestCaseVersionId,
    Guid ExecutionId,
    Domain.Enums.FailureClassification Classification);

/// <summary>Latest version snapshot for stale-version checks.</summary>
public sealed record VersionSnapshotRow(
    Guid TestCaseId,
    Guid VersionId,
    int VersionNumber,
    string ReviewStatus,
    string? StepsJson,
    string TestKey,
    string Title);

/// <summary>
/// Persistence seam for maintenance proposals and scan evidence.
/// Implementations never enforce authorization; the service owns project
/// scoping, RBAC, and audit.
/// </summary>
public interface IMaintenanceStore
{
    Task AddAsync(MaintenanceProposal proposal, CancellationToken ct);
    Task<MaintenanceProposal?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MaintenanceProposalRow>> ListAsync(
        Guid projectId, Domain.Enums.MaintenanceProposalStatus? status, string? signal, string? search,
        int skip, int take, CancellationToken ct);
    Task<int> CountAsync(
        Guid projectId, Domain.Enums.MaintenanceProposalStatus? status, string? signal, string? search, CancellationToken ct);
    Task<MaintenanceProposal?> FindOpenAsync(
        Guid testCaseId, Guid versionId, int stepOrder,
        string proposedStrategy, string proposedValue, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task<IReadOnlyList<HealingEvidenceRow>> ListHealingEvidenceAsync(
        Guid projectId, DateTimeOffset since, int take, CancellationToken ct);
    Task<IReadOnlyList<FailedVerdictRow>> ListFailedVerdictsAsync(
        Guid projectId, DateTimeOffset since, int take, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListOpenAppBugTestCaseIdsAsync(
        Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<VersionSnapshotRow>> ListLatestVersionsAsync(
        Guid projectId, IReadOnlyList<Guid> testCaseIds, CancellationToken ct);
}

/// <summary>
/// Human-gated test-maintenance lifecycle (Phase 4 Slice 4, web locators
/// only). Scan aggregates persisted evidence into Proposed rows; approval
/// mints a new Pending version through the existing version path; the
/// existing TestCase review remains the final execution gate. Reads require
/// testcases.read; scan/approve/reject require testcases.manage.
/// </summary>
public interface IMaintenanceService
{
    Task<MaintenanceScanResult> ScanAsync(Guid projectId, CancellationToken ct);
    Task<PagedResult<MaintenanceProposalDto>> ListAsync(
        Guid projectId, MaintenanceProposalFilters filters,
        int page, int pageSize, CancellationToken ct);
    Task<MaintenanceProposalDetailDto> GetAsync(
        Guid projectId, Guid proposalId, CancellationToken ct);
    Task<MaintenanceApproveResult> ApproveAsync(
        Guid projectId, Guid proposalId, CancellationToken ct);
    Task<MaintenanceProposalDto> RejectAsync(
        Guid projectId, Guid proposalId, string? reason, CancellationToken ct);
}
