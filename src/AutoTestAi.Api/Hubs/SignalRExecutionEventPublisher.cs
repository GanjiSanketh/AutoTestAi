using AutoTestAi.Application.TestExecution;
using Microsoft.AspNetCore.SignalR;

namespace AutoTestAi.Api.Hubs;

/// <summary>SignalR fan-out for live execution events (Slice 5 §22).</summary>
public sealed class SignalRExecutionEventPublisher : IExecutionEventPublisher
{
    private readonly IHubContext<ExecutionHub> _hub;

    public SignalRExecutionEventPublisher(IHubContext<ExecutionHub> hub) => _hub = hub;

    public Task PublishAsync(
        Guid executionId,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
        => ExecutionHub.PublishAsync(_hub, executionId.ToString(), eventName, payload, cancellationToken);
}
