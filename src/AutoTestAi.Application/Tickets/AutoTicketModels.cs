using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Project-scoped automatic Jira ticket policy DTOs (Phase 2 Slice 10).
/// No Jira credentials are ever present in these shapes.
/// </summary>
public sealed record AutoTicketPolicyDto(
    Guid ProjectId,
    bool Enabled,
    Guid? IntegrationId,
    IReadOnlyList<string> Severities,
    IReadOnlyList<string> DefectStatuses,
    IReadOnlyList<string> Classifications,
    decimal? MinimumConfidence,
    DateTimeOffset UpdatedAt);

public sealed record UpsertAutoTicketPolicyCommand(
    Guid ProjectId,
    bool Enabled,
    Guid? IntegrationId,
    IReadOnlyList<string>? Severities,
    IReadOnlyList<string>? DefectStatuses,
    IReadOnlyList<string>? Classifications,
    decimal? MinimumConfidence);

public sealed record AutoTicketStatusDto(
    Guid ProjectId,
    bool Enabled,
    bool Configured,
    Guid? IntegrationId,
    IReadOnlyList<string> Severities,
    IReadOnlyList<string> DefectStatuses,
    IReadOnlyList<string> Classifications,
    decimal? MinimumConfidence,
    int PendingCount,
    int FailedCount,
    int SyncedAutomaticCount,
    DateTimeOffset? LastAutomationAt);

public sealed record AutoTicketAttemptDto(
    Guid TicketId,
    Guid? DefectId,
    string SyncStatus,
    string Origin,
    int AttemptCount,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset UpdatedAt);

/// <summary>Policy configuration seam (Phase 2 Slice 10). No authorization here.</summary>
public interface IAutoTicketPolicyService
{
    Task<AutoTicketPolicyDto?> GetAsync(Guid projectId, CancellationToken ct);

    Task<AutoTicketPolicyDto> UpsertAsync(UpsertAutoTicketPolicyCommand command, CancellationToken ct);

    Task<AutoTicketStatusDto> GetStatusAsync(Guid projectId, CancellationToken ct);
}

/// <summary>
/// System-driven automation seam (Phase 2 Slice 10). Called after an internal
/// defect is created and from background/retry paths. Never requires a human
/// permission; project scope is validated against the defect record itself.
/// </summary>
public interface IAutomatedTicketService
{
    /// <summary>
    /// Fast, Jira-free step run inline after defect creation: evaluates the
    /// deterministic policy and, when eligible, persists a Pending automatic
    /// ticket intent and enqueues background Jira execution.
    /// </summary>
    Task<AutoTicketRequestResult> RequestAutomationAsync(Guid projectId, Guid defectId, CancellationToken ct);

    /// <summary>Executes one persisted Pending/Failed-due automatic ticket (background or retry).</summary>
    Task<TicketDto?> ExecutePendingAsync(Guid ticketId, CancellationToken ct);

    /// <summary>Operator retry for a failed automatic ticket (requires tickets.create).</summary>
    Task<TicketDto> RetryFailedAsync(Guid projectId, Guid defectId, CancellationToken ct);
}

public sealed record AutoTicketRequestResult(
    string Outcome,
    Guid? TicketId = null);
