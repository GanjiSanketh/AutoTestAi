using AutoTestAi.Application.SelfHealing;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoTestAi.Infrastructure.SelfHealing;

/// <summary>EF Core implementation of the self-healing seams. No authorization here.</summary>
public sealed class EfSelfHealingPolicyStore : ISelfHealingPolicyStore
{
    private readonly AutoTestAiDbContext _db;

    public EfSelfHealingPolicyStore(AutoTestAiDbContext db) => _db = db;

    public Task<SelfHealingPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
        => _db.SelfHealingPolicies.FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);

    public Task AddAsync(SelfHealingPolicy policy, CancellationToken ct)
        => _db.SelfHealingPolicies.AddAsync(policy, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}

public sealed class EfSelfHealingAttemptStore : ISelfHealingAttemptStore
{
    private readonly AutoTestAiDbContext _db;

    public EfSelfHealingAttemptStore(AutoTestAiDbContext db) => _db = db;

    public async Task<IReadOnlyList<SelfHealingAttempt>> ListByTestAsync(Guid executionTestId, CancellationToken ct)
        => await _db.SelfHealingAttempts
            .Where(a => a.ExecutionTestId == executionTestId)
            .OrderBy(a => a.StepOrder)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SelfHealingAttempt>> ListByExecutionAsync(Guid executionId, CancellationToken ct)
        => await _db.SelfHealingAttempts
            .Where(a => a.ExecutionId == executionId)
            .OrderBy(a => a.StepOrder)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<SelfHealingAttempt?> FindByTestAndStepAsync(
        Guid executionTestId, int stepOrder, CancellationToken ct)
        => _db.SelfHealingAttempts.FirstOrDefaultAsync(
            a => a.ExecutionTestId == executionTestId && a.StepOrder == stepOrder, ct);

    public async Task<HealingAttemptCounts> CountByProjectAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await _db.SelfHealingAttempts
            .Where(a => a.ProjectId == projectId)
            .AsNoTracking()
            .ToListAsync(ct);
        return new HealingAttemptCounts(
            rows.Count,
            rows.Count(r => r.WasApplied),
            rows.Where(r => r.WasApplied).OrderByDescending(r => r.CreatedAt)
                .Select(r => (DateTimeOffset?)r.CreatedAt).FirstOrDefault());
    }

    public async Task AddAsync(SelfHealingAttempt attempt, CancellationToken ct)
    {
        await _db.SelfHealingAttempts.AddAsync(attempt, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent worker won the (test, step) row: converge instead
            // of duplicating authoritative state (Slice 11 §S).
            throw new Application.Common.ConflictException(
                "A healing attempt already exists for this execution step.");
        }
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException pg && pg.SqlState == "23505";
}
