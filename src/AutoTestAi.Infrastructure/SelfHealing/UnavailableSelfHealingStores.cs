using AutoTestAi.Application.SelfHealing;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Infrastructure.SelfHealing;

/// <summary>Fail-closed stores used when no database is configured.</summary>
public sealed class UnavailableSelfHealingPolicyStore : ISelfHealingPolicyStore
{
    public Task<SelfHealingPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");

    public Task AddAsync(SelfHealingPolicy policy, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");

    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}

public sealed class UnavailableSelfHealingAttemptStore : ISelfHealingAttemptStore
{
    public Task<IReadOnlyList<SelfHealingAttempt>> ListByTestAsync(Guid executionTestId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");

    public Task<IReadOnlyList<SelfHealingAttempt>> ListByExecutionAsync(Guid executionId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");

    public Task<SelfHealingAttempt?> FindByTestAndStepAsync(
        Guid executionTestId, int stepOrder, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");

    public Task<HealingAttemptCounts> CountByProjectAsync(Guid projectId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");

    public Task AddAsync(SelfHealingAttempt attempt, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");

    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}
