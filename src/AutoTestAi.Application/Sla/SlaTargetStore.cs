using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.Sla;

/// <summary>
/// Persistence seam for the one-per-project SLA target configuration.
/// Methods do NOT authorize — <see cref="ISlaTargetService"/> enforces that.
/// </summary>
public interface ISlaTargetStore
{
    Task<SlaTargetPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct);

    Task AddAsync(SlaTargetPolicy row, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
