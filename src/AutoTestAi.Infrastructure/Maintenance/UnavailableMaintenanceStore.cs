using AutoTestAi.Application.Maintenance;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Infrastructure.Maintenance;

/// <summary>Fail-closed maintenance seam when no database is configured.</summary>
public sealed class UnavailableMaintenanceStore : IMaintenanceStore
{
    private static Exception Unavailable() => new InvalidOperationException("Maintenance storage is not configured.");
    public Task AddAsync(MaintenanceProposal proposal, CancellationToken ct) => throw Unavailable();
    public Task<MaintenanceProposal?> GetByIdAsync(Guid id, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<MaintenanceProposalRow>> ListAsync(Guid projectId, MaintenanceProposalStatus? status, string? signal, string? search, int skip, int take, CancellationToken ct) => throw Unavailable();
    public Task<int> CountAsync(Guid projectId, MaintenanceProposalStatus? status, string? signal, string? search, CancellationToken ct) => throw Unavailable();
    public Task<MaintenanceProposal?> FindOpenAsync(Guid testCaseId, Guid versionId, int stepOrder, string proposedStrategy, string proposedValue, CancellationToken ct) => throw Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<HealingEvidenceRow>> ListHealingEvidenceAsync(Guid projectId, DateTimeOffset since, int take, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<FailedVerdictRow>> ListFailedVerdictsAsync(Guid projectId, DateTimeOffset since, int take, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<Guid>> ListOpenAppBugTestCaseIdsAsync(Guid projectId, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<VersionSnapshotRow>> ListLatestVersionsAsync(Guid projectId, IReadOnlyList<Guid> testCaseIds, CancellationToken ct) => throw Unavailable();
}
