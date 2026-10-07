using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Suite summary for list views.
/// </summary>
public sealed record SuiteListItemDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    string Status,
    int TestCount,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Suite detail with membership.
/// </summary>
public sealed record SuiteDetailDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SuiteMemberDto> Members);

/// <summary>
/// Suite member with test case details.
/// </summary>
public sealed record SuiteMemberDto(
    Guid SuiteId,
    Guid TestCaseId,
    string TestKey,
    string Title,
    int ExecutionOrder,
    string? JiraIssueKey,
    string? FreshnessState);

/// <summary>
/// Suite creation input.
/// </summary>
public sealed record CreateSuiteCommand(
    Guid ProjectId,
    string Name,
    string? Description,
    string? Status,
    IReadOnlyList<CreateSuiteMemberCommand>? Members);

/// <summary>
/// Suite member creation input.
/// </summary>
public sealed record CreateSuiteMemberCommand(
    Guid TestCaseId,
    int ExecutionOrder);

/// <summary>
/// Suite update input.
/// </summary>
public sealed record UpdateSuiteCommand(
    string Name,
    string? Description,
    string? Status);

/// <summary>
/// Suite member reorder input.
/// </summary>
public sealed record ReorderSuiteMembersCommand(
    IReadOnlyList<SuiteMemberReorderItem> Members);

/// <summary>
/// Suite member reorder item.
/// </summary>
public sealed record SuiteMemberReorderItem(
    Guid TestCaseId,
    int ExecutionOrder);

/// <summary>
/// Suite execution request.
/// </summary>
public sealed record ExecuteSuiteCommand(
    Guid ProjectId,
    Guid SuiteId,
    string? IdempotencyKey);

