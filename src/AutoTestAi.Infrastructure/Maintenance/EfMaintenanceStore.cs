using AutoTestAi.Application.Maintenance;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Maintenance;

/// <summary>EF Core maintenance proposal + scan evidence seam (Phase 4 Slice 4).</summary>
public sealed class EfMaintenanceStore : IMaintenanceStore
{
    private readonly Persistence.AutoTestAiDbContext _db;

    public EfMaintenanceStore(Persistence.AutoTestAiDbContext db) => _db = db;

    public Task AddAsync(MaintenanceProposal proposal, CancellationToken ct)
        => _db.MaintenanceProposals.AddAsync(proposal, ct).AsTask();

    public Task<MaintenanceProposal?> GetByIdAsync(Guid id, CancellationToken ct)
        => _db.MaintenanceProposals.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<MaintenanceProposalRow>> ListAsync(
        Guid projectId, MaintenanceProposalStatus? status, string? signal, string? search,
        int skip, int take, CancellationToken ct)
    {
        var query = BaseQuery(projectId, status, signal, search);
        return await query
            .OrderByDescending(r => r.Confidence)
            .ThenByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
    }

    public Task<int> CountAsync(
        Guid projectId, MaintenanceProposalStatus? status, string? signal, string? search, CancellationToken ct)
        => BaseQuery(projectId, status, signal, search).CountAsync(ct);

    public Task<MaintenanceProposal?> FindOpenAsync(
        Guid testCaseId, Guid versionId, int stepOrder,
        string proposedStrategy, string proposedValue, CancellationToken ct)
        => _db.MaintenanceProposals.FirstOrDefaultAsync(p =>
            p.TestCaseId == testCaseId &&
            p.TestCaseVersionId == versionId &&
            p.StepOrder == stepOrder &&
            p.ProposedStrategy == proposedStrategy &&
            p.ProposedValue == proposedValue &&
            p.Status == MaintenanceProposalStatus.Proposed, ct);

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<HealingEvidenceRow>> ListHealingEvidenceAsync(
        Guid projectId, DateTimeOffset since, int take, CancellationToken ct)
    {
        var query =
            from a in _db.SelfHealingAttempts
            join t in _db.ExecutionTests on a.ExecutionTestId equals t.Id
            join e in _db.Executions on a.ExecutionId equals e.Id
            where a.ProjectId == projectId && a.CreatedAt >= since
            orderby a.CreatedAt descending, a.ExecutionId
            select new HealingEvidenceRow(
                a.Id, a.ProjectId, a.TestCaseId, a.TestCaseVersionId,
                a.StepOrder, a.StepAction,
                a.OriginalStrategy, a.OriginalValue,
                a.RecoveredStrategy, a.RecoveredValue,
                a.IsAiAssisted, a.WasApplied, a.Status,
                a.HealingStrategy,
                a.ExecutionId, e.Status, t.FailureClassification, a.CreatedAt);
        return await query.Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FailedVerdictRow>> ListFailedVerdictsAsync(
        Guid projectId, DateTimeOffset since, int take, CancellationToken ct)
    {
        var query =
            from t in _db.ExecutionTests
            join e in _db.Executions on t.ExecutionId equals e.Id
            where e.ProjectId == projectId && t.CreatedAt >= since &&
                t.Status == ExecutionTestStatus.Failed &&
                (t.FailureClassification == FailureClassification.AutomationFailure ||
                    t.FailureClassification == FailureClassification.TestFailure ||
                    t.FailureClassification == FailureClassification.ApplicationDefect ||
                    t.FailureClassification == FailureClassification.EnvironmentFailure)
            orderby t.CreatedAt descending, t.ExecutionId
            select new FailedVerdictRow(t.TestCaseId, t.TestCaseVersionId, t.ExecutionId, t.FailureClassification);
        return await query.Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListOpenAppBugTestCaseIdsAsync(
        Guid projectId, CancellationToken ct)
    {
        var query =
            from d in _db.Defects
            join t in _db.ExecutionTests on d.ExecutionTestId equals t.Id
            where d.ProjectId == projectId &&
                d.ExecutionTestId != null &&
                (d.Status == DefectStatus.Open || d.Status == DefectStatus.InProgress) &&
                (d.RootCauseType == FailureClassification.ApplicationDefect ||
                    d.RootCauseType == FailureClassification.EnvironmentFailure)
            select t.TestCaseId;
        return await query.Distinct().Take(2000).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<VersionSnapshotRow>> ListLatestVersionsAsync(
        Guid projectId, IReadOnlyList<Guid> testCaseIds, CancellationToken ct)
    {
        if (testCaseIds.Count == 0) return Array.Empty<VersionSnapshotRow>();
        var cases = await _db.TestCases
            .Where(c => c.ProjectId == projectId && testCaseIds.Contains(c.Id))
            .AsNoTracking()
            .Select(c => new { c.Id, c.TestKey, c.Title })
            .ToListAsync(ct);
        var versions = await _db.TestCaseVersions
            .Where(v => testCaseIds.Contains(v.TestCaseId))
            .AsNoTracking()
            .ToListAsync(ct);
        var rows = new List<VersionSnapshotRow>();
        foreach (var testCase in cases)
        {
            var latest = versions
                .Where(v => v.TestCaseId == testCase.Id)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefault();
            if (latest is null) continue;
            rows.Add(new VersionSnapshotRow(
                testCase.Id, latest.Id, latest.VersionNumber,
                latest.ReviewStatus.ToString(),
                latest.StructuredSteps is null ? null : latest.StructuredSteps.RootElement.GetRawText(),
                testCase.TestKey, testCase.Title));
        }
        return rows;
    }

    private IQueryable<MaintenanceProposalRow> BaseQuery(
        Guid projectId, MaintenanceProposalStatus? status, string? signal, string? search)
    {
        var query =
            from p in _db.MaintenanceProposals.AsNoTracking()
            join c in _db.TestCases.AsNoTracking() on p.TestCaseId equals c.Id
            join v in _db.TestCaseVersions.AsNoTracking() on p.TestCaseVersionId equals v.Id
            where p.ProjectId == projectId
            select new MaintenanceProposalRow(
                p.Id, p.ProjectId, p.TestCaseId, c.TestKey, c.Title,
                p.TestCaseVersionId, v.VersionNumber,
                p.StepOrder, p.StepAction,
                p.OriginalStrategy, p.OriginalValue,
                p.ProposedStrategy, p.ProposedValue,
                p.HealingStrategy.ToString(), p.SignalType,
                p.Confidence, p.OccurrenceCount, p.Status.ToString(),
                p.ReviewedBy, p.ReviewedAt, p.RejectionReason,
                p.CreatedVersionId, p.CreatedAt, p.UpdatedAt);
        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value.ToString());
        if (!string.IsNullOrWhiteSpace(signal))
            query = query.Where(r => r.SignalType == signal.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(r =>
                r.TestKey.ToLower().Contains(term) || r.TestTitle.ToLower().Contains(term));
        }
        return query;
    }
}
