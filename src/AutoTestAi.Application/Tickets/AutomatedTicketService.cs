using System.Collections.Concurrent;
using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Policy-controlled automatic Jira ticket creation (Phase 2 Slice 10).
/// Reuses the Slice 7 Jira provider abstraction, content builder, severity
/// mapper, URL validator and the Ticket idempotency constraint
/// (DefectId + IntegrationId, Synced). The internal defect remains the
/// system of record; automation only adds an external ticket reference.
/// System operation: project scope is validated against the defect row.
/// </summary>
public sealed class AutomatedTicketService : IAutomatedTicketService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private readonly ITicketStore _tickets;
    private readonly IAutoTicketQueryStore _query;
    private readonly IAutoTicketPolicyStore _policies;
    private readonly IIntegrationStore _integrations;
    private readonly IDefectStore _defects;
    private readonly IExecutionStore _executions;
    private readonly ITestCaseStore _cases;
    private readonly IJiraTicketProvider _jira;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly IAuthorizationService _authorization;
    private readonly AutoTicketQueue _queue;
    private readonly ILogger<AutomatedTicketService> _logger;
    private readonly IOptions<TicketOptions> _options;
    private readonly IOptions<AutoTicketOptions> _autoOptions;

    public AutomatedTicketService(
        ITicketStore tickets,
        IAutoTicketQueryStore query,
        IAutoTicketPolicyStore policies,
        IIntegrationStore integrations,
        IDefectStore defects,
        IExecutionStore executions,
        ITestCaseStore cases,
        IJiraTicketProvider jira,
        IDateTimeProvider clock,
        IAuditService audit,
        IAuthorizationService authorization,
        AutoTicketQueue queue,
        ILogger<AutomatedTicketService> logger,
        IOptions<TicketOptions> options,
        IOptions<AutoTicketOptions> autoOptions)
    {
        _tickets = tickets;
        _query = query;
        _policies = policies;
        _integrations = integrations;
        _defects = defects;
        _executions = executions;
        _cases = cases;
        _jira = jira;
        _clock = clock;
        _audit = audit;
        _authorization = authorization;
        _queue = queue;
        _logger = logger;
        _options = options;
        _autoOptions = autoOptions;
    }

    public async Task<AutoTicketRequestResult> RequestAutomationAsync(Guid projectId, Guid defectId, CancellationToken ct)
    {
        // System operation: scope comes from the defect row, never the caller.
        var defect = await _defects.GetByIdAsync(defectId, ct);
        if (defect is null || defect.ProjectId != projectId)
        {
            _logger.LogWarning("Auto-ticket skipped: defect {DefectId} is not in project {ProjectId}.", defectId, projectId);
            return new AutoTicketRequestResult("skipped_cross_project");
        }

        var policy = await _policies.GetByProjectAsync(projectId, ct);
        if (policy is null)
        {
            await AuditSkippedAsync(defect, "no_policy", ct);
            return new AutoTicketRequestResult("skipped_no_policy");
        }

        var (eligible, reason) = AutoTicketEvaluator.Evaluate(defect, policy);
        if (!eligible)
        {
            await AuditSkippedAsync(defect, reason, ct);
            _logger.LogInformation("Auto-ticket skipped for defect {DefectId}: {Reason}.", defect.Id, reason);
            return new AutoTicketRequestResult($"skipped_{reason}");
        }

        var integration = await ResolveIntegrationAsync(projectId, policy, ct);
        if (integration is null)
        {
            await PersistConfigFailureAsync(defect, policy, "The Jira integration is not configured for automatic ticketing.", ct);
            return new AutoTicketRequestResult("failed_no_integration");
        }
        if (integration.Status != IntegrationStatus.Active)
        {
            await PersistConfigFailureAsync(defect, policy, "The Jira integration is disabled for this project.", ct);
            return new AutoTicketRequestResult("failed_integration_disabled");
        }
        var config = JiraIntegrationConfig.FromJson(integration.Configuration);
        if (string.IsNullOrWhiteSpace(config.BaseUrl) ||
            string.IsNullOrWhiteSpace(config.ProjectKey) ||
            string.IsNullOrWhiteSpace(config.Email) ||
            string.IsNullOrWhiteSpace(integration.SecretReference))
        {
            await PersistConfigFailureAsync(defect, policy, "The Jira integration is not fully configured.", ct);
            return new AutoTicketRequestResult("failed_integration_incomplete");
        }

        // Idempotency first: an existing synced ticket (manual or automatic)
        // satisfies every trigger.
        var synced = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
        if (synced is not null)
        {
            await AuditSkippedAsync(defect, "already_synced", ct, synced.Id, integration.Id);
            return new AutoTicketRequestResult("skipped_already_synced", synced.Id);
        }

        var gate = Locks.GetOrAdd(defect.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check inside the claim: a concurrent trigger may have won.
            synced = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
            if (synced is not null)
                return new AutoTicketRequestResult("skipped_already_synced", synced.Id);

            var latest = await _tickets.FindLatestForDefectAsync(defect.Id, integration.Id, ct);
            if (latest is not null && latest.SyncStatus == TicketSyncStatus.Pending)
            {
                await AuditSkippedAsync(defect, "already_pending", ct, latest.Id, integration.Id);
                return new AutoTicketRequestResult("skipped_already_pending", latest.Id);
            }
            if (latest is not null && latest.SyncStatus == TicketSyncStatus.Failed &&
                latest.Origin == TicketOrigin.Automatic &&
                latest.NextAttemptAt is not null && latest.NextAttemptAt.Value > _clock.UtcNow)
            {
                await AuditSkippedAsync(defect, "awaiting_retry", ct, latest.Id, integration.Id);
                return new AutoTicketRequestResult("skipped_awaiting_retry", latest.Id);
            }

            var now = _clock.UtcNow;
            Ticket intent;
            if (latest is not null && latest.SyncStatus == TicketSyncStatus.Failed)
            {
                intent = latest;
                intent.SyncStatus = TicketSyncStatus.Pending;
                intent.Origin = TicketOrigin.Automatic;
                intent.LastError = null;
                intent.NextAttemptAt = null;
                intent.UpdatedAt = now;
            }
            else
            {
                intent = new Ticket
                {
                    ProjectId = projectId,
                    DefectId = defect.Id,
                    IntegrationId = integration.Id,
                    Provider = JiraIntegrationConfig.ProviderName,
                    Title = JiraTicketContentBuilder.BuildSummary(defect),
                    Status = "pending",
                    SyncStatus = TicketSyncStatus.Pending,
                    Origin = TicketOrigin.Automatic,
                    AttemptCount = 0,
                    CreatedBy = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await _tickets.AddAsync(intent, ct);
            }

            try
            {
                await _tickets.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // Concurrent winner synced between our checks: converge on it.
                _logger.LogInformation("Concurrent auto-ticket intent for defect {DefectId}; converging on winner.", defect.Id);
                var winner = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
                if (winner is not null)
                    return new AutoTicketRequestResult("skipped_already_synced", winner.Id);
                var pendingWinner = await _tickets.FindLatestForDefectAsync(defect.Id, integration.Id, ct);
                return new AutoTicketRequestResult("skipped_already_pending", pendingWinner?.Id);
            }

            await _audit.RecordAsync("ticket.automation.requested", "ticket", intent.Id.ToString(), projectId,
                SafeMeta(defect, integration, "automatic"), ct);
            _logger.LogInformation("Auto-ticket queued for defect {DefectId} (ticket {TicketId}).", defect.Id, intent.Id);

            // Asynchronous handoff: the HTTP/defect path returns here. The
            // background executor performs the Jira call.
            _queue.Enqueue(new AutoTicketWorkItem(intent.Id));
            return new AutoTicketRequestResult("queued", intent.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Executes one persisted automatic intent under a database-backed claim.
    /// State machine: Pending(unclaimed) → Pending(claimed) → Synced
    /// (terminal, claim cleared) | Failed(retryable: claim released,
    /// NextAttemptAt set) | Failed(permanent: claim released, no schedule).
    /// Synced never regresses. Only the current claimant's writes land:
    /// concurrent or stale claimants converge or abort. The in-process gate
    /// below is a local fast-path only; correctness comes from the persisted
    /// claim plus optimistic concurrency, so this is safe across API
    /// instances. Re-evaluates the current policy: retries and
    /// reconciliation never bypass it.
    /// </summary>
    public async Task<TicketDto?> ExecutePendingAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await _query.GetTicketByIdAsync(ticketId, ct);
        if (ticket is null)
            return null;
        if (ticket.SyncStatus == TicketSyncStatus.Synced)
            return Map(ticket, true);
        if (ticket.DefectId is null || ticket.IntegrationId is null)
        {
            await MarkFailedAsync(ticket, "Automatic ticketing requires a defect-scoped ticket.", null, null, ct);
            return null;
        }

        // Fast path before the local gate: an unexpired foreign claim means
        // another instance owns this intent right now — never wait on it,
        // never execute alongside it.
        if (IsClaimedByOther(ticket, _clock.UtcNow))
        {
            _logger.LogInformation("Auto-ticket {TicketId} is claimed by another worker; skipping.", ticket.Id);
            return null;
        }

        var gate = Locks.GetOrAdd(ticket.DefectId.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Reload inside the gate and converge on concurrent winners.
            ticket = await _query.GetTicketByIdAsync(ticketId, ct);
            if (ticket is null || ticket.SyncStatus == TicketSyncStatus.Synced)
                return ticket is null ? null : Map(ticket, true);
            if (ticket.DefectId is null || ticket.IntegrationId is null)
            {
                await MarkFailedAsync(ticket, "Automatic ticketing requires a defect-scoped ticket.", null, null, ct);
                return null;
            }
            var now = _clock.UtcNow;
            if (IsClaimedByOther(ticket, now))
            {
                _logger.LogInformation("Auto-ticket {TicketId} is claimed by another worker; skipping.", ticket.Id);
                return null;
            }
            var defectId = ticket.DefectId.Value;
            var integrationId = ticket.IntegrationId.Value;

            var defect = await _defects.GetByIdAsync(defectId, ct);
            if (defect is null || defect.ProjectId != ticket.ProjectId)
            {
                await MarkFailedAsync(ticket, "The defect is no longer in this project.", null, null, ct);
                return null;
            }

            // Policy is re-evaluated against current state: a policy that was
            // disabled (or a defect that left eligibility) after the intent
            // was queued must not produce a Jira issue.
            var policy = await _policies.GetByProjectAsync(ticket.ProjectId, ct);
            var (eligible, reason) = policy is null
                ? (false, "no_policy")
                : AutoTicketEvaluator.Evaluate(defect, policy);
            if (!eligible)
            {
                await MarkFailedAsync(
                    ticket, $"Automatic ticketing is no longer eligible under the project policy ({reason}).",
                    null, null, ct);
                await AuditSkippedAsync(defect, $"policy_{reason}", ct, ticket.Id, ticket.IntegrationId);
                return null;
            }

            if (ticket.AttemptCount >= AutoTicketRetryPolicy.MaxAttempts)
            {
                await MarkFailedAsync(ticket, "Maximum automatic ticket attempts reached.", null, null, ct);
                return null;
            }
            if (ticket.SyncStatus == TicketSyncStatus.Failed &&
                ticket.NextAttemptAt is not null && ticket.NextAttemptAt.Value > now)
            {
                return null; // Not due yet; reconciliation will pick it up later.
            }

            var winner = await _tickets.FindSyncedAsync(defect.Id, integrationId, ct);
            if (winner is not null && winner.Id != ticket.Id)
            {
                // A manual or concurrent automatic ticket won: retire this intent.
                await MarkFailedAsync(ticket, "A Jira ticket already exists for this defect.", null, null, ct, retire: true);
                await _audit.RecordAsync("ticket.automation.skipped", "ticket", ticket.Id.ToString(), ticket.ProjectId,
                    SafeMeta(defect, null, "duplicate_superseded"), ct);
                return Map(winner, true);
            }

            var integration = await _integrations.GetByIdAsync(integrationId, ct);
            if (integration is null || integration.ProjectId != ticket.ProjectId ||
                integration.Status != IntegrationStatus.Active)
            {
                await MarkFailedAsync(ticket, "The Jira integration is unavailable for automatic ticketing.", null, null, ct);
                return null;
            }
            var config = JiraIntegrationConfig.FromJson(integration.Configuration);
            if (string.IsNullOrWhiteSpace(config.BaseUrl) ||
                string.IsNullOrWhiteSpace(config.ProjectKey) ||
                string.IsNullOrWhiteSpace(config.Email) ||
                string.IsNullOrWhiteSpace(integration.SecretReference))
            {
                await MarkFailedAsync(ticket, "The Jira integration is not fully configured.", null, null, ct);
                return null;
            }

            // Claim acquisition (compare-and-set): take the lease only when
            // unclaimed or expired. A concurrent claimant colliding here loses
            // via optimistic concurrency and converges below.
            var previousToken = ticket.ClaimToken;
            var ambiguousRecovery = previousToken is not null &&
                ticket.AttemptCount > 0 &&
                ticket.SyncStatus == TicketSyncStatus.Pending &&
                ticket.LastError is null;
            var myToken = Guid.NewGuid();
            ticket.ClaimToken = myToken;
            ticket.ClaimExpiresAt = now.Add(ClaimLease());
            ticket.AttemptCount++;
            ticket.SyncStatus = TicketSyncStatus.Pending;
            ticket.UpdatedAt = now;
            try
            {
                await _query.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex))
            {
                _logger.LogInformation("Auto-ticket {TicketId} claim lost to a concurrent worker; converging.", ticket.Id);
                var current = await _query.GetTicketSnapshotAsync(ticketId, ct);
                if (current?.SyncStatus == TicketSyncStatus.Synced)
                {
                    var syncedWinner = current.DefectId is not null
                        ? await _tickets.FindSyncedAsync(current.DefectId.Value, integrationId, ct)
                        : null;
                    if (syncedWinner is not null)
                        return Map(syncedWinner, true);
                }
                return null;
            }

            if (ambiguousRecovery)
            {
                // The previous claimant started an attempt but never recorded
                // an outcome (crash or timeout): Jira may already hold the
                // issue, so this retry may duplicate it externally. Proceed
                // (bounded) but say so explicitly.
                _logger.LogWarning("Auto-ticket {TicketId} recovered after an incomplete attempt; Jira state is ambiguous.", ticket.Id);
                await _audit.RecordAsync("ticket.automation.recovered", "ticket", ticket.Id.ToString(), ticket.ProjectId,
                    SafeRecoveryMeta(defect, ticket), ct);
            }

            var (analysis, testInfo) = await LoadContextAsync(defect, ct);
            var appBase = (_options.Value.AppBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            string? defectRef = !string.IsNullOrWhiteSpace(appBase)
                ? $"{appBase}/projects/{defect.ProjectId}/bugs/{defect.Id}"
                : null;
            var request = new JiraCreateRequest(
                JiraIntegrationConfig.NormalizeBaseUrl(config.BaseUrl),
                JiraIntegrationConfig.NormalizeProjectKey(config.ProjectKey),
                string.IsNullOrWhiteSpace(config.IssueType) ? JiraIntegrationConfig.DefaultIssueType : config.IssueType.Trim(),
                JiraTicketContentBuilder.BuildSummary(defect),
                JiraTicketContentBuilder.BuildDescription(defect, config, analysis, testInfo, defectRef),
                JiraSeverityMapper.Map(defect.Severity, config.PriorityMapping));

            _logger.LogInformation("Auto-ticket attempt {Attempt} for defect {DefectId}.", ticket.AttemptCount, defect.Id);
            JiraCreateResult result;
            try
            {
                result = await _jira.CreateIssueAsync(request, config.Email.Trim(), integration.SecretReference, ct);
            }
            catch (JiraProviderException ex)
            {
                await HandleProviderFailureAsync(ticket, defect, ex, myToken, ct);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await MarkFailedAsync(ticket, "Automatic ticket creation was cancelled.", JiraErrorKind.Cancelled, myToken, ct);
                return null;
            }

            if (string.IsNullOrWhiteSpace(result.ExternalId) || string.IsNullOrWhiteSpace(result.ExternalKey) ||
                !JiraUrlValidator.IsSafeExternalTicketUrl(result.ExternalUrl))
            {
                await MarkFailedAsync(ticket, "Jira returned an invalid creation response.", JiraErrorKind.MalformedResponse, myToken, ct);
                return null;
            }

            // Finish guarded by live ownership: the claim must still be ours
            // AND unexpired. A stale claimant (crashed, timed out, reclaimed
            // by someone else) must never overwrite current state.
            if (!await StillOwnsClaimAsync(ticket.Id, myToken, ct))
            {
                _logger.LogWarning("Auto-ticket {TicketId} claim lost during Jira call; not persisting result (Jira state ambiguous, check Jira before retrying).", ticket.Id);
                await _audit.RecordAsync("ticket.automation.superseded", "ticket", ticket.Id.ToString(), ticket.ProjectId,
                    SafeMeta(defect, integration, "stale_claimant"), ct);
                return null;
            }

            ticket.ExternalTicketId = result.ExternalId;
            ticket.ExternalKey = result.ExternalKey;
            ticket.ExternalUrl = result.ExternalUrl;
            ticket.Title = request.Summary;
            ticket.Status = "created";
            ticket.SyncStatus = TicketSyncStatus.Synced;
            ticket.Origin = TicketOrigin.Automatic;
            ticket.LastError = null;
            ticket.NextAttemptAt = null;
            ticket.ClaimToken = null;
            ticket.ClaimExpiresAt = null;
            ticket.UpdatedAt = _clock.UtcNow;
            try
            {
                await _query.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                _logger.LogInformation("Concurrent auto-ticket success for defect {DefectId}; returning winner.", defect.Id);
                var syncedWinner = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
                if (syncedWinner is not null)
                    return Map(syncedWinner, true);
                throw new ConflictException("A Jira ticket already exists for this defect.");
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex))
            {
                _logger.LogWarning("Auto-ticket {TicketId} lost its claim during Jira call; not persisting result.", ticket.Id);
                await _audit.RecordAsync("ticket.automation.superseded", "ticket", ticket.Id.ToString(), ticket.ProjectId,
                    SafeMeta(defect, integration, "stale_claimant"), ct);
                return null;
            }

            _logger.LogInformation("Auto-ticket {Key} created for defect {DefectId}.", result.ExternalKey, defect.Id);
            await _audit.RecordAsync("ticket.automation.created", "ticket", ticket.Id.ToString(), ticket.ProjectId,
                SafeSuccessMeta(defect, integration, ticket), ct);
            return Map(ticket, false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TicketDto> RetryFailedAsync(Guid projectId, Guid defectId, CancellationToken ct)
    {
        var defect = await _defects.GetByIdAsync(defectId, ct);
        await _authorization.RequireProjectAccessAsync(defect?.ProjectId ?? defectId, Permissions.TicketsCreate, ct);
        if (defect is null)
            throw new Common.NotFoundException("Defect not found.");
        if (defect.ProjectId != projectId)
            throw new ForbiddenException("The caller has no access to this project.");

        var integration = await _integrations.FindByProjectAndProviderAsync(projectId, JiraIntegrationConfig.ProviderName, ct);
        if (integration is null)
            throw new ConflictException("Jira is not configured for this project.");

        var synced = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
        if (synced is not null)
            return Map(synced, true);

        // Operator retry is explicit, but it still honors the current policy:
        // a disabled or non-matching policy must not produce a Jira issue.
        var policy = await _policies.GetByProjectAsync(projectId, ct);
        var (eligible, reason) = policy is null
            ? (false, "no_policy")
            : AutoTicketEvaluator.Evaluate(defect, policy);
        if (!eligible)
            throw new ConflictException(
                $"Automatic ticketing is not currently eligible for this defect under the project policy ({reason}).");

        var latest = await _tickets.FindLatestForDefectAsync(defect.Id, integration.Id, ct);
        if (latest is null || latest.SyncStatus == TicketSyncStatus.Synced)
            throw new ConflictException("There is no failed automatic ticket to retry for this defect.");
        if (latest.SyncStatus == TicketSyncStatus.Pending)
            return Map(latest, true);

        latest.SyncStatus = TicketSyncStatus.Pending;
        latest.NextAttemptAt = null;
        latest.ClaimToken = null;
        latest.ClaimExpiresAt = null;
        latest.UpdatedAt = _clock.UtcNow;
        try
        {
            await _tickets.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            var current = await _tickets.FindSyncedAsync(defect.Id, integration.Id, ct);
            if (current is not null)
                return Map(current, true);
            throw new ConflictException("The automatic ticket changed concurrently. Please retry.");
        }
        await _audit.RecordAsync("ticket.automation.retry_scheduled", "ticket", latest.Id.ToString(), projectId,
            SafeMeta(defect, integration, "manual_retry"), ct);
        _queue.Enqueue(new AutoTicketWorkItem(latest.Id));
        return Map(latest, true);
    }

    // ---------- helpers ----------

    private TimeSpan ClaimLease()
        => TimeSpan.FromSeconds(Math.Clamp(_autoOptions.Value.ClaimLeaseSeconds, 30, 3600));

    private static bool IsClaimedByOther(Ticket ticket, DateTimeOffset now)
        => ticket.ClaimToken is not null &&
            ticket.ClaimExpiresAt is not null &&
            ticket.ClaimExpiresAt.Value > now;

    /// <summary>
    /// Live ownership check against a fresh snapshot: the claim must still
    /// be ours and unexpired. Guards every write that follows a Jira call.
    /// </summary>
    private async Task<bool> StillOwnsClaimAsync(Guid ticketId, Guid myToken, CancellationToken ct)
    {
        var snapshot = await _query.GetTicketSnapshotAsync(ticketId, ct);
        return snapshot is not null &&
            snapshot.ClaimToken == myToken &&
            snapshot.ClaimExpiresAt is not null &&
            snapshot.ClaimExpiresAt.Value > _clock.UtcNow;
    }

    private async Task<Integration?> ResolveIntegrationAsync(Guid projectId, AutoTicketPolicy policy, CancellationToken ct)
    {
        if (policy.IntegrationId is not null)
        {
            var pinned = await _integrations.GetByIdAsync(policy.IntegrationId.Value, ct);
            // Cross-project pins are rejected at configuration time; a row that
            // drifted out of scope must never ticket another project.
            if (pinned is null || pinned.ProjectId != projectId)
                return null;
            return pinned;
        }
        return await _integrations.FindByProjectAndProviderAsync(projectId, JiraIntegrationConfig.ProviderName, ct);
    }

    private async Task PersistConfigFailureAsync(Defect defect, AutoTicketPolicy policy, string error, CancellationToken ct)
    {
        var integration = await ResolveIntegrationAsync(defect.ProjectId, policy, ct);
        var now = _clock.UtcNow;
        Ticket row;
        if (integration is not null)
        {
            var latest = await _tickets.FindLatestForDefectAsync(defect.Id, integration.Id, ct);
            if (latest is not null && latest.SyncStatus == TicketSyncStatus.Failed)
            {
                row = latest;
                row.LastError = Truncate(error);
                row.Origin = TicketOrigin.Automatic;
                row.UpdatedAt = now;
                await _tickets.SaveChangesAsync(ct);
                await AuditFailedAsync(defect, integration, row, "configuration", ct);
                return;
            }
            row = NewFailedRow(defect, integration.Id, error, now);
        }
        else
        {
            // No integration to scope the row to: record the failure in audit
            // only. The defect itself is untouched.
            await AuditFailedAsync(defect, null, null, "configuration", ct);
            return;
        }
        await _tickets.AddAsync(row, ct);
        try { await _tickets.SaveChangesAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist auto-ticket configuration failure for defect {DefectId}.", defect.Id);
        }
        await AuditFailedAsync(defect, integration, row, "configuration", ct);
    }

    private Ticket NewFailedRow(Defect defect, Guid integrationId, string error, DateTimeOffset now) => new()
    {
        ProjectId = defect.ProjectId,
        DefectId = defect.Id,
        IntegrationId = integrationId,
        Provider = JiraIntegrationConfig.ProviderName,
        Title = JiraTicketContentBuilder.BuildSummary(defect),
        Status = "failed",
        SyncStatus = TicketSyncStatus.Failed,
        Origin = TicketOrigin.Automatic,
        AttemptCount = 0,
        LastError = Truncate(error),
        NextAttemptAt = null,
        CreatedBy = null,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private async Task HandleProviderFailureAsync(
        Ticket ticket, Defect defect, JiraProviderException ex, Guid myToken, CancellationToken ct)
    {
        // Never record a failure over someone else's newer claim.
        if (!await StillOwnsClaimAsync(ticket.Id, myToken, ct))
        {
            _logger.LogWarning("Auto-ticket {TicketId} claim lost during Jira call; failure result discarded.", ticket.Id);
            await _audit.RecordAsync("ticket.automation.superseded", "ticket", ticket.Id.ToString(), ticket.ProjectId,
                SafeMeta(defect, null, "stale_claimant"), ct);
            return;
        }

        var retryable = AutoTicketRetryPolicy.IsRetryable(ex.Kind) &&
            ticket.AttemptCount < AutoTicketRetryPolicy.MaxAttempts;
        var now = _clock.UtcNow;
        ticket.SyncStatus = TicketSyncStatus.Failed;
        ticket.LastError = Truncate(AutoTicketRetryPolicy.FriendlyError(ex));
        ticket.NextAttemptAt = retryable
            ? now.Add(AutoTicketRetryPolicy.DelayForAttempt(ex.Kind, ticket.AttemptCount, ex.RetryAfter))
            : null;
        // Release the lease so reconciliation or another worker can proceed
        // when (and only when, via NextAttemptAt) a retry is due.
        ticket.ClaimToken = null;
        ticket.ClaimExpiresAt = null;
        ticket.UpdatedAt = now;
        try { await _query.SaveChangesAsync(ct); }
        catch (Exception persistEx) when (IsConcurrencyConflict(persistEx))
        {
            _logger.LogWarning(persistEx, "Auto-ticket {TicketId} claim lost while recording failure.", ticket.Id);
            return;
        }
        catch (Exception persistEx)
        {
            _logger.LogWarning(persistEx, "Failed to persist auto-ticket failure for defect {DefectId}.", defect.Id);
        }

        _logger.LogWarning("Auto-ticket attempt for defect {DefectId} failed ({Kind}, retryable={Retryable}).",
            defect.Id, ex.Kind, retryable);
        var integration = ticket.IntegrationId is null
            ? null
            : await _integrations.GetByIdAsync(ticket.IntegrationId.Value, ct);
        await AuditFailedAsync(defect, integration, ticket, ex.Kind.ToString(), ct);
        if (retryable)
            await _audit.RecordAsync("ticket.automation.retry_scheduled", "ticket", ticket.Id.ToString(), ticket.ProjectId,
                SafeRetryMeta(defect, ticket), ct);
    }

    private async Task MarkFailedAsync(
        Ticket ticket, string error, JiraErrorKind? kind, Guid? myToken, CancellationToken ct, bool retire = false)
    {
        // Post-claim writes must not clobber a newer claimant.
        if (myToken is not null && !await StillOwnsClaimAsync(ticket.Id, myToken.Value, ct))
        {
            _logger.LogWarning("Auto-ticket {TicketId} claim lost; failure result discarded.", ticket.Id);
            return;
        }
        ticket.SyncStatus = TicketSyncStatus.Failed;
        ticket.LastError = Truncate(error);
        // Retired intents (superseded by a winner) and permanent failures
        // carry no retry schedule.
        ticket.NextAttemptAt = null;
        ticket.ClaimToken = null;
        ticket.ClaimExpiresAt = null;
        ticket.UpdatedAt = _clock.UtcNow;
        if (retire && string.IsNullOrWhiteSpace(ticket.ExternalKey))
            ticket.Status = "superseded";
        try { await _query.SaveChangesAsync(ct); }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            _logger.LogWarning(ex, "Auto-ticket {TicketId} changed concurrently; failure result discarded.", ticket.Id);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist auto-ticket failure state for ticket {TicketId}.", ticket.Id);
        }
        Defect? defect = ticket.DefectId is null ? null : await _defects.GetByIdAsync(ticket.DefectId.Value, ct);
        Integration? integration = ticket.IntegrationId is null ? null : await _integrations.GetByIdAsync(ticket.IntegrationId.Value, ct);
        if (defect is not null)
            await AuditFailedAsync(defect, integration, ticket, kind?.ToString() ?? "permanent", ct);
    }

    private async Task<(Domain.Entities.FailureAnalysis? Analysis, JiraTicketContentBuilder.TestInfo? Test)> LoadContextAsync(
        Defect defect, CancellationToken ct)
    {
        Domain.Entities.FailureAnalysis? analysis = null;
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

    private async Task AuditSkippedAsync(Defect defect, string reason, CancellationToken ct, Guid? ticketId = null, Guid? integrationId = null)
        => await _audit.RecordAsync("ticket.automation.skipped", "ticket",
            ticketId?.ToString(), defect.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                defectId = defect.Id,
                integrationId,
                provider = JiraIntegrationConfig.ProviderName,
                origin = "automatic",
                reason,
            })), ct);

    private async Task AuditFailedAsync(Defect defect, Integration? integration, Ticket? ticket, string kind, CancellationToken ct)
        => await _audit.RecordAsync("ticket.automation.failed", "ticket",
            ticket?.Id.ToString(), defect.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                defectId = defect.Id,
                integrationId = integration?.Id ?? ticket?.IntegrationId,
                provider = JiraIntegrationConfig.ProviderName,
                origin = "automatic",
                kind,
            })), ct);

    private static string SafeMeta(Defect defect, Integration? integration, string origin)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            defectId = defect.Id,
            integrationId = integration?.Id,
            provider = JiraIntegrationConfig.ProviderName,
            origin,
        }));

    private static string SafeSuccessMeta(Defect defect, Integration integration, Ticket ticket)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            defectId = defect.Id,
            integrationId = integration.Id,
            ticketId = ticket.Id,
            provider = JiraIntegrationConfig.ProviderName,
            externalKey = ticket.ExternalKey,
            origin = "automatic",
            syncStatus = ticket.SyncStatus.ToString(),
        }));

    private static string SafeRetryMeta(Defect defect, Ticket ticket)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            defectId = defect.Id,
            ticketId = ticket.Id,
            provider = JiraIntegrationConfig.ProviderName,
            origin = "automatic",
            attemptCount = ticket.AttemptCount,
            nextAttemptAt = ticket.NextAttemptAt,
        }));

    private static string SafeRecoveryMeta(Defect defect, Ticket ticket)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            defectId = defect.Id,
            ticketId = ticket.Id,
            provider = JiraIntegrationConfig.ProviderName,
            origin = "automatic",
            attemptCount = ticket.AttemptCount,
            note = "Previous claim started an attempt without recording an outcome; Jira may already hold the issue.",
        }));

    /// <summary>
    /// String-based optimistic-concurrency detection (same precedent as the
    /// Slice 7 unique-violation check): the Application layer does not take
    /// a hard dependency on an EF exception type.
    /// </summary>
    private static bool IsConcurrencyConflict(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateConcurrencyException", StringComparison.Ordinal))
            return true;
        if (ex.InnerException is not null && IsConcurrencyConflict(ex.InnerException))
            return true;
        return false;
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..499] + "…";

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

    private static TicketDto Map(Ticket t, bool alreadyExisted) => new(
        t.Id, t.ProjectId, t.DefectId, t.IntegrationId, t.Provider,
        t.ExternalTicketId, t.ExternalKey, t.ExternalUrl, t.Title,
        t.SyncStatus.ToString(), t.CreatedBy, t.CreatedAt, t.UpdatedAt, alreadyExisted,
        t.Origin.ToString());
}
