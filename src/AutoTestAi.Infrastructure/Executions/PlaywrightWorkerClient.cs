using AutoTestAi.Application.TestExecution;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Infrastructure.Executions;

/// <summary>
/// HTTP boundary to the isolated Playwright worker (Slice 5 §36). Holds the
/// server-side worker token in memory only — never logged, never returned.
/// Transport failures throw <see cref="WorkerInfrastructureException"/> (the
/// engine retries once); functional outcomes are returned as data.
///
/// Targets the single configured worker from <see cref="WorkerOptions"/>.
/// Grid dispatch (Phase 2 Slice 9) routes through per-worker endpoints via
/// <see cref="WorkerHttpTransport"/> instead.
/// </summary>
public sealed class PlaywrightWorkerClient : IPlaywrightWorkerClient
{
    private readonly WorkerHttpTransport _transport;
    private readonly IOptions<WorkerOptions> _options;

    public PlaywrightWorkerClient(
        WorkerHttpTransport transport,
        IOptions<WorkerOptions> options)
    {
        _transport = transport;
        _options = options;
    }

    public Task<string> StartAssignmentAsync(WorkerAssignmentDto assignment, CancellationToken ct)
    {
        var settings = _options.Value;
        return _transport.StartAssignmentAsync(
            settings.BaseUrl, settings.ApiToken, settings.RequestTimeoutSeconds, assignment, ct);
    }

    public Task<WorkerAssignmentProgressDto> GetAssignmentAsync(string assignmentId, CancellationToken ct)
    {
        var settings = _options.Value;
        return _transport.GetAssignmentAsync(
            settings.BaseUrl, settings.ApiToken, settings.RequestTimeoutSeconds, assignmentId, ct);
    }

    public Task CancelAssignmentAsync(string assignmentId, CancellationToken ct)
    {
        var settings = _options.Value;
        return _transport.CancelAssignmentAsync(
            settings.BaseUrl, settings.ApiToken, settings.RequestTimeoutSeconds, assignmentId, ct);
    }
}
