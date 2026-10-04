using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Mobile;

// ---------- visual baseline protocol (Slice 3C-4D-1) ----------

/// <summary>Baseline reference metadata. Image bytes are never inlined:
/// reviewers fetch them through the presigned download path.</summary>
public sealed record VisualBaselineDto(
    Guid Id,
    Guid ProjectId,
    Guid TestCaseId,
    Guid TestCaseVersionId,
    int StepOrder,
    string Status,
    string StorageKey,
    string Sha256,
    int Width,
    int Height,
    string ContentType,
    int? MismatchThresholdBps,
    Guid? CreatedBy,
    Guid? ApprovedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProposeBaselineCommand(
    Guid ProjectId,
    Guid TestCaseVersionId,
    int StepOrder,
    byte[] PngBytes,
    int Width,
    int Height);

public sealed record ApproveBaselineCommand(
    Guid ProjectId,
    Guid BaselineId);

/// <summary>
/// Persistence seam for visual baselines. Implementations never enforce
/// authorization; the service owns project scoping, RBAC, and audit.
/// </summary>
public interface IVisualBaselineStore
{
    Task<VisualBaseline?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<VisualBaseline?> FindActiveAsync(Guid testCaseVersionId, int stepOrder, CancellationToken ct);
    Task<VisualBaseline?> FindCandidateAsync(Guid testCaseVersionId, int stepOrder, string sha256, CancellationToken ct);
    Task<IReadOnlyList<VisualBaseline>> ListAsync(Guid projectId, Guid? testCaseVersionId, VisualBaselineStatus? status, CancellationToken ct);
    Task AddAsync(VisualBaseline baseline, CancellationToken ct);
    Task DeleteAsync(VisualBaseline baseline, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Project-scoped visual baseline lifecycle (Phase 3 Slice 3C-4D-1).
/// Candidate → Active → Superseded with explicit approval only; rejected
/// candidates are removed; an Active baseline is never deleted. Reads
/// require executions.read; mutations require settings.manage. Comparison
/// logic belongs to 3C-4D-2; execution never consults this service.
/// </summary>
public interface IVisualBaselineService
{
    Task<VisualBaselineDto> ProposeAsync(ProposeBaselineCommand command, CancellationToken ct);
    Task<IReadOnlyList<VisualBaselineDto>> ListAsync(Guid projectId, Guid? testCaseVersionId, string? status, CancellationToken ct);
    Task<VisualBaselineDto> GetAsync(Guid projectId, Guid baselineId, CancellationToken ct);
    Task<VisualBaselineDto> ApproveAsync(ApproveBaselineCommand command, CancellationToken ct);
    Task RejectAsync(Guid projectId, Guid baselineId, CancellationToken ct);
    Task<string> GetDownloadUrlAsync(Guid projectId, Guid baselineId, CancellationToken ct);
}
