namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Live execution fan-out (Slice 5 §22). Implemented with SignalR in the API
/// layer; the engine and activities depend only on this abstraction.
/// Payloads must never contain secrets or sensitive step values.
/// </summary>
public interface IExecutionEventPublisher
{
    Task PublishAsync(
        Guid executionId,
        string eventName,
        object payload,
        CancellationToken cancellationToken);
}
