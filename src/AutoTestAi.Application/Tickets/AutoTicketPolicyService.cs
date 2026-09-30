using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Project-scoped auto-ticket policy configuration (Phase 2 Slice 10).
/// Reads require tickets.read; writes require settings.manage. The policy
/// never contains credentials.
/// </summary>
public sealed class AutoTicketPolicyService : IAutoTicketPolicyService
{
    private static readonly IReadOnlySet<string> ValidSeverities =
        new HashSet<string>(Enum.GetNames<Severity>(), StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ValidStatuses =
        new HashSet<string>(Enum.GetNames<DefectStatus>(), StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ValidClassifications =
        new HashSet<string>(Enum.GetNames<FailureClassification>(), StringComparer.OrdinalIgnoreCase);

    private readonly IAutoTicketPolicyStore _policies;
    private readonly IIntegrationStore _integrations;
    private readonly IAutoTicketQueryStore _query;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public AutoTicketPolicyService(
        IAutoTicketPolicyStore policies,
        IIntegrationStore integrations,
        IAutoTicketQueryStore query,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _policies = policies;
        _integrations = integrations;
        _query = query;
        _authorization = authorization;
        _currentUser = currentUser;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<AutoTicketPolicyDto?> GetAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TicketsRead, ct);
        var row = await _policies.GetByProjectAsync(projectId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<AutoTicketPolicyDto> UpsertAsync(UpsertAutoTicketPolicyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var severities = Normalize(command.Severities, ValidSeverities, "severities",
            "Severities must be valid severity names ('Critical', 'High', 'Medium', 'Low').");
        var statuses = Normalize(command.DefectStatuses, ValidStatuses, "defectStatuses",
            "Defect statuses must be valid defect statuses ('Open', 'InProgress', 'Resolved', 'Closed', 'Rejected').");
        var classifications = Normalize(command.Classifications, ValidClassifications, "classifications",
            "Classifications must be valid failure classifications.");
        var errors = new List<FieldError>();
        if (command.Enabled)
        {
            if (severities.Count == 0)
                errors.Add(new FieldError("severities", "At least one eligible severity is required when automation is enabled."));
            if (statuses.Count == 0)
                errors.Add(new FieldError("defectStatuses", "At least one eligible defect status is required when automation is enabled."));
            if (classifications.Count == 0)
                errors.Add(new FieldError("classifications", "At least one eligible failure classification is required when automation is enabled."));
        }
        if (severities.Count > 10 || statuses.Count > 10 || classifications.Count > 10)
            errors.Add(new FieldError("severities", "Eligibility lists must contain at most 10 entries each."));
        if (command.MinimumConfidence is not null &&
            (command.MinimumConfidence.Value < 0 || command.MinimumConfidence.Value > 1))
            errors.Add(new FieldError("minimumConfidence", "Minimum confidence must be between 0 and 1."));
        ValidationException.ThrowIfInvalid(errors);

        // A pinned integration must belong to this project and serve Jira ticketing.
        Guid? integrationId = null;
        if (command.IntegrationId is not null)
        {
            var integration = await _integrations.GetByIdAsync(command.IntegrationId.Value, ct);
            if (integration is null || integration.ProjectId != command.ProjectId)
                throw new ValidationException("The pinned integration does not belong to this project.",
                    new[] { new FieldError("integrationId", "Integration must belong to this project.") });
            if (!string.Equals(integration.Provider, JiraIntegrationConfig.ProviderName, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("The pinned integration is not a Jira integration.",
                    new[] { new FieldError("integrationId", "Integration must be a Jira integration.") });
            integrationId = integration.Id;
        }

        var now = _clock.UtcNow;
        var actor = await ResolveAppUserIdAsync(ct);
        var existing = await _policies.GetByProjectAsync(command.ProjectId, ct);
        var isNew = existing is null;
        var row = existing ?? new AutoTicketPolicy { ProjectId = command.ProjectId, CreatedAt = now };
        row.Enabled = command.Enabled;
        row.IntegrationId = integrationId;
        row.Severities = AutoTicketEvaluator.Join(severities);
        row.DefectStatuses = AutoTicketEvaluator.Join(statuses);
        row.Classifications = AutoTicketEvaluator.Join(classifications);
        row.MinimumConfidence = command.MinimumConfidence;
        row.UpdatedBy = actor;
        row.UpdatedAt = now;
        if (isNew)
            await _policies.AddAsync(row, ct);
        await _policies.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            isNew ? "autoticket.policy_configured" : "autoticket.policy_updated",
            "auto-ticket-policy", row.Id.ToString(), command.ProjectId,
            SafeMeta(row), ct);
        return Map(row);
    }

    public async Task<AutoTicketStatusDto> GetStatusAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TicketsRead, ct);
        var row = await _policies.GetByProjectAsync(projectId, ct);
        var pending = await _query.CountAsync(projectId, nameof(TicketSyncStatus.Pending), ct);
        var failed = await _query.CountAsync(projectId, nameof(TicketSyncStatus.Failed), ct);
        var syncedAuto = await _query.CountAsync(projectId, nameof(TicketSyncStatus.Synced), ct);
        var lastAt = await _query.LastAutomationAtAsync(projectId, ct);
        if (row is null)
            return new AutoTicketStatusDto(projectId, false, false, null,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                null, pending, failed, syncedAuto, lastAt);
        return new AutoTicketStatusDto(projectId, row.Enabled, true, row.IntegrationId,
            AutoTicketEvaluator.ParseSeverities(row.Severities).ToList(),
            AutoTicketEvaluator.ParseStatuses(row.DefectStatuses).ToList(),
            AutoTicketEvaluator.ParseClassifications(row.Classifications).ToList(),
            row.MinimumConfidence, pending, failed, syncedAuto, lastAt);
    }

    private static List<string> Normalize(
        IReadOnlyList<string>? values, IReadOnlySet<string> valid, string field, string message)
    {
        var result = new List<string>();
        if (values is null)
            return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0 || !seen.Add(value))
                continue;
            if (!valid.Contains(value))
                throw new ValidationException(message, new[] { new FieldError(field, message) });
            result.Add(value);
        }
        return result;
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken ct)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, ct);

    private static AutoTicketPolicyDto Map(AutoTicketPolicy row) => new(
        row.ProjectId, row.Enabled, row.IntegrationId,
        AutoTicketEvaluator.ParseSeverities(row.Severities).ToList(),
        AutoTicketEvaluator.ParseStatuses(row.DefectStatuses).ToList(),
        AutoTicketEvaluator.ParseClassifications(row.Classifications).ToList(),
        row.MinimumConfidence, row.UpdatedAt);

    private static string SafeMeta(AutoTicketPolicy row)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            projectId = row.ProjectId,
            enabled = row.Enabled,
            integrationId = row.IntegrationId,
        }));
}
