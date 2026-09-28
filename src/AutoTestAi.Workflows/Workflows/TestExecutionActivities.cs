using AutoTestAi.Application.TestExecution;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Activities;

namespace AutoTestAi.Workflows.Workflows;

/// <summary>
/// Real activity implementations (Slice 5 §11). Thin wrappers over the
/// Temporal-free <see cref="IExecutionEngine"/> resolved per-call from a
/// service scope, so workflow code stays deterministic and engine logic stays
/// unit-testable. One scope per activity keeps EF Core lifetimes correct.
/// </summary>
public sealed class TestExecutionActivities
{
    private readonly IServiceScopeFactory _scopes;

    public TestExecutionActivities(IServiceScopeFactory scopes) => _scopes = scopes;

    [Activity]
    public async Task<PreparedExecution> PrepareExecutionAsync(Guid executionId)
    {
        using var scope = _scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        return await engine.PrepareAsync(
            executionId, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity]
    public async Task<WorkerExecutionOutcome> RunWorkerExecutionAsync(Guid executionId)
    {
        using var scope = _scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        var context = ActivityExecutionContext.Current;
        return await engine.RunWorkerAsync(
            executionId,
            () =>
            {
                context.Heartbeat();
                return Task.CompletedTask;
            },
            context.CancellationToken);
    }

    [Activity]
    public async Task PersistExecutionResultAsync(Guid executionId, WorkerExecutionOutcome outcome)
    {
        using var scope = _scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        await engine.PersistResultAsync(
            executionId, outcome, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity]
    public async Task FinalizeCancelledAsync(Guid executionId, string reason)
    {
        using var scope = _scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        await engine.FinalizeCancelledAsync(executionId, reason, CancellationToken.None);
    }

    [Activity]
    public async Task FinalizeTimedOutAsync(Guid executionId, string reason)
    {
        using var scope = _scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        await engine.FinalizeTimedOutAsync(executionId, reason, CancellationToken.None);
    }

    [Activity]
    public async Task FinalizeErrorAsync(Guid executionId, string reason)
    {
        using var scope = _scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        await engine.FinalizeErrorAsync(executionId, reason, CancellationToken.None);
    }
}
