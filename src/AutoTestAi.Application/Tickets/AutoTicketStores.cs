using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Tickets;

/// <summary>Persistence seam for auto-ticket policies (Phase 2 Slice 10). No authorization here.</summary>
public interface IAutoTicketPolicyStore
{
    Task<AutoTicketPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct);

    Task AddAsync(AutoTicketPolicy policy, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Read seam for automation queries (Phase 2 Slice 10). No authorization here.</summary>
public interface IAutoTicketQueryStore
{
    Task<Ticket?> GetTicketByIdAsync(Guid ticketId, CancellationToken ct);

    /// <summary>Detached snapshot for claim-ownership checks (never tracked).</summary>
    Task<Ticket?> GetTicketSnapshotAsync(Guid ticketId, CancellationToken ct);

    Task<int> CountAsync(Guid projectId, string syncStatus, CancellationToken ct);

    Task<DateTimeOffset?> LastAutomationAtAsync(Guid projectId, CancellationToken ct);

    Task<IReadOnlyList<Ticket>> ListRecentAutomationAsync(Guid projectId, int take, CancellationToken ct);

    /// <summary>Due work for background reconciliation: Pending intents plus Failed rows with NextAttemptAt elapsed.</summary>
    Task<IReadOnlyList<Ticket>> ListDueAutomationAsync(DateTimeOffset now, int take, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
