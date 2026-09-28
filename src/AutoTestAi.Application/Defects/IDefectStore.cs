using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Defects;

/// <summary>
/// Persistence seam for defects (Slice 6 §23). Implemented in Infrastructure
/// with EF Core; throws when no database is configured (fail closed).
/// Methods do NOT authorize — the service enforces that.
/// </summary>
public interface IDefectStore
{
    Task<int> CountAsync(
        Guid projectId, string? status, string? severity,
        string? classification, Guid? testCaseId, string? search,
        CancellationToken ct);

    Task<IReadOnlyList<Defect>> ListAsync(
        Guid projectId, string? status, string? severity,
        string? classification, Guid? testCaseId, string? search,
        int skip, int take, CancellationToken ct);

    Task<Defect?> GetByIdAsync(Guid defectId, CancellationToken ct);

    Task AddAsync(Defect defect, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
