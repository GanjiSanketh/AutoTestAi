using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Defects;

/// <summary>EF Core implementation of the defect seam. No authorization here.</summary>
public sealed class EfDefectStore : IDefectStore
{
    private readonly AutoTestAiDbContext _db;

    public EfDefectStore(AutoTestAiDbContext db) => _db = db;

    public Task<int> CountAsync(
        Guid projectId, string? status, string? severity,
        string? classification, Guid? testCaseId, string? search,
        CancellationToken ct)
        => ApplyFilters(projectId, status, severity, classification, testCaseId, search).CountAsync(ct);

    public async Task<IReadOnlyList<Defect>> ListAsync(
        Guid projectId, string? status, string? severity,
        string? classification, Guid? testCaseId, string? search,
        int skip, int take, CancellationToken ct)
        => await ApplyFilters(projectId, status, severity, classification, testCaseId, search)
            .OrderByDescending(d => d.CreatedAt)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<Defect?> GetByIdAsync(Guid defectId, CancellationToken ct)
        => _db.Defects.FirstOrDefaultAsync(d => d.Id == defectId, ct);

    public Task AddAsync(Defect defect, CancellationToken ct)
        => _db.Defects.AddAsync(defect, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    private IQueryable<Defect> ApplyFilters(
        Guid projectId, string? status, string? severity,
        string? classification, Guid? testCaseId, string? search)
    {
        IQueryable<Defect> query = _db.Defects.Where(d => d.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(d => d.Status.ToString() == status);
        if (!string.IsNullOrWhiteSpace(severity))
            query = query.Where(d => d.Severity.ToString() == severity);
        if (!string.IsNullOrWhiteSpace(classification))
            query = query.Where(d => d.RootCauseType.ToString() == classification);
        if (testCaseId.HasValue)
        {
            var id = testCaseId.Value;
            query = query.Where(d => d.ExecutionTestId.HasValue && _db.ExecutionTests
                .Any(t => t.Id == d.ExecutionTestId.Value && t.TestCaseId == id));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(d =>
                d.Title.ToLower().Contains(term) ||
                (d.Description != null && d.Description.ToLower().Contains(term)));
        }
        return query;
    }
}
