using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Suite store abstraction (Phase 4 Slice 9A).
/// Methods do NOT authorize — <see cref="ISuiteService"/> enforces that.
/// </summary>
public interface ISuiteStore
{
    Task<int> CountAsync(Guid projectId, string? search, string? status, CancellationToken ct);

    Task<IReadOnlyList<SuiteListItemDto>> ListAsync(Guid projectId, string? search, string? status, int skip, int take, CancellationToken ct);

    Task<TestSuite?> GetByIdRawAsync(Guid suiteId, CancellationToken ct);

    /// <summary>Exact (case-insensitive) name match within the project, optionally excluding one suite.</summary>
    Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeSuiteId, CancellationToken ct);

    Task AddAsync(TestSuite suite, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    Task<SuiteDetailDto?> GetByIdWithMembersAsync(Guid suiteId, CancellationToken ct);

    Task<IReadOnlyList<SuiteMemberDto>> GetMembersAsync(Guid suiteId, CancellationToken ct);

    Task AddMemberAsync(Guid suiteId, Guid testCaseId, int executionOrder, CancellationToken ct);

    /// <summary>Removes one membership row. Returns true when a row was removed.</summary>
    Task<bool> RemoveMemberAsync(Guid suiteId, Guid testCaseId, CancellationToken ct);

    /// <summary>
    /// Applies execution orders for existing membership rows only.
    /// Never adds or removes rows — the caller validates the member set.
    /// </summary>
    Task UpdateMemberOrdersAsync(Guid suiteId, IReadOnlyDictionary<Guid, int> orders, CancellationToken ct);

    Task<int> GetExecutionCountAsync(Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, CancellationToken ct);

    Task<IReadOnlyList<SuiteExecutionSummaryDto>> GetExecutionHistoryAsync(
        Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, int skip, int take, CancellationToken ct);

    Task<SuiteReportDto?> GetReportDataAsync(Guid suiteId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    Task<Execution?> GetExecutionByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken ct);
}
