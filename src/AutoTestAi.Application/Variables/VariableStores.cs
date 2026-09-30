using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Variables;

/// <summary>Persistence seam for VariableSet aggregate (implemented in Infrastructure).</summary>
public interface IVariableSetStore
{
    Task<VariableSet?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<VariableSet>> ListByProjectAsync(Guid projectId, CancellationToken ct);
    Task<VariableSet?> FindByScopeAsync(Guid projectId, Domain.Enums.VariableScopeType scopeType, Guid? scopeId, CancellationToken ct);
    Task AddAsync(VariableSet set, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task DeleteAsync(VariableSet set, CancellationToken ct);
}

/// <summary>Persistence seam for per-execution override envelopes.</summary>
public interface IExecutionVariablesStore
{
    Task<ExecutionVariables?> GetByExecutionAsync(Guid executionId, CancellationToken ct);
    Task SaveAsync(ExecutionVariables envelope, CancellationToken ct);
}

/// <summary>Suite existence/ownership lookup (suites live in DbContext; no service layer).</summary>
public interface ITestSuiteLookup
{
    Task<TestSuite?> GetSuiteByIdAsync(Guid suiteId, CancellationToken ct);
}
