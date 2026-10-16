using AutoTestAi.Application.Sla;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Sla;

/// <summary>EF Core SLA target policy seam. No authorization here.</summary>
public sealed class EfSlaTargetStore : ISlaTargetStore
{
    private readonly AutoTestAiDbContext _db;

    public EfSlaTargetStore(AutoTestAiDbContext db) => _db = db;

    public Task<SlaTargetPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
        => _db.SlaTargetPolicies.FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);

    public Task AddAsync(SlaTargetPolicy row, CancellationToken ct)
        => _db.SlaTargetPolicies.AddAsync(row, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}

/// <summary>Fail-closed store used when no database is configured.</summary>
public sealed class UnavailableSlaTargetStore : ISlaTargetStore
{
    public Task<SlaTargetPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task AddAsync(SlaTargetPolicy row, CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
    public Task SaveChangesAsync(CancellationToken ct)
        => throw new InvalidOperationException("The database is not configured.");
}
