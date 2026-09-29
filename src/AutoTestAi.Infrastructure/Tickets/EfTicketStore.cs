using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Tickets;

/// <summary>EF Core ticket seam. No authorization here.</summary>
public sealed class EfTicketStore : ITicketStore
{
    private readonly AutoTestAiDbContext _db;

    public EfTicketStore(AutoTestAiDbContext db) => _db = db;

    public Task<Ticket?> GetByIdAsync(Guid ticketId, CancellationToken ct)
        => _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);

    public Task<Ticket?> FindSyncedAsync(Guid defectId, Guid integrationId, CancellationToken ct)
        => _db.Tickets
            .Where(t => t.DefectId == defectId && t.IntegrationId == integrationId && t.SyncStatus == TicketSyncStatus.Synced)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<Ticket?> FindLatestForDefectAsync(Guid defectId, Guid integrationId, CancellationToken ct)
        => _db.Tickets
            .Where(t => t.DefectId == defectId && t.IntegrationId == integrationId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Ticket>> ListForDefectAsync(Guid defectId, CancellationToken ct)
        => await _db.Tickets
            .Where(t => t.DefectId == defectId)
            .OrderByDescending(t => t.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task AddAsync(Ticket ticket, CancellationToken ct)
        => _db.Tickets.AddAsync(ticket, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}
