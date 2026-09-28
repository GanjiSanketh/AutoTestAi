using AutoTestAi.Domain.Entities;
using DomainFailureAnalysis = AutoTestAi.Domain.Entities.FailureAnalysis;

namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Joined list row: execution plus its (single, MVP) test and test identity.
/// Produced by the store in one round-trip; no authorization here.
/// </summary>
public sealed record ExecutionListRow(
    Execution Execution,
    ExecutionTest Test,
    string TestKey,
    string TestTitle,
    int TestCaseVersionNumber);

/// <summary>
/// Persistence seam for execution history (Slice 5 §4). Implemented in
/// Infrastructure with EF Core; throws when no database is configured
/// (fail closed at the API). Methods do NOT authorize — the service enforces that.
/// Returned executions/tests are tracked so the service can transition them.
/// </summary>
public interface IExecutionStore
{
    Task<int> CountAsync(Guid projectId, string? status, Guid? testCaseId, CancellationToken ct);

    Task<IReadOnlyList<ExecutionListRow>> ListAsync(
        Guid projectId, string? status, Guid? testCaseId, int skip, int take, CancellationToken ct);

    Task<Execution?> GetExecutionByIdAsync(Guid executionId, CancellationToken ct);

    Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid executionTestId, CancellationToken ct);

    Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid executionId, CancellationToken ct);

    Task<Execution?> FindByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken ct);

    Task AddExecutionAsync(Execution execution, CancellationToken ct);

    Task AddExecutionTestAsync(ExecutionTest test, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    // ---------- step results ----------

    Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid executionTestId, CancellationToken ct);

    Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct);

    Task DeleteStepResultsAsync(Guid executionTestId, CancellationToken ct);

    // ---------- logs (bounded reads, chronological cursor) ----------

    Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(
        Guid executionTestId, long? afterId, int take, CancellationToken ct);

    Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct);

    Task DeleteLogsAsync(Guid executionTestId, CancellationToken ct);

    // ---------- artifacts ----------

    Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid executionTestId, CancellationToken ct);

    Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken ct);

    Task AddArtifactAsync(ExecutionArtifact artifact, CancellationToken ct);

    Task DeleteArtifactsAsync(Guid executionTestId, CancellationToken ct);

    // ---------- failure analyses (attempt history, never overwritten) ----------

    Task<IReadOnlyList<DomainFailureAnalysis>> ListAnalysesAsync(Guid executionTestId, CancellationToken ct);

    Task<DomainFailureAnalysis?> GetAnalysisByIdAsync(Guid analysisId, CancellationToken ct);

    /// <summary>
    /// Persists a Running attempt. Throws <see cref="Common.ConflictException"/>
    /// when another Running attempt already exists (unique filtered index).
    /// </summary>
    Task AddAnalysisAsync(DomainFailureAnalysis analysis, CancellationToken ct);
}
