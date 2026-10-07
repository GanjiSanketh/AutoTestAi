using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestExecution;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Suite execution orchestration (Phase 4 Slice 9A).
/// </summary>
public interface ISuiteExecutionService
{
    /// <summary>
    /// Executes a suite manually.
    /// </summary>
    Task<ExecuteSuiteResult> ExecuteAsync(Guid projectId, Guid suiteId, ExecuteSuiteCommand command, CancellationToken ct);
}