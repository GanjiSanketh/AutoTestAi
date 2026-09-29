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
/// Project-scoped Jira integration configuration (Slice 7 §7).
/// Authorized via settings.manage; secrets stay server-side and are never
/// returned (GET reports configured:true instead).
/// </summary>
public sealed class JiraIntegrationService : IJiraIntegrationService
{
    private readonly IIntegrationStore _store;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public JiraIntegrationService(
        IIntegrationStore store,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _store = store;
        _authorization = authorization;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
    }

    public async Task<JiraIntegrationStatusDto> GetStatusAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TicketsRead, ct);
        var row = await _store.FindByProjectAndProviderAsync(projectId, JiraIntegrationConfig.ProviderName, ct);
        if (row is null)
            return new JiraIntegrationStatusDto(JiraIntegrationConfig.ProviderName, false, false, null, null, null);
        var config = JiraIntegrationConfig.FromJson(row.Configuration);
        return new JiraIntegrationStatusDto(
            JiraIntegrationConfig.ProviderName,
            Configured: IsConfigured(row, config),
            Enabled: row.Status == IntegrationStatus.Active,
            ProjectKey: string.IsNullOrWhiteSpace(config.ProjectKey) ? null : config.ProjectKey,
            BaseUrl: string.IsNullOrWhiteSpace(config.BaseUrl) ? null : config.BaseUrl,
            IssueType: config.IssueType);
    }

    public async Task<JiraIntegrationDto> UpsertAsync(UpsertJiraIntegrationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var errors = new List<FieldError>();
        var baseUrl = command.BaseUrl?.Trim() ?? string.Empty;
        var projectKey = command.ProjectKey?.Trim() ?? string.Empty;
        var email = command.Email?.Trim() ?? string.Empty;
        var issueType = string.IsNullOrWhiteSpace(command.IssueType) ? JiraIntegrationConfig.DefaultIssueType : command.IssueType.Trim();
        JiraIntegrationConfig.ValidateFields(baseUrl, projectKey, email, issueType, errors);
        var token = command.ApiToken?.Trim() ?? string.Empty;
        ValidationException.ThrowIfInvalid(errors);

        var existing = await _store.FindByProjectAndProviderAsync(command.ProjectId, JiraIntegrationConfig.ProviderName, ct);
        // Retain the existing secret when the caller does not resend it.
        string? secret = string.IsNullOrWhiteSpace(token) ? existing?.SecretReference : token;
        if (string.IsNullOrWhiteSpace(secret))
            throw new ValidationException("Jira API token is required.",
                new[] { new FieldError("apiToken", "Jira API token is required.") });

        var normalized = new JiraIntegrationConfig(
            JiraIntegrationConfig.NormalizeBaseUrl(baseUrl),
            JiraIntegrationConfig.NormalizeProjectKey(projectKey),
            email,
            issueType,
            JiraIntegrationConfig.NormalizePriorityMapping(command.PriorityMapping),
            AppBaseUrl: null);

        var now = _clock.UtcNow;
        Integration row;
        var isNew = false;
        if (existing is null)
        {
            row = new Integration
            {
                ProjectId = command.ProjectId,
                Provider = JiraIntegrationConfig.ProviderName,
                IntegrationType = "ticketing",
                Configuration = JsonDocument.Parse(SensitiveDataRedactor.Redact(normalized.ToJson())),
                SecretReference = secret,
                Status = command.Enabled ? IntegrationStatus.Active : IntegrationStatus.Disabled,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _store.AddAsync(row, ct);
            isNew = true;
        }
        else
        {
            row = existing;
            row.Configuration = JsonDocument.Parse(SensitiveDataRedactor.Redact(normalized.ToJson()));
            // Never overwrite a stored secret with an empty value.
            row.SecretReference = secret;
            row.Status = command.Enabled ? IntegrationStatus.Active : IntegrationStatus.Disabled;
            row.UpdatedAt = now;
        }
        await _store.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            isNew ? "integration.jira_configured" : "integration.jira_updated",
            "integration", row.Id.ToString(), command.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                provider = JiraIntegrationConfig.ProviderName,
                projectKey = normalized.ProjectKey,
                enabled = command.Enabled,
            })), ct);

        return new JiraIntegrationDto(
            row.Id, command.ProjectId, JiraIntegrationConfig.ProviderName,
            command.Enabled, normalized.BaseUrl, normalized.ProjectKey,
            normalized.Email, normalized.IssueType, normalized.PriorityMapping,
            HasSecret: true, row.UpdatedAt);
    }

    private static bool IsConfigured(Integration row, JiraIntegrationConfig config)
        => !string.IsNullOrWhiteSpace(config.BaseUrl) &&
           !string.IsNullOrWhiteSpace(config.ProjectKey) &&
           !string.IsNullOrWhiteSpace(config.Email) &&
           !string.IsNullOrWhiteSpace(row.SecretReference);
}
