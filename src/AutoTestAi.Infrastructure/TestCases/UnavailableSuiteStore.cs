using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Infrastructure.TestCases;

/// <summary>
/// Fail-closed store used when no database is configured: every operation
/// surfaces as a dependency failure (503) rather than inventing data.
/// </summary>
public sealed class UnavailableSuiteStore : ISuiteStore
{
    private static Task<T> Unavailable<T>() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    private static Task Unavailable() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    public Task<int> CountAsync(Guid projectId, string? search, string? status, CancellationToken ct)
        => Unavailable<int>();
    public Task<IReadOnlyList<SuiteListItemDto>> ListAsync(
        Guid projectId, string? search, string? status, int skip, int take, CancellationToken ct)
        => Unavailable<IReadOnlyList<SuiteListItemDto>>();
    public Task<TestSuite?> GetByIdRawAsync(Guid suiteId, CancellationToken ct) => Unavailable<TestSuite?>();
    public Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeSuiteId, CancellationToken ct)
        => Unavailable<bool>();
    public Task AddAsync(TestSuite suite, CancellationToken ct) => Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => Unavailable();
    public Task<SuiteDetailDto?> GetByIdWithMembersAsync(Guid suiteId, CancellationToken ct)
        => Unavailable<SuiteDetailDto?>();
    public Task<IReadOnlyList<SuiteMemberDto>> GetMembersAsync(Guid suiteId, CancellationToken ct)
        => Unavailable<IReadOnlyList<SuiteMemberDto>>();
    public Task AddMemberAsync(Guid suiteId, Guid testCaseId, int executionOrder, CancellationToken ct)
        => Unavailable();
    public Task<bool> RemoveMemberAsync(Guid suiteId, Guid testCaseId, CancellationToken ct)
        => Unavailable<bool>();
    public Task UpdateMemberOrdersAsync(Guid suiteId, IReadOnlyDictionary<Guid, int> orders, CancellationToken ct)
        => Unavailable();
    public Task<int> GetExecutionCountAsync(Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, CancellationToken ct)
        => Unavailable<int>();
    public Task<IReadOnlyList<SuiteExecutionSummaryDto>> GetExecutionHistoryAsync(
        Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, int skip, int take, CancellationToken ct)
        => Unavailable<IReadOnlyList<SuiteExecutionSummaryDto>>();
    public Task<SuiteReportDto?> GetReportDataAsync(Guid suiteId, DateTimeOffset? from, DateTimeOffset? to, TriggerType? trigger, CancellationToken ct)
        => Unavailable<SuiteReportDto?>();
    public Task<IReadOnlyList<TriggerBreakdownItem>> GetTriggerBreakdownAsync(Guid suiteId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
        => Unavailable<IReadOnlyList<TriggerBreakdownItem>>();
    public Task<SuiteTrendData> GetTrendDataAsync(Guid suiteId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => Unavailable<SuiteTrendData>();
    public Task<Execution?> GetExecutionByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken ct)
        => Unavailable<Execution?>();
}
