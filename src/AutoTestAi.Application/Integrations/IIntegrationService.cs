namespace AutoTestAi.Application.Integrations;

/// <summary>Phase-1 seam for the integrations hub (FR-1.3). Implemented in Phase 1.</summary>
public interface IIntegrationService
{
    Task<bool> TestConnectionAsync(Guid integrationId, CancellationToken cancellationToken);
}

public sealed record IntegrationDto(Guid Id, string Provider, string IntegrationType, string Status);
