using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Tickets;

/// <summary>EF Core auto-ticket policy + automation query seams. No authorization here.</summary>
public sealed class EfAutoTicketPolicyStore : IAutoTicketPolicyStore
{
    private readonly AutoTestAiDbContext _db;

    public EfAutoTicketPolicyStore(AutoTestAiDbContext db) => _db = db;

    public Task<AutoTicketPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
        => _db.AutoTicketPolicies.FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);

    public Task AddAsync(AutoTicketPolicy policy, CancellationToken ct)
        => _db.AutoTicketPolicies.AddAsync(policy, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}

public sealed class EfAutoTicketQueryStore : IAutoTicketQueryStore
{
    private readonly AutoTestAiDbContext _db;

    public EfAutoTicketQueryStore(AutoTestAiDbContext db) => _db = db;

    public Task<Ticket?> GetTicketByIdAsync(Guid ticketId, CancellationToken ct)
        => _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);

    public Task<Ticket?> GetTicketSnapshotAsync(Guid ticketId, CancellationToken ct)
        => _db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);

    public Task<int> CountAsync(Guid projectId, string syncStatus, CancellationToken ct)
    {
        if (!Enum.TryParse<TicketSyncStatus>(syncStatus, ignoreCase: true, out var status))
            return Task.FromResult(0);
        if (status == TicketSyncStatus.Synced)
            return _db.Tickets.CountAsync(
                t => t.ProjectId == projectId && t.SyncStatus == status && t.Origin == TicketOrigin.Automatic, ct);
        return _db.Tickets.CountAsync(
            t => t.ProjectId == projectId && t.SyncStatus == status && t.Origin == TicketOrigin.Automatic, ct);
    }

    public async Task<DateTimeOffset?> LastAutomationAtAsync(Guid projectId, CancellationToken ct)
        => await _db.Tickets
            .Where(t => t.ProjectId == projectId && t.Origin == TicketOrigin.Automatic)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => (DateTimeOffset?)t.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Ticket>> ListRecentAutomationAsync(Guid projectId, int take, CancellationToken ct)
        => await _db.Tickets
            .Where(t => t.ProjectId == projectId && t.Origin == TicketOrigin.Automatic)
            .OrderByDescending(t => t.UpdatedAt)
            .Take(Math.Clamp(take, 1, 50))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Ticket>> ListDueAutomationAsync(DateTimeOffset now, int take, CancellationToken ct)
    {
        var pending = await _db.Tickets
            .Where(t => t.Origin == TicketOrigin.Automatic && t.SyncStatus == TicketSyncStatus.Pending)
            .OrderBy(t => t.UpdatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);
        if (pending.Count > 0)
            return pending;
        return await _db.Tickets
            .Where(t => t.Origin == TicketOrigin.Automatic &&
                t.SyncStatus == TicketSyncStatus.Failed &&
                t.NextAttemptAt != null && t.NextAttemptAt <= now)
            .OrderBy(t => t.NextAttemptAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}

/// <summary>Fail-closed stores used when no database is configured.</summary>
public sealed class UnavailableAutoTicketPolicyStore : IAutoTicketPolicyStore
{
    public Task<AutoTicketPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task AddAsync(AutoTicketPolicy policy, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}

public sealed class UnavailableAutoTicketQueryStore : IAutoTicketQueryStore
{
    public Task<Ticket?> GetTicketByIdAsync(Guid ticketId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<Ticket?> GetTicketSnapshotAsync(Guid ticketId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<int> CountAsync(Guid projectId, string syncStatus, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<DateTimeOffset?> LastAutomationAtAsync(Guid projectId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<IReadOnlyList<Ticket>> ListRecentAutomationAsync(Guid projectId, int take, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task<IReadOnlyList<Ticket>> ListDueAutomationAsync(DateTimeOffset now, int take, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}
