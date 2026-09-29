using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Tickets;

/// <summary>Persistence seam for tickets (Slice 7). No authorization here.</summary>
public interface ITicketStore
{
    Task<Ticket?> GetByIdAsync(Guid ticketId, CancellationToken ct);

    Task<Ticket?> FindSyncedAsync(Guid defectId, Guid integrationId, CancellationToken ct);

    Task<Ticket?> FindLatestForDefectAsync(Guid defectId, Guid integrationId, CancellationToken ct);

    Task<IReadOnlyList<Ticket>> ListForDefectAsync(Guid defectId, CancellationToken ct);

    Task AddAsync(Ticket ticket, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Persistence seam for project integrations (Slice 7). No authorization here.</summary>
public interface IIntegrationStore
{
    Task<Integration?> GetByIdAsync(Guid integrationId, CancellationToken ct);

    Task<Integration?> FindByProjectAndProviderAsync(Guid projectId, string provider, CancellationToken ct);

    Task AddAsync(Integration integration, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
