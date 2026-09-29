using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Reports;

/// <summary>
/// Read-only operational reports (Slice 8). Server-side filtering and
/// pagination over persisted data. Never mutates state.
/// </summary>
public sealed class ReportService : IReportService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private static readonly IReadOnlySet<string> ExecutionStatuses =
        new HashSet<string>(Enum.GetNames<ExecutionStatus>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> DefectStatuses =
        new HashSet<string>(Enum.GetNames<DefectStatus>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> Severities =
        new HashSet<string>(Enum.GetNames<Severity>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> Classifications =
        new HashSet<string>(Enum.GetNames<FailureClassification>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> SyncStatuses =
        new HashSet<string>(Enum.GetNames<TicketSyncStatus>(), StringComparer.OrdinalIgnoreCase);

    private readonly IReportQueryStore _store;
    private readonly IAuthorizationService _authorization;

    public ReportService(IReportQueryStore store, IAuthorizationService authorization)
    {
        _store = store;
        _authorization = authorization;
    }

    public async Task<PagedResult<ExecutionReportItem>> GetExecutionsAsync(
        Guid projectId, ReportDateRange range, ExecutionReportFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        DashboardService.ValidateExecutionStatus(filters.Status);
        DashboardService.ValidateClassification(filters.Classification);
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.QueryExecutionsAsync(projectId, range,
            new ExecutionReportFilters(
                Normalize(filters.Status), filters.TestCaseId, Normalize(filters.Classification)),
            skip, take, ct);
        return new PagedResult<ExecutionReportItem>(result.Items, result.TotalCount, pageNumber, size);
    }

    public async Task<PagedResult<DefectReportItem>> GetDefectsAsync(
        Guid projectId, ReportDateRange range, DefectReportFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        ValidateEnum(filters.Status, DefectStatuses, "status", "Status must be a valid defect status.");
        ValidateEnum(filters.Severity, Severities, "severity", "Severity must be 'Critical', 'High', 'Medium' or 'Low'.");
        if (!string.IsNullOrWhiteSpace(filters.Classification) &&
            !Classifications.Contains(filters.Classification.Trim()) &&
            !string.Equals(filters.Classification.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Classification filter is invalid.",
                new[] { new FieldError("classification", "Classification must be a valid failure classification.") });
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.QueryDefectsAsync(projectId, range,
            new DefectReportFilters(
                Normalize(filters.Status), Normalize(filters.Severity),
                Normalize(filters.Classification), filters.Search?.Trim()),
            skip, take, ct);
        return new PagedResult<DefectReportItem>(result.Items, result.TotalCount, pageNumber, size);
    }

    public async Task<PagedResult<TicketReportItem>> GetTicketsAsync(
        Guid projectId, ReportDateRange range, TicketReportFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        ValidateEnum(filters.SyncStatus, SyncStatuses, "syncStatus", "Sync status must be 'Pending', 'Synced' or 'Failed'.");
        if (!string.IsNullOrWhiteSpace(filters.Provider) && filters.Provider.Trim().Length > 60)
            throw new ValidationException("Provider filter is invalid.",
                new[] { new FieldError("provider", "Provider must be at most 60 characters.") });
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var result = await _store.QueryTicketsAsync(projectId, range,
            new TicketReportFilters(filters.Provider?.Trim(), Normalize(filters.SyncStatus)),
            skip, take, ct);
        return new PagedResult<TicketReportItem>(result.Items, result.TotalCount, pageNumber, size);
    }

    private static (int Skip, int Take, int Page, int Size) Paginate(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        return ((page - 1) * pageSize, pageSize, page, pageSize);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateEnum(
        string? value, IReadOnlySet<string> allowed, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!allowed.Contains(value.Trim()))
            throw new ValidationException(message, new[] { new FieldError(field, message) });
    }
}
