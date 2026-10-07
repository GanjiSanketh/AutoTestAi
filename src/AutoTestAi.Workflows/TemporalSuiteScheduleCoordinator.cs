using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Workflows.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace AutoTestAi.Workflows;

/// <summary>
/// Temporal Schedule-backed coordinator (Phase 4 Slice 9B). Owns all SDK
/// types; Application sees only <see cref="ISuiteScheduleCoordinator"/>.
/// One Temporal Schedule per row, deterministic id
/// <c>suite-schedule-{scheduleId:N}</c>; the tick action starts
/// <c>SuiteScheduleWorkflow</c> with a fixed base action id (the server
/// uniquifies per tick under AllowAll and enforces single-open under Skip).
/// </summary>
public sealed class TemporalSuiteScheduleCoordinator : ISuiteScheduleCoordinator
{
    private static readonly TimeSpan CatchupWindow = TimeSpan.FromMinutes(5);

    private readonly TemporalOptions _options;
    private readonly ILogger<TemporalSuiteScheduleCoordinator> _logger;

    public TemporalSuiteScheduleCoordinator(
        IOptions<TemporalOptions> options,
        ILogger<TemporalSuiteScheduleCoordinator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.Configured;

    public async Task CreateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct)
    {
        var client = await ConnectAsync(ct);
        try
        {
            await client.CreateScheduleAsync(
                    SuiteScheduleIds.ForSchedule(scheduleId),
                    BuildSchedule(scheduleId, definition),
                    new ScheduleOptions { TriggerImmediately = false })
                .WaitAsync(ct);
        }
        catch (RpcException ex)
        {
            throw MapRpc(ex, $"creating schedule {scheduleId}");
        }
        _logger.LogInformation("Created Temporal schedule {ScheduleId}.", SuiteScheduleIds.ForSchedule(scheduleId));
    }

    public async Task UpdateAsync(Guid scheduleId, SuiteScheduleDefinition definition, CancellationToken ct)
    {
        var handle = await HandleAsync(scheduleId, ct);
        try
        {
            await handle.UpdateAsync(
                    input => new ScheduleUpdate(
                        BuildSchedule(scheduleId, definition) with
                        {
                            // Preserve the existing action (workflow + args); only
                            // cadence, policy, and pause state change.
                            Action = input.Description.Schedule.Action,
                        },
                        input.Description.TypedSearchAttributes),
                    Rpc(ct))
                .WaitAsync(ct);
        }
        catch (RpcException ex)
        {
            throw MapRpc(ex, $"updating schedule {scheduleId}");
        }
    }

    public async Task PauseAsync(Guid scheduleId, string note, CancellationToken ct)
    {
        var handle = await HandleAsync(scheduleId, ct);
        try
        {
            await handle.PauseAsync(note, Rpc(ct)).WaitAsync(ct);
        }
        catch (RpcException ex)
        {
            throw MapRpc(ex, $"pausing schedule {scheduleId}");
        }
    }

    public async Task ResumeAsync(Guid scheduleId, string note, CancellationToken ct)
    {
        var handle = await HandleAsync(scheduleId, ct);
        try
        {
            await handle.UnpauseAsync(note, Rpc(ct)).WaitAsync(ct);
        }
        catch (RpcException ex)
        {
            throw MapRpc(ex, $"resuming schedule {scheduleId}");
        }
    }

    public async Task DeleteAsync(Guid scheduleId, CancellationToken ct)
    {
        var handle = await HandleAsync(scheduleId, ct);
        try
        {
            await handle.DeleteAsync(Rpc(ct)).WaitAsync(ct);
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            // Idempotent: already gone is the desired end state.
            return;
        }
        catch (RpcException ex)
        {
            throw MapRpc(ex, $"deleting schedule {scheduleId}");
        }
    }

    public async Task<DateTimeOffset?> GetNextRunAsync(Guid scheduleId, CancellationToken ct)
    {
        if (!IsConfigured)
            return null;
        var handle = await HandleAsync(scheduleId, ct);
        ScheduleDescription description;
        try
        {
            description = await handle.DescribeAsync(Rpc(ct)).WaitAsync(ct);
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            return null;
        }
        catch (RpcException)
        {
            return null;
        }
        var next = description.Info.NextActionTimes.FirstOrDefault();
        return next == default ? null : new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Utc));
    }

    // ---------- helpers ----------

    private Schedule BuildSchedule(Guid scheduleId, SuiteScheduleDefinition definition)
    {
        var actionId = SuiteScheduleIds.ForSchedule(scheduleId);
        var action = ScheduleActionStartWorkflow.Create(
            (Workflows.SuiteScheduleWorkflow wf) => wf.RunAsync(scheduleId),
            new WorkflowOptions(actionId, _options.TaskQueue));
        return new Schedule(
            action,
            new ScheduleSpec
            {
                CronExpressions = new List<string> { definition.CronExpression },
                TimeZoneName = definition.TimeZoneId,
            })
        {
            Policy = new SchedulePolicy
            {
                Overlap = MapOverlap(definition.OverlapPolicy),
                CatchupWindow = CatchupWindow,
            },
            State = new ScheduleState
            {
                Paused = definition.Paused,
                Note = definition.Paused ? "Paused." : string.Empty,
            },
        };
    }

    private static ScheduleOverlapPolicy MapOverlap(string overlap)
        => string.Equals(overlap, Domain.Enums.ScheduleOverlapPolicy.Allow.ToString(), StringComparison.OrdinalIgnoreCase)
            ? ScheduleOverlapPolicy.AllowAll
            : ScheduleOverlapPolicy.Skip;

    private async Task<ITemporalClient> ConnectAsync(CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Temporal is not configured. Set the Temporal section (address/namespace).");
        return await TemporalClient.ConnectAsync(
                new TemporalClientConnectOptions(_options.Address) { Namespace = _options.Namespace })
            .WaitAsync(ct);
    }

    private async Task<ScheduleHandle> HandleAsync(Guid scheduleId, CancellationToken ct)
    {
        var client = await ConnectAsync(ct);
        return client.GetScheduleHandle(SuiteScheduleIds.ForSchedule(scheduleId));
    }

    private static Temporalio.Client.RpcOptions Rpc(CancellationToken ct)
        => new() { CancellationToken = ct };

    private static Exception MapRpc(RpcException ex, string operation)
        => ex.Code switch
        {
            RpcException.StatusCode.NotFound
                => new NotFoundException("Test suite schedule was not found in the scheduler."),
            RpcException.StatusCode.InvalidArgument
                => new ValidationException($"The scheduler rejected the schedule ({operation}).",
                    [new FieldError("cronExpression", ex.Message)]),
            RpcException.StatusCode.AlreadyExists
                => new ConflictException("The schedule already exists in the scheduler."),
            _ => new InvalidOperationException(
                $"Temporal scheduling is unavailable ({operation})."),
        };
}
