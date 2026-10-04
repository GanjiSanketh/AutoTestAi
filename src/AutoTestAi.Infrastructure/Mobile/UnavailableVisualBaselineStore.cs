using AutoTestAi.Application.Mobile;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Infrastructure.Mobile;

/// <summary>Fail-closed visual baseline seam when no database is configured.</summary>
public sealed class UnavailableVisualBaselineStore : IVisualBaselineStore
{
    private static Exception Unavailable() => new InvalidOperationException("Visual baseline storage is not configured.");
    public Task<VisualBaseline?> GetByIdAsync(Guid id, CancellationToken ct) => throw Unavailable();
    public Task<VisualBaseline?> FindActiveAsync(Guid testCaseVersionId, int stepOrder, CancellationToken ct) => throw Unavailable();
    public Task<VisualBaseline?> FindCandidateAsync(Guid testCaseVersionId, int stepOrder, string sha256, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<VisualBaseline>> ListAsync(Guid projectId, Guid? testCaseVersionId, VisualBaselineStatus? status, CancellationToken ct) => throw Unavailable();
    public Task AddAsync(VisualBaseline baseline, CancellationToken ct) => throw Unavailable();
    public Task DeleteAsync(VisualBaseline baseline, CancellationToken ct) => throw Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => throw Unavailable();
}
