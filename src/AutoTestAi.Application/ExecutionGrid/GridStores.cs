using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>
/// Persistence seam for grid workers (Slice 9). No authorization here;
/// no credential material ever leaves through these methods except the
/// hash/salt columns the service needs for verification.
/// </summary>
public interface IGridWorkerStore
{
    Task<GridWorker?> GetByIdAsync(Guid workerId, CancellationToken ct);

    Task<GridWorker?> FindByKeyAsync(string workerKey, CancellationToken ct);

    Task<IReadOnlyList<GridWorker>> ListAsync(CancellationToken ct);

    Task AddAsync(GridWorker worker, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Persistence seam for assignment leases (Slice 9). Claim/insert paths rely
/// on the unique filtered index plus the worker concurrency token; unique
/// and concurrency violations surface as DbUpdateException.
/// </summary>
public interface IGridAssignmentStore
{
    Task<GridAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct);

    Task<GridAssignment?> FindActiveByTestAsync(Guid executionTestId, CancellationToken ct);

    Task<GridAssignment?> FindActiveByRefAsync(string workerAssignmentRef, CancellationToken ct);

    Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct);

    Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct);

    Task<int> CountActiveAsync(CancellationToken ct);

    Task<int> CountActiveByProjectAsync(Guid projectId, CancellationToken ct);

    Task<int> CountQueuedExecutionsAsync(CancellationToken ct);

    Task AddAsync(GridAssignment assignment, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
