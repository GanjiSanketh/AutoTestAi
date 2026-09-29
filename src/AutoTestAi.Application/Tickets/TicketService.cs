using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using DomainFailureAnalysis = AutoTestAi.Domain.Entities.FailureAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.Tickets;

/// <summary>App-facing URL for defect traceability links embedded in Jira descriptions.</summary>
public sealed class TicketOptions
{
    public const string SectionName = "Tickets";
    public string? AppBaseUrl { get; set; }
}

/// <summary>
/// Manual Jira ticket creation from an internal defect (Slice 7).
/// Human-triggered only: no execution/defect/AI path calls this service.
/// The defect is never mutated; the ticket stores external identifiers.
/// </summary>
public sealed class TicketService : ITicketService
{
    private readonly ITicketStore _tickets;
    private readonly IIntegrationStore _integrations;
    private readonly IDefectStore _defects;
    private readonly IExecutionStore _executions;
    private readonly ITestCaseStore _cases;
    private readonly IJiraTicketProvider _jira;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly ILogger<TicketService> _logger;
    private readonly IOptions<TicketOptions> _options;

    public TicketService(
        ITicketStore tickets,
        IIntegrationStore integrations,
        IDefectStore defects,
        IExecutionStore executions,
        ITestCaseStore cases,
        IJiraTicketProvider jira,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit,
        ILogger<TicketService> logger,
        IOptions<TicketOptions> options)
    {
        _tickets = tickets;
        _integrations = integrations;
        _defects = defects;
        _executions = executions;
        _cases = cases;
        _jira = jira;
        _currentUser = currentUser;
        _authorization = authorization;
        _users = users;
        _clock = clock;
        _audit = audit;
        _logger = logger;
        _options = options;
    }

    public async Task<TicketDto?> GetForDefectAsync(Guid projectId, Guid defectId, CancellationToken ct)
    {
        var defect = await LoadAuthorizedDefectAsync(projectId, defectId, Permissions.TicketsRead, ct);
        var integration = await _integrations.FindByProjectAndProviderAsync(projectId, JiraIntegrationConfig.ProviderName, ct);
        if (integration is null)
            return null;
        var rows = await _tickets.ListForDefectAsync(defect.Id, ct);
        var synced = rows
            .Where(t => t.IntegrationId == integration.Id && t.SyncStatus == TicketSyncStatus.Synced)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefault();
        return synced is null ? null : Map(synced, alreadyExisted: true);
    }

