namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Manual ticket creation seam (FR-3.5, Slice 7). Human-triggered only:
/// create a Jira ticket from an internal defect. The defect remains the
/// system of record.
/// </summary>
public interface ITicketService
{
    Task<TicketDto> CreateFromDefectAsync(Guid projectId, Guid defectId, CancellationToken cancellationToken);

    Task<TicketDto?> GetForDefectAsync(Guid projectId, Guid defectId, CancellationToken cancellationToken);
}
