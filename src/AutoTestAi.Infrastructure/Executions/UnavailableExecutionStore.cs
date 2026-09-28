using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.Executions;

/// <summary>
/// Fail-closed store used when no database is configured: every operation
/// surfaces as a dependency failure (503) rather than inventing data.
/// </summary>
public sealed class UnavailableExecutionStore : IExecutionStore
{
    private static Task<T> Unavailable<T>() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    private static Task Unavailable() => throw new InvalidOperationException(
        "PostgreSQL is not configured. Set ConnectionStrings:Postgres.");

    public Task<int> CountAsync(Guid projectId, string? status, Guid? testCaseId, CancellationToken ct)
        => Unavailable<int>();
    public Task<IReadOnlyList<ExecutionListRow>> ListAsync(
        Guid projectId, string? status, Guid? testCaseId, int skip, int take, CancellationToken ct)
        => Unavailable<IReadOnlyList<ExecutionListRow>>();
    public Task<Execution?> GetExecutionByIdAsync(Guid executionId, CancellationToken ct)
        => Unavailable<Execution?>();
    public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid executionTestId, CancellationToken ct)
        => Unavailable<ExecutionTest?>();
    public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid executionId, CancellationToken ct)
        => Unavailable<IReadOnlyList<ExecutionTest>>();
    public Task<Execution?> FindByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken ct)
        => Unavailable<Execution?>();
    public Task AddExecutionAsync(Execution execution, CancellationToken ct) => Unavailable();
    public Task AddExecutionTestAsync(ExecutionTest test, CancellationToken ct) => Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => Unavailable();
    public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid executionTestId, CancellationToken ct)
        => Unavailable<IReadOnlyList<ExecutionStepResult>>();
    public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct) => Unavailable();
    public Task DeleteStepResultsAsync(Guid executionTestId, CancellationToken ct) => Unavailable();
    public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(
        Guid executionTestId, long? afterId, int take, CancellationToken ct)
        => Unavailable<IReadOnlyList<ExecutionLog>>();
    public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct) => Unavailable();
    public Task DeleteLogsAsync(Guid executionTestId, CancellationToken ct) => Unavailable();
    public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid executionTestId, CancellationToken ct)
        => Unavailable<IReadOnlyList<ExecutionArtifact>>();
    public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken ct)
        => Unavailable<ExecutionArtifact?>();
    public Task AddArtifactAsync(ExecutionArtifact artifact, CancellationToken ct) => Unavailable();
    public Task DeleteArtifactsAsync(Guid executionTestId, CancellationToken ct) => Unavailable();
}
