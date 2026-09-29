using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Tickets;

/// <summary>EF Core integration seam. Secrets stay in SecretReference; never projected to DTOs.</summary>
public sealed class EfIntegrationStore : IIntegrationStore
{
    private readonly AutoTestAiDbContext _db;

    public EfIntegrationStore(AutoTestAiDbContext db) => _db = db;

    public Task<Integration?> GetByIdAsync(Guid integrationId, CancellationToken ct)
        => _db.Integrations.FirstOrDefaultAsync(i => i.Id == integrationId, ct);

    public Task<Integration?> FindByProjectAndProviderAsync(Guid projectId, string provider, CancellationToken ct)
        => _db.Integrations.FirstOrDefaultAsync(i => i.ProjectId == projectId && i.Provider == provider, ct);

    public Task AddAsync(Integration integration, CancellationToken ct)
        => _db.Integrations.AddAsync(integration, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}

/// <summary>Fail-closed stores used when no database is configured.</summary>
public sealed class UnavailableTicketStore : ITicketStore
{
    public Task<Ticket?> GetByIdAsync(Guid ticketId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<Ticket?> FindSyncedAsync(Guid defectId, Guid integrationId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<Ticket?> FindLatestForDefectAsync(Guid defectId, Guid integrationId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<IReadOnlyList<Ticket>> ListForDefectAsync(Guid defectId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task AddAsync(Ticket ticket, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}

public sealed class UnavailableIntegrationStore : IIntegrationStore
{
    public Task<Integration?> GetByIdAsync(Guid integrationId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<Integration?> FindByProjectAndProviderAsync(Guid projectId, string provider, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task AddAsync(Integration integration, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}
