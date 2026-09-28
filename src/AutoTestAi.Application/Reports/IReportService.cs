namespace AutoTestAi.Application.Reports;

/// <summary>Phase-1 seam for quality dashboards (FR-2.1/2.4). Implemented in Phase 1.</summary>
public interface IReportService
{
    Task<DashboardSummaryDto> GetDashboardSummaryAsync(Guid? projectId, CancellationToken cancellationToken);
}

public sealed record DashboardSummaryDto(
    int TotalTestCases,
    int PassedTests,
    int FailedTests,
    int OpenDefects);
