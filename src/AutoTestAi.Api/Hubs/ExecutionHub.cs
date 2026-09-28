using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.TestExecution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AutoTestAi.Api.Hubs;

/// <summary>
/// Real-time execution hub (docs/06 §12).
/// Requires authentication; every subscription is authorized against project
/// membership before the connection joins the execution group.
/// </summary>
[Authorize]
public sealed class ExecutionHub : Hub
{
    private readonly IExecutionSubscriptionAuthorizer _authorizer;
    private readonly ILogger<ExecutionHub> _logger;

    public ExecutionHub(IExecutionSubscriptionAuthorizer authorizer, ILogger<ExecutionHub> logger)
    {
        _authorizer = authorizer;
        _logger = logger;
    }

    public async Task SubscribeToExecution(string executionId)
    {
        if (!Guid.TryParse(executionId, out var id))
            throw new HubException("Invalid execution id.");
        try
        {
            var projectId = await _authorizer.AuthorizeAsync(id, Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(executionId));
            // Category-only logging: no tokens, no payload content.
            _logger.LogInformation(
                "Execution subscription accepted for execution {ExecutionId} in project {ProjectId}.",
                id, projectId);
        }
        catch (KeyNotFoundException)
        {
            throw new HubException("Execution not found.");
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            throw new HubException("Authentication is required.");
        }
        catch (ForbiddenException)
        {
            _logger.LogWarning("Execution subscription denied for execution {ExecutionId}.", id);
            throw new HubException("Forbidden: no access to this execution.");
        }
    }

    public async Task UnsubscribeFromExecution(string executionId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(executionId));

    public static string GroupFor(string executionId) => $"execution:{executionId}";

    /// <summary>
    /// Server-side fan-out helper used by Phase-1 workflow event publishers.
    /// Keeps event-name constants in one place (<see cref="ExecutionEvents"/>).
    /// </summary>
    public static Task PublishAsync(
        IHubContext<ExecutionHub> hub,
        string executionId,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
        => hub.Clients.Group(GroupFor(executionId)).SendAsync(eventName, payload, cancellationToken);
}