/// <summary>
/// Suite execution result.
/// </summary>
public sealed record ExecuteSuiteResult(
    Guid ExecutionId,
    Guid SuiteId,
    int TestCount,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// Suite execution report. TriggerBreakdown and Trend are additive Slice 9B
/// extensions: existing consumers that omit the new filters observe the same
/// base fields as before plus an empty trend and a full-window breakdown.
/// </summary>
public sealed record SuiteReportDto(
    Guid SuiteId,
    string SuiteName,
    int TotalExecutions,
    int PassedCount,
    int FailedCount,
    int CancelledCount,
    int TimedOutCount,
    int ErrorCount,
    double? PassRate,
    long TotalDurationMs,
    long AverageDurationMs,
    DateTimeOffset? LatestExecutionAt,
    IReadOnlyList<TriggerBreakdownItem> TriggerBreakdown,
    IReadOnlyList<SuiteReportTrendPoint> Trend);

/// <summary>
/// Per-trigger aggregate within a suite report window. Total counts terminal
/// executions; the outcome fields count their tests.
/// </summary>
public sealed record TriggerBreakdownItem(
    string Trigger,
    int Total,
    int Passed,
    int Failed,
    double? PassRate);

/// <summary>
/// One daily bucket of a suite report trend. Total counts terminal
/// executions that day; the outcome fields count their tests.
/// </summary>
public sealed record SuiteReportTrendPoint(
    DateOnly Date,
    int Total,
    int Passed,
    int Failed,
    int Cancelled,
    int TimedOut,
    int Error,
    double? PassRate,
    long TotalDurationMs);

/// <summary>
/// Raw bounded trend inputs for in-memory day bucketing (no N+1, no
/// provider-specific date functions).
/// </summary>
public sealed record SuiteTrendData(
    IReadOnlyList<SuiteTrendExecution> Executions,
    IReadOnlyDictionary<Guid, SuiteTrendTests> TestsByExecution);

/// <summary>One terminal execution in a trend window.</summary>
public sealed record SuiteTrendExecution(
    Guid ExecutionId,
    string Trigger,
    DateTimeOffset CreatedAt);

/// <summary>Test aggregates for one execution.</summary>
public sealed record SuiteTrendTests(
    int Passed,
    int Failed,
    int Cancelled,
    int TimedOut,
    int Error,
    long TotalDurationMs);

/// <summary>
/// Suite execution summary for list view.
/// </summary>
public sealed record SuiteExecutionSummaryDto(
    Guid ExecutionId,
    string Status,
    string TriggerType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int TestCount,
    int PassedCount,
    int FailedCount);

/// <summary>
/// Paged suite execution history response.
/// </summary>
public sealed record PagedSuiteExecutionsResponse(
    IReadOnlyList<SuiteExecutionSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// Paged suite list.
/// </summary>
public sealed record PagedSuiteList(
    IReadOnlyList<SuiteListItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// Suite creation result.
/// </summary>
public sealed record CreateSuiteResult(
    Guid Id,
    Guid ProjectId,
    string Name,
    string TestKey);

/// <summary>
/// Suite membership result.
/// </summary>
public sealed record SuiteMemberResult(
    Guid SuiteId,
    Guid TestCaseId,
    int ExecutionOrder);

/// <summary>
/// Suite list filters.
/// </summary>
public sealed record SuiteListFilters(
    string? Search,
    string? Status);

/// <summary>
/// Filters for suite execution history.
/// </summary>
public sealed record SuiteExecutionHistoryFilters(
    string? Status,
    string? TriggerType);

/// <summary>
/// Suite filters for report. Trigger and GroupBy are optional Slice 9B
/// extensions; omitting them preserves the Slice 9A response shape.
/// </summary>
public sealed record SuiteReportFilters(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Trigger = null,
    string? GroupBy = null);

/// <summary>
/// Pagination parameters.
/// </summary>
public sealed record PaginationParams(
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// Suite member for list view.
/// </summary>
public sealed record SuiteMemberListDto(
    Guid TestCaseId,
    string TestKey,
    string Title,
    int ExecutionOrder,
    string? JiraIssueKey,
    string? FreshnessState);

/// <summary>
/// Suite management use cases (Phase 4 Slice 9A).
/// </summary>
public interface ISuiteService
{
    /// <summary>
    /// Creates a new test suite.
    /// </summary>
    Task<CreateSuiteResult> CreateAsync(CreateSuiteCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Lists suites for a project with pagination and filtering.
    /// </summary>
    Task<PagedSuiteList> ListAsync(Guid projectId, SuiteListFilters filters, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Gets suite details with members.
    /// </summary>
    Task<SuiteDetailDto?> GetByIdAsync(Guid suiteId, CancellationToken ct);

    /// <summary>
    /// Updates suite metadata.
    /// </summary>
    Task<SuiteDetailDto?> UpdateAsync(Guid suiteId, UpdateSuiteCommand command, CancellationToken ct);

    /// <summary>
    /// Archives a suite (soft delete).
    /// </summary>
    Task ArchiveAsync(Guid suiteId, CancellationToken ct);

    /// <summary>
    /// Adds a test case to the suite.
    /// </summary>
    Task<SuiteMemberResult> AddTestCaseAsync(Guid suiteId, CreateSuiteMemberCommand command, CancellationToken ct);

    /// <summary>
    /// Removes a test case from the suite.
    /// </summary>
    Task RemoveTestCaseAsync(Guid suiteId, Guid testCaseId, CancellationToken ct);

    /// <summary>
    /// Reorders test cases in the suite.
    /// </summary>
    Task ReorderAsync(Guid suiteId, ReorderSuiteMembersCommand command, CancellationToken ct);

    /// <summary>
    /// Gets execution history for the suite.
    /// </summary>
    Task<PagedSuiteExecutionsResponse> GetExecutionHistoryAsync(Guid suiteId, SuiteExecutionHistoryFilters filters, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Gets a basic report for the suite.
    /// </summary>
    Task<SuiteReportDto?> GetReportAsync(Guid suiteId, SuiteReportFilters filters, CancellationToken ct);
}