    public async Task<TicketDto> CreateFromDefectAsync(Guid projectId, Guid defectId, CancellationToken ct)
    {
        var defect = await LoadAuthorizedDefectAsync(projectId, defectId, Permissions.TicketsCreate, ct);

        var integration = await _integrations.FindByProjectAndProviderAsync(projectId, JiraIntegrationConfig.ProviderName, ct)
            ?? throw new ConflictException("Jira is not configured for this project. Configure the Jira integration first.");
        if (integration.Status != IntegrationStatus.Active)
            throw new ConflictException("The Jira integration is disabled for this project.");
        if (integration.ProjectId != projectId)
            throw new ForbiddenException("The caller has no access to this project.");

        var config = JiraIntegrationConfig.FromJson(integration.Configuration);
        if (string.IsNullOrWhiteSpace(config.BaseUrl) ||
            string.IsNullOrWhiteSpace(config.ProjectKey) ||
            string.IsNullOrWhiteSpace(config.Email) ||
            string.IsNullOrWhiteSpace(integration.SecretReference))
            throw new ConflictException("The Jira integration is not fully configured.");

        // Idempotency: one successful ticket per defect per integration.
        var existing = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
        if (existing is not null)
            return Map(existing, alreadyExisted: true);

        var pending = await _tickets.FindLatestForDefectAsync(defect.Id, integration.Id, ct);
        var reusableFailed = pending is not null && pending.SyncStatus == TicketSyncStatus.Failed ? pending : null;

        await _audit.RecordAsync("ticket.creation_requested", "ticket", null, projectId,
            SafeMeta(defect, integration), ct);

        // Build provider-neutral request from persisted data only.
        var (analysis, testInfo) = await LoadContextAsync(defect, ct);
        var appBase = (_options.Value.AppBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        string? defectRef = !string.IsNullOrWhiteSpace(appBase)
            ? $"{appBase}/projects/{projectId}/bugs/{defect.Id}"
            : null;
        var request = new JiraCreateRequest(
            JiraIntegrationConfig.NormalizeBaseUrl(config.BaseUrl),
            JiraIntegrationConfig.NormalizeProjectKey(config.ProjectKey),
            string.IsNullOrWhiteSpace(config.IssueType) ? JiraIntegrationConfig.DefaultIssueType : config.IssueType.Trim(),
            JiraTicketContentBuilder.BuildSummary(defect),
            JiraTicketContentBuilder.BuildDescription(defect, config, analysis, testInfo, defectRef),
            JiraSeverityMapper.Map(defect.Severity, config.PriorityMapping));

        JiraCreateResult result;
        try
        {
            result = await _jira.CreateIssueAsync(request, config.Email.Trim(), integration.SecretReference, ct);
        }
        catch (JiraProviderException ex)
        {
            await PersistFailureAsync(defect, integration, reusableFailed, FriendlyJiraError(ex), ct);
            await _audit.RecordAsync("ticket.creation_failed", "ticket",
                reusableFailed?.Id.ToString(), projectId, SafeFailureMeta(defect, integration, ex), ct);
            throw MapProviderError(ex);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            await _audit.RecordAsync("ticket.creation_failed", "ticket",
                reusableFailed?.Id.ToString(), projectId, SafeFailureMeta(defect, integration, null), ct);
            throw new JiraProviderTimeoutLike(new JiraProviderException(JiraErrorKind.Cancelled, "Jira ticket creation was cancelled."), ex);
        }

        if (string.IsNullOrWhiteSpace(result.ExternalId) || string.IsNullOrWhiteSpace(result.ExternalKey))
        {
            await PersistFailureAsync(defect, integration, reusableFailed, "Jira returned an invalid creation response.", ct);
            await _audit.RecordAsync("ticket.creation_failed", "ticket",
                reusableFailed?.Id.ToString(), projectId, SafeFailureMeta(defect, integration, null), ct);
            throw new InvalidOperationException("Jira returned an invalid creation response.");
        }
        if (!JiraUrlValidator.IsSafeExternalTicketUrl(result.ExternalUrl))
        {
            await PersistFailureAsync(defect, integration, reusableFailed, "Jira returned an unsafe ticket URL.", ct);
            await _audit.RecordAsync("ticket.creation_failed", "ticket",
                reusableFailed?.Id.ToString(), projectId, SafeFailureMeta(defect, integration, null), ct);
            throw new InvalidOperationException("Jira returned an unsafe ticket URL.");
        }

        var now = _clock.UtcNow;
        var actor = await ResolveAppUserIdAsync(ct);
        Ticket row;
        if (reusableFailed is not null)
        {
            row = reusableFailed;
            row.ExternalTicketId = result.ExternalId;
            row.ExternalKey = result.ExternalKey;
            row.ExternalUrl = result.ExternalUrl;
            row.Title = request.Summary;
            row.SyncStatus = TicketSyncStatus.Synced;
            row.LastError = null;
            row.UpdatedAt = now;
        }
        else
        {
            row = new Ticket
            {
                ProjectId = projectId,
                DefectId = defect.Id,
                IntegrationId = integration.Id,
                Provider = JiraIntegrationConfig.ProviderName,
                ExternalTicketId = result.ExternalId,
                ExternalKey = result.ExternalKey,
                ExternalUrl = result.ExternalUrl,
                Title = request.Summary,
                Status = "created",
                SyncStatus = TicketSyncStatus.Synced,
                CreatedBy = actor,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _tickets.AddAsync(row, ct);
        }

        try
        {
            await _tickets.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Concurrent winner: return the existing synced ticket (idempotent).
            _logger.LogInformation("Concurrent Jira ticket creation for defect {DefectId}; returning existing.", defect.Id);
            var winner = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
            if (winner is not null)
                return Map(winner, alreadyExisted: true);
            throw new ConflictException("A Jira ticket already exists for this defect.");
        }

        // Log identifiers only — never secrets or provider bodies.
        _logger.LogInformation("Jira ticket {Key} created for defect {DefectId}.", result.ExternalKey, defect.Id);
        await _audit.RecordAsync("ticket.created", "ticket", row.Id.ToString(), projectId,
            SafeSuccessMeta(defect, integration, row), ct);
        return Map(row, alreadyExisted: false);
    }

    // ---------- helpers ----------

    private async Task<Defect> LoadAuthorizedDefectAsync(Guid projectId, Guid defectId, string permission, CancellationToken ct)
    {
        var defect = await _defects.GetByIdAsync(defectId, ct);
        await _authorization.RequireProjectAccessAsync(defect?.ProjectId ?? defectId, permission, ct);
        if (defect is null)
            throw new Common.NotFoundException("Defect not found.");
        if (defect.ProjectId != projectId)
            throw new ForbiddenException("The caller has no access to this project.");
        return defect;
    }

    private async Task<(DomainFailureAnalysis? Analysis, JiraTicketContentBuilder.TestInfo? Test)> LoadContextAsync(Defect defect, CancellationToken ct)
    {
        DomainFailureAnalysis? analysis = null;
        if (defect.FailureAnalysisId is not null)
            analysis = await _executions.GetAnalysisByIdAsync(defect.FailureAnalysisId.Value, ct);
        JiraTicketContentBuilder.TestInfo? test = null;
        if (defect.ExecutionTestId is not null)
        {
            var row = await _executions.GetExecutionTestByIdAsync(defect.ExecutionTestId.Value, ct);
            if (row is not null)
            {
                string? key = null, title = null;
                int? version = null;
                var tc = await _cases.GetByIdAsync(row.TestCaseId, ct);
                key = tc?.TestKey;
                title = tc?.Title;
                if (row.TestCaseVersionId is not null)
                    version = (await _cases.GetVersionByIdAsync(row.TestCaseVersionId.Value, ct))?.VersionNumber;
                test = new JiraTicketContentBuilder.TestInfo(key, title, version, row.ExecutionId);
            }
        }
        return (analysis, test);
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken ct)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, ct);

    private async Task PersistFailureAsync(Defect defect, Integration integration, Ticket? reusable, string error, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (reusable is not null)
        {
            reusable.LastError = Truncate(error);
            reusable.SyncStatus = TicketSyncStatus.Failed;
            reusable.UpdatedAt = now;
        }
        else
        {
            await _tickets.AddAsync(new Ticket
            {
                ProjectId = defect.ProjectId,
                DefectId = defect.Id,
                IntegrationId = integration.Id,
                Provider = JiraIntegrationConfig.ProviderName,
                Title = JiraTicketContentBuilder.BuildSummary(defect),
                Status = "failed",
                SyncStatus = TicketSyncStatus.Failed,
                LastError = Truncate(error),
                CreatedBy = await ResolveAppUserIdAsync(ct),
                CreatedAt = now,
                UpdatedAt = now,
            }, ct);
        }
        try
        {
            await _tickets.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Failure bookkeeping is best-effort; the provider error still surfaces.
            _logger.LogWarning(ex, "Failed to persist Jira failure state for defect {DefectId}.", defect.Id);
        }
    }

    private static string FriendlyJiraError(JiraProviderException ex) => ex.Kind switch
    {
        JiraErrorKind.Validation => "Jira rejected the ticket details. Review the integration configuration.",
        JiraErrorKind.Authentication => "Jira rejected the configured credentials. Check the integration secret.",
        JiraErrorKind.Permission => "Jira denied the request. The configured account lacks permission.",
        JiraErrorKind.NotFound => "The Jira project or endpoint could not be found.",
        JiraErrorKind.RateLimited => "Jira rate-limited the request. Please try again shortly.",
        JiraErrorKind.Timeout => "Jira did not respond in time. The ticket state is ambiguous; check Jira before retrying.",
        _ => "Jira is currently unavailable. Please try again later.",
    };

    private static Exception MapProviderError(JiraProviderException ex) => ex.Kind switch
    {
        JiraErrorKind.Validation => new ValidationException(ex.Message, new[] { new FieldError("jira", ex.Message) }),
        JiraErrorKind.Authentication => new InvalidOperationException("Jira rejected the configured credentials. Check the integration secret."),
        JiraErrorKind.Permission => new InvalidOperationException("Jira denied the request. The configured account lacks permission."),
        JiraErrorKind.NotFound => new Common.NotFoundException("The Jira project or endpoint could not be found."),
        JiraErrorKind.RateLimited => new RateLimitedException(ex.Message),
        JiraErrorKind.Timeout => new InvalidOperationException("Jira did not respond in time. The ticket state is ambiguous; check Jira before retrying.", ex),
        _ => new InvalidOperationException("Jira is currently unavailable. Please try again later.", ex),
    };

    private static bool IsUniqueViolation(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateException", StringComparison.Ordinal))
            return true;
        if (ex.InnerException is not null && IsUniqueViolation(ex.InnerException))
            return true;
        return ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeMeta(Defect defect, Integration integration)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            defectId = defect.Id,
            integrationId = integration.Id,
            provider = JiraIntegrationConfig.ProviderName,
        }));

    private static string SafeSuccessMeta(Defect defect, Integration integration, Ticket ticket)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            defectId = defect.Id,
            integrationId = integration.Id,
            ticketId = ticket.Id,
            provider = JiraIntegrationConfig.ProviderName,
            externalKey = ticket.ExternalKey,
            syncStatus = ticket.SyncStatus.ToString(),
        }));

    private static string SafeFailureMeta(Defect defect, Integration integration, JiraProviderException? ex)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            defectId = defect.Id,
            integrationId = integration.Id,
            provider = JiraIntegrationConfig.ProviderName,
            kind = ex?.Kind.ToString() ?? "unknown",
        }));

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..499] + "…";

    private static TicketDto Map(Ticket t, bool alreadyExisted) => new(
        t.Id, t.ProjectId, t.DefectId, t.IntegrationId, t.Provider,
        t.ExternalTicketId, t.ExternalKey, t.ExternalUrl, t.Title,
        t.SyncStatus.ToString(), t.CreatedBy, t.CreatedAt, t.UpdatedAt, alreadyExisted);

    /// <summary>Wraps cancellation so the API layer maps it to 503 without leaking internals.</summary>
    private sealed class JiraProviderTimeoutLike : InvalidOperationException
    {
        public JiraProviderException ProviderError { get; }
        public JiraProviderTimeoutLike(JiraProviderException error, Exception inner)
            : base(error.Message, inner) => ProviderError = error;
    }
}

/// <summary>Upstream rate limit. Maps to 429 RATE_LIMITED.</summary>
public sealed class RateLimitedException : Exception
{
    public RateLimitedException(string message) : base(message) { }
}
