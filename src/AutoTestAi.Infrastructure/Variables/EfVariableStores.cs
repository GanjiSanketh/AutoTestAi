using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Variables;

/// <summary>EF Core variable-set seam (Slice 3A). No authorization here.</summary>
public sealed class EfVariableSetStore : IVariableSetStore
{
    private readonly AutoTestAiDbContext _db;

    public EfVariableSetStore(AutoTestAiDbContext db) => _db = db;

    public Task<VariableSet?> GetByIdAsync(Guid id, CancellationToken ct)
        => _db.VariableSets.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<VariableSet>> ListByProjectAsync(Guid projectId, CancellationToken ct)
        => await _db.VariableSets.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.ScopeType).ThenBy(s => s.Name)
            .ToListAsync(ct);

    public Task<VariableSet?> FindByScopeAsync(Guid projectId, VariableScopeType scopeType, Guid? scopeId, CancellationToken ct)
        => _db.VariableSets.FirstOrDefaultAsync(
            s => s.ProjectId == projectId && s.ScopeType == scopeType && s.ScopeId == scopeId, ct);

    public async Task AddAsync(VariableSet set, CancellationToken ct)
        => await _db.VariableSets.AddAsync(set, ct);

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    public Task DeleteAsync(VariableSet set, CancellationToken ct)
    {
        _db.VariableSets.Remove(set);
        return Task.CompletedTask;
    }
}

/// <summary>EF Core execution-envelope seam (Slice 3A). Refs only, never values.</summary>
public sealed class EfExecutionVariablesStore : IExecutionVariablesStore
{
    private readonly AutoTestAiDbContext _db;

    public EfExecutionVariablesStore(AutoTestAiDbContext db) => _db = db;

    public Task<ExecutionVariables?> GetByExecutionAsync(Guid executionId, CancellationToken ct)
        => _db.ExecutionVariables.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, ct);

    public async Task SaveAsync(ExecutionVariables envelope, CancellationToken ct)
    {
        var existing = await _db.ExecutionVariables
            .FirstOrDefaultAsync(e => e.ExecutionId == envelope.ExecutionId, ct);
        if (existing is null)
            await _db.ExecutionVariables.AddAsync(envelope, ct);
        else
        {
            existing.SuiteId = envelope.SuiteId;
            existing.EnvironmentId = envelope.EnvironmentId;
            existing.VariableOverridesJson = envelope.VariableOverridesJson;
            existing.SecretRefOverridesJson = envelope.SecretRefOverridesJson;
        }
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>Suite existence/ownership lookup over the shared DbContext.</summary>
public sealed class EfTestSuiteLookup : ITestSuiteLookup
{
    private readonly AutoTestAiDbContext _db;

    public EfTestSuiteLookup(AutoTestAiDbContext db) => _db = db;

    public Task<TestSuite?> GetSuiteByIdAsync(Guid suiteId, CancellationToken ct)
        => _db.TestSuites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == suiteId, ct);
}
