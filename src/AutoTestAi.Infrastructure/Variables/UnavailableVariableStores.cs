using AutoTestAi.Application.Common;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.Variables;

/// <summary>Fail-closed variable seams when no database is configured.</summary>
public sealed class UnavailableVariableSetStore : IVariableSetStore
{
    private static Exception Unavailable() => new InvalidOperationException("Variable storage is not configured.");
    public Task<VariableSet?> GetByIdAsync(Guid id, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<VariableSet>> ListByProjectAsync(Guid projectId, CancellationToken ct) => throw Unavailable();
    public Task<VariableSet?> FindByScopeAsync(Guid projectId, Domain.Enums.VariableScopeType scopeType, Guid? scopeId, CancellationToken ct) => throw Unavailable();
    public Task AddAsync(VariableSet set, CancellationToken ct) => throw Unavailable();
    public Task SaveChangesAsync(CancellationToken ct) => throw Unavailable();
    public Task DeleteAsync(VariableSet set, CancellationToken ct) => throw Unavailable();
}

public sealed class UnavailableExecutionVariablesStore : IExecutionVariablesStore
{
    public Task<ExecutionVariables?> GetByExecutionAsync(Guid executionId, CancellationToken ct)
        => Task.FromResult<ExecutionVariables?>(null);
    public Task SaveAsync(ExecutionVariables envelope, CancellationToken ct)
        => throw new InvalidOperationException("Variable storage is not configured.");
}

public sealed class UnavailableTestSuiteLookup : ITestSuiteLookup
{
    public Task<TestSuite?> GetSuiteByIdAsync(Guid suiteId, CancellationToken ct)
        => Task.FromResult<TestSuite?>(null);
}
