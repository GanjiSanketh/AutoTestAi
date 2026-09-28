namespace AutoTestAi.Application.TestExecution;

/// <summary>Phase-1 seam for execution orchestration. Implemented in Phase 1 (FR-3.3).</summary>
public interface ITestExecutionService
{
    Task<Guid> StartExecutionAsync(StartExecutionCommand command, CancellationToken cancellationToken);
    Task CancelExecutionAsync(Guid executionId, CancellationToken cancellationToken);
}

public sealed record StartExecutionCommand(
    Guid ProjectId,
    Guid? SuiteId,
    Guid? EnvironmentId,
    IReadOnlyList<Guid> TestCaseIds);
