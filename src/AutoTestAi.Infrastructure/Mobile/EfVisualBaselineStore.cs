using AutoTestAi.Application.Mobile;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Mobile;

/// <summary>EF Core visual baseline seam (Phase 3 Slice 3C-4D-1). No authorization here.</summary>
public sealed class EfVisualBaselineStore : IVisualBaselineStore
{
    private readonly AutoTestAiDbContext _db;

    public EfVisualBaselineStore(AutoTestAiDbContext db) => _db = db;

    public Task<VisualBaseline?> GetByIdAsync(Guid id, CancellationToken ct)
        => _db.VisualBaselines.FirstOrDefaultAsync(b => b.Id == id, ct);

    public Task<VisualBaseline?> FindActiveAsync(Guid testCaseVersionId, int stepOrder, CancellationToken ct)
        => _db.VisualBaselines.FirstOrDefaultAsync(b =>
            b.TestCaseVersionId == testCaseVersionId &&
            b.StepOrder == stepOrder &&
            b.Status == VisualBaselineStatus.Active, ct);

    public Task<VisualBaseline?> FindCandidateAsync(
        Guid testCaseVersionId, int stepOrder, string sha256, CancellationToken ct)
        => _db.VisualBaselines.FirstOrDefaultAsync(b =>
            b.TestCaseVersionId == testCaseVersionId &&
            b.StepOrder == stepOrder &&
            b.Status == VisualBaselineStatus.Candidate &&
            b.Sha256 == sha256, ct);

    public async Task<IReadOnlyList<VisualBaseline>> ListAsync(
        Guid projectId, Guid? testCaseVersionId, VisualBaselineStatus? status, CancellationToken ct)
        => await _db.VisualBaselines.AsNoTracking()
            .Where(b => b.ProjectId == projectId &&
                (testCaseVersionId == null || b.TestCaseVersionId == testCaseVersionId) &&
                (status == null || b.Status == status))
            .OrderBy(b => b.TestCaseVersionId).ThenBy(b => b.StepOrder).ThenBy(b => b.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(VisualBaseline baseline, CancellationToken ct)
        => await _db.VisualBaselines.AddAsync(baseline, ct);

    public Task DeleteAsync(VisualBaseline baseline, CancellationToken ct)
    {
        _db.VisualBaselines.Remove(baseline);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}
