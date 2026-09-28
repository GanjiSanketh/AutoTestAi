using AutoTestAi.Application.Defects;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.Defects;

/// <summary>
/// Fail-closed store used when no database is configured: every operation
/// surfaces as a dependency failure (503) rather than inventing data.
/// </summary>
public sealed class UnavailableDefectStore : IDefectStore
{
    private static Task<T> Unavailable<T>() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    private static Task Unavailable() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    public Task<int> CountAsync(
        Guid projectId, string? status, string? severity,
        string? classification, Guid? testCaseId, string? search,
        CancellationToken ct)
        => Unavailable<int>();
    public Task<IReadOnlyList<Defect>> ListAsync(
        Guid projectId, string? status, string? severity,
        string? classification, Guid? testCaseId, string? search,
        int skip, int take, CancellationToken ct)
        => Unavailable<IReadOnlyList<Defect>>();
    public Task<Defect?> GetByIdAsync(Guid defectId, CancellationToken ct)
        => Unavailable<Defect?>();
    public Task AddAsync(Defect defect, CancellationToken ct) => Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => Unavailable();
}
