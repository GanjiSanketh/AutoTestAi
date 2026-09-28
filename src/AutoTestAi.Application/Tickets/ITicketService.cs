namespace AutoTestAi.Application.Tickets;

/// <summary>Phase-1 seam for manual ticket creation (FR-3.5). Implemented in Phase 1.</summary>
public interface ITicketService
{
    Task<Guid> CreateManualTicketAsync(CreateTicketCommand command, CancellationToken cancellationToken);
}

public sealed record CreateTicketCommand(Guid DefectId, string Provider, string Title);
