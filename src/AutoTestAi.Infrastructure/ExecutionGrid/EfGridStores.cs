using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.ExecutionGrid;

/// <summary>EF Core grid seams. Tracked entities so the scheduler's
/// concurrency tokens and unique indexes do their job. No authorization here.</summary>
public sealed class EfGridWorkerStore : IGridWorkerStore
{
    private readonly AutoTestAiDbContext _db;

    public EfGridWorkerStore(AutoTestAiDbContext db) => _db = db;

    public Task<GridWorker?> GetByIdAsync(Guid workerId, CancellationToken ct)
        => _db.GridWorkers.FirstOrDefaultAsync(w => w.Id == workerId, ct);

    public Task<GridWorker?> FindByKeyAsync(string workerKey, CancellationToken ct)
        => _db.GridWorkers.FirstOrDefaultAsync(w => w.WorkerKey == workerKey, ct);

    public async Task<IReadOnlyList<GridWorker>> ListAsync(CancellationToken ct)
        => await _db.GridWorkers.AsNoTracking().ToListAsync(ct);

    public Task AddAsync(GridWorker worker, CancellationToken ct)
        => _db.GridWorkers.AddAsync(worker, ct).AsTask();

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new ConflictException("A grid worker changed concurrently; retry the operation.");
        }
    }
}

/// <summary>EF Core assignment-lease seam. Claim paths rely on the unique
/// filtered lease index; violations surface as DbUpdateException for the
/// scheduler to resolve by trying the next worker.</summary>
public sealed class EfGridAssignmentStore : IGridAssignmentStore
{
    private static readonly GridAssignmentStatus[] Active =
    {
        GridAssignmentStatus.Claimed,
        GridAssignmentStatus.Running,
    };

    private readonly AutoTestAiDbContext _db;

    public EfGridAssignmentStore(AutoTestAiDbContext db) => _db = db;

    public Task<GridAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct)
        => _db.GridAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct);

    public Task<GridAssignment?> FindActiveByTestAsync(Guid executionTestId, CancellationToken ct)
        => _db.GridAssignments.FirstOrDefaultAsync(
            a => a.ExecutionTestId == executionTestId && Active.Contains(a.Status), ct);

    public Task<GridAssignment?> FindActiveByRefAsync(string workerAssignmentRef, CancellationToken ct)
        => _db.GridAssignments.FirstOrDefaultAsync(
            a => a.WorkerAssignmentRef == workerAssignmentRef && Active.Contains(a.Status), ct);

    public async Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct)
        => await _db.GridAssignments.Where(a => Active.Contains(a.Status)).ToListAsync(ct);

    public async Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(
        DateTimeOffset now, int take, CancellationToken ct)
        => await _db.GridAssignments
            .Where(a => Active.Contains(a.Status) && a.ExpiresAt < now)
            .OrderBy(a => a.ExpiresAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<int> CountActiveAsync(CancellationToken ct)
        => _db.GridAssignments.CountAsync(a => Active.Contains(a.Status), ct);

    public Task<int> CountActiveByProjectAsync(Guid projectId, CancellationToken ct)
        => _db.GridAssignments
            .Where(a => Active.Contains(a.Status))
            .Join(_db.Executions,
                a => a.ExecutionId,
                e => e.Id,
                (a, e) => e.ProjectId)
            .CountAsync(p => p == projectId, ct);

    public Task<int> CountQueuedExecutionsAsync(CancellationToken ct)
        => _db.Executions.CountAsync(
            e => e.Status == ExecutionStatus.Queued, ct);

    public Task AddAsync(GridAssignment assignment, CancellationToken ct)
        => _db.GridAssignments.AddAsync(assignment, ct).AsTask();

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Unique filtered lease index (double claim) and worker
            // concurrency-token (capacity overclaim) collisions land here.
            throw new ConflictException("An assignment changed concurrently; retry the operation.");
        }
    }
}

/// <summary>Fail-closed seams used when no database is configured.</summary>
public sealed class UnavailableGridWorkerStore : IGridWorkerStore
{
    public Task<GridWorker?> GetByIdAsync(Guid workerId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<GridWorker?> FindByKeyAsync(string workerKey, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<IReadOnlyList<GridWorker>> ListAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task AddAsync(GridWorker worker, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}

public sealed class UnavailableGridAssignmentStore : IGridAssignmentStore
{
    public Task<GridAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<GridAssignment?> FindActiveByTestAsync(Guid executionTestId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<GridAssignment?> FindActiveByRefAsync(string workerAssignmentRef, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset now, int take, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<int> CountActiveAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<int> CountActiveByProjectAsync(Guid projectId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<int> CountQueuedExecutionsAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task AddAsync(GridAssignment assignment, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}
