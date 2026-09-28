namespace AutoTestAi.Application.Defects;

/// <summary>Phase-1 seam for defect triage (FR-3.4). Implemented in Phase 1.</summary>
public interface IDefectService
{
    Task<Guid> CreateFromFailureAsync(CreateDefectCommand command, CancellationToken cancellationToken);
}

public sealed record CreateDefectCommand(Guid ProjectId, Guid? ExecutionTestId, string Title, string Severity);
