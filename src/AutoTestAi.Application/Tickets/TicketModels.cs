namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Manual Jira ticket creation DTOs (Slice 7). The internal defect remains the
/// system of record; the ticket stores external Jira identifiers only.
/// </summary>
public sealed record TicketDto(
    Guid Id,
    Guid ProjectId,
    Guid? DefectId,
    Guid? IntegrationId,
    string Provider,
    string? ExternalId,
    string? ExternalKey,
    string? ExternalUrl,
    string Title,
    string SyncStatus,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool AlreadyExisted = false,
    string Origin = "Manual");

public sealed record JiraIntegrationStatusDto(
    string Provider,
    bool Configured,
    bool Enabled,
    string? ProjectKey,
    string? BaseUrl,
    string? IssueType);

public sealed record UpsertJiraIntegrationCommand(
    Guid ProjectId,
    string BaseUrl,
    string ProjectKey,
    string Email,
    string? ApiToken,
    string? IssueType,
    IReadOnlyDictionary<string, string>? PriorityMapping,
    bool Enabled);

public sealed record JiraIntegrationDto(
    Guid Id,
    Guid ProjectId,
    string Provider,
    bool Enabled,
    string? BaseUrl,
    string? ProjectKey,
    string? Email,
    string? IssueType,
    IReadOnlyDictionary<string, string>? PriorityMapping,
    bool HasSecret,
    DateTimeOffset UpdatedAt);

/// <summary>Project-scoped Jira integration configuration (Slice 7).</summary>
public interface IJiraIntegrationService
{
    Task<JiraIntegrationStatusDto> GetStatusAsync(Guid projectId, CancellationToken cancellationToken);

    Task<JiraIntegrationDto> UpsertAsync(UpsertJiraIntegrationCommand command, CancellationToken cancellationToken);
}
