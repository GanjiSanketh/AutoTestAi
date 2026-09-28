using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Identity;

namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Authorizes SignalR execution subscriptions at the service level so the logic
/// is unit-testable without a live hub (docs/06 §12).
/// </summary>
public interface IExecutionSubscriptionAuthorizer
{
    /// <summary>
    /// Returns the owning project id when the caller may subscribe.
    /// Throws <see cref="KeyNotFoundException"/> when the execution is unknown
    /// and <see cref="ForbiddenException"/> when access is denied.
    /// </summary>
    Task<Guid> AuthorizeAsync(Guid executionId, CancellationToken cancellationToken);
}

public sealed class ExecutionSubscriptionAuthorizer : IExecutionSubscriptionAuthorizer
{
    private readonly IExecutionProjectResolver _resolver;
    private readonly IAuthorizationService _authorization;

    public ExecutionSubscriptionAuthorizer(
        IExecutionProjectResolver resolver, IAuthorizationService authorization)
    {
        _resolver = resolver;
        _authorization = authorization;
    }

    public async Task<Guid> AuthorizeAsync(Guid executionId, CancellationToken cancellationToken)
    {
        if (executionId == Guid.Empty)
            throw new ArgumentException("Execution id must not be empty.", nameof(executionId));
        var projectId = await _resolver.GetProjectIdAsync(executionId, cancellationToken);
        if (projectId is null)
            throw new KeyNotFoundException("Execution not found.");
        await _authorization.RequireProjectAccessAsync(
            projectId.Value, Permissions.ExecutionsRead, cancellationToken);
        return projectId.Value;
    }
}
