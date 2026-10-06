using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Phase 2 Slice 10: policy-controlled automatic Jira ticket creation.
/// Policy evaluation, idempotency, retry classification, audit, isolation.
/// Jira HTTP is faked; no live Jira tenant.
/// </summary>
public sealed class AutoTicketTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private sealed class FakeTicketStore : ITicketStore
    {
        public readonly List<Ticket> Tickets = new();
        public bool ThrowUniqueOnSave;
        public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));
        public Task<Ticket?> FindSyncedAsync(Guid defectId, Guid integrationId, CancellationToken ct)
            => Task.FromResult(Tickets.FirstOrDefault(t =>
                t.DefectId == defectId && t.IntegrationId == integrationId && t.SyncStatus == TicketSyncStatus.Synced));
        public Task<Ticket?> FindLatestForDefectAsync(Guid defectId, Guid integrationId, CancellationToken ct)
            => Task.FromResult(Tickets.Where(t => t.DefectId == defectId && t.IntegrationId == integrationId)
                .OrderByDescending(t => t.CreatedAt).FirstOrDefault());
        public Task<IReadOnlyList<Ticket>> ListForDefectAsync(Guid defectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Ticket>>(Tickets.Where(t => t.DefectId == defectId).ToList());
        public Task AddAsync(Ticket t, CancellationToken ct) { Tickets.Add(t); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct)
        {
            if (ThrowUniqueOnSave)
                throw new InvalidOperationException("duplicate key violates unique constraint");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeQueryStore : IAutoTicketQueryStore
    {
        private readonly FakeTicketStore _inner;
        public FakeQueryStore(FakeTicketStore inner) => _inner = inner;
        public Task<Ticket?> GetTicketByIdAsync(Guid id, CancellationToken ct)
            => _inner.GetByIdAsync(id, ct);
        public Task<Ticket?> GetTicketSnapshotAsync(Guid id, CancellationToken ct)
            => Task.FromResult(_inner.Tickets.FirstOrDefault(t => t.Id == id) is { } row
                ? new Ticket
                {
                    Id = row.Id, ProjectId = row.ProjectId, DefectId = row.DefectId,
                    IntegrationId = row.IntegrationId, Provider = row.Provider,
                    ExternalTicketId = row.ExternalTicketId, ExternalKey = row.ExternalKey,
                    ExternalUrl = row.ExternalUrl, Title = row.Title, Status = row.Status,
                    SyncStatus = row.SyncStatus, Origin = row.Origin, AttemptCount = row.AttemptCount,
                    LastError = row.LastError, NextAttemptAt = row.NextAttemptAt,
                    ClaimToken = row.ClaimToken, ClaimExpiresAt = row.ClaimExpiresAt,
                    CreatedBy = row.CreatedBy, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
                }
                : null);
        public Task<int> CountAsync(Guid projectId, string syncStatus, CancellationToken ct)
        {
            if (!Enum.TryParse<TicketSyncStatus>(syncStatus, true, out var status))
                return Task.FromResult(0);
            return Task.FromResult(_inner.Tickets.Count(t =>
                t.ProjectId == projectId && t.SyncStatus == status && t.Origin == TicketOrigin.Automatic));
        }
        public Task<DateTimeOffset?> LastAutomationAtAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult(_inner.Tickets
                .Where(t => t.ProjectId == projectId && t.Origin == TicketOrigin.Automatic)
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => (DateTimeOffset?)t.UpdatedAt)
                .FirstOrDefault());
        public Task<IReadOnlyList<Ticket>> ListRecentAutomationAsync(Guid projectId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Ticket>>(_inner.Tickets
                .Where(t => t.ProjectId == projectId && t.Origin == TicketOrigin.Automatic)
                .OrderByDescending(t => t.UpdatedAt).Take(take).ToList());
        public Task<IReadOnlyList<Ticket>> ListDueAutomationAsync(DateTimeOffset now, int take, CancellationToken ct)
        {
            var pending = _inner.Tickets
                .Where(t => t.Origin == TicketOrigin.Automatic && t.SyncStatus == TicketSyncStatus.Pending)
                .OrderBy(t => t.UpdatedAt).Take(take).ToList();
            if (pending.Count > 0)
                return Task.FromResult<IReadOnlyList<Ticket>>(pending);
            return Task.FromResult<IReadOnlyList<Ticket>>(_inner.Tickets
                .Where(t => t.Origin == TicketOrigin.Automatic && t.SyncStatus == TicketSyncStatus.Failed &&
                    t.NextAttemptAt != null && t.NextAttemptAt <= now)
                .OrderBy(t => t.NextAttemptAt).Take(take).ToList());
        }
        public Task SaveChangesAsync(CancellationToken ct) => _inner.SaveChangesAsync(ct);
    }

    private sealed class FakePolicyStore : IAutoTicketPolicyStore
    {
        public readonly List<AutoTicketPolicy> Rows = new();
        public Task<AutoTicketPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.ProjectId == projectId));
        public Task AddAsync(AutoTicketPolicy policy, CancellationToken ct) { Rows.Add(policy); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeIntegrationStore : IIntegrationStore
    {
        public readonly List<Integration> Rows = new();
        public Task<Integration?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
        public Task<Integration?> FindByProjectAndProviderAsync(Guid projectId, string provider, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.ProjectId == projectId && r.Provider == provider));
        public Task AddAsync(Integration r, CancellationToken ct) { Rows.Add(r); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDefectStore : IDefectStore
    {
        public readonly List<Defect> Defects = new();
        public Task<int> CountAsync(Guid p, string? s, string? sev, string? c, Guid? t, string? q, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<Defect>> ListAsync(Guid p, string? s, string? sev, string? c, Guid? t, string? q, int sk, int ta, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Defect>>(Array.Empty<Defect>());
        public Task<Defect?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Defects.FirstOrDefault(d => d.Id == id));
        public Task AddAsync(Defect d, CancellationToken ct) { Defects.Add(d); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeExecutions : IExecutionStore
    {
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<Execution?>(null);
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<ExecutionTest?>(null);
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Array.Empty<ExecutionTest>());
        public Task<Domain.Entities.FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<Domain.Entities.FailureAnalysis?>(null);
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int sk, int ta, CancellationToken ct) => throw new NotImplementedException();
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionAsync(Execution e, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? afterId, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteLogsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Domain.Entities.FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAnalysisAsync(Domain.Entities.FailureAnalysis a, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeJira : IJiraTicketProvider
    {
        public int Calls;
        public Func<JiraCreateRequest, Task<JiraCreateResult>>? Handler;
        public Task<JiraCreateResult> CreateIssueAsync(JiraCreateRequest request, string email, string apiToken, CancellationToken ct)
        {
            Calls++;
            if (Handler is not null) return Handler(request);
            return Task.FromResult(new JiraCreateResult("10001", "ABC-123", "https://jira.test/browse/ABC-123"));
        }

        public Task<JiraIssueDto> GetIssueAsync(JiraIssueRequest request, string email, string apiToken, CancellationToken ct)
            => throw new NotImplementedException("Auto-ticket tests never read Jira issues.");
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<(string Action, string? Meta)> Events = new();
        public Task RecordAsync(string a, string e, string? id, Guid? p, string? m, CancellationToken ct)
        { Events.Add((a, m)); return Task.CompletedTask; }
    }

    private sealed record Harness(
        AutomatedTicketService Automation,
        AutoTicketPolicyService Policies,
        FakeTicketStore Tickets,
        FakePolicyStore PolicyRows,
        FakeIntegrationStore Integrations,
        FakeDefectStore Defects,
        FakeJira Jira,
        FakeAudit Audit,
        Guid DefectId,
        Guid IntegrationId);

    private static StubCurrentUser AdminUser() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "admin-1",
        Roles = ["admin"], Permissions = RolePermissions.Resolve(["admin"]),
    };

    private static Integration JiraRow(Guid project) => new()
    {
        ProjectId = project,
        Provider = "jira",
        IntegrationType = "ticketing",
        Configuration = JsonDocument.Parse(
            """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
        SecretReference = "test-token-123",
        Status = IntegrationStatus.Active,
    };

    private static AutoTicketPolicy EnabledPolicy(Guid project, Guid? integrationId = null) => new()
    {
        ProjectId = project,
        Enabled = true,
        IntegrationId = integrationId,
        Severities = "Critical,High",
        DefectStatuses = "Open,InProgress",
        Classifications = "ApplicationDefect",
        MinimumConfidence = null,
    };

    private static Harness Create(
        ICurrentUserService? user = null,
        bool withJira = true,
        bool withPolicy = true,
        Func<Defect>? defectFactory = null)
    {
        user ??= AdminUser();
        var tickets = new FakeTicketStore();
        var query = new FakeQueryStore(tickets);
        var policies = new FakePolicyStore();
        var integrations = new FakeIntegrationStore();
        var defects = new FakeDefectStore();
        var executions = new FakeExecutions();
        var cases = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        memberships.Add("admin-1", ProjectA);
        memberships.Add("admin-1", ProjectB);
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        var audit = new FakeAudit();
        var jira = new FakeJira();
        var queue = new AutoTicketQueue();

        var defect = defectFactory?.Invoke() ?? new Defect
        {
            ProjectId = ProjectA,
            Title = "Login 500",
            Severity = Severity.High,
            Status = DefectStatus.Open,
            RootCauseType = FailureClassification.ApplicationDefect,
        };
        defects.Defects.Add(defect);

        Guid integrationId = Guid.Empty;
        if (withJira)
        {
            var row = JiraRow(ProjectA);
            integrations.Rows.Add(row);
            integrationId = row.Id;
        }
        if (withPolicy)
            policies.Rows.Add(EnabledPolicy(ProjectA, null));

        var automation = new AutomatedTicketService(
            tickets, query, policies, integrations, defects, executions, cases, jira,
            new SystemDateTimeProvider(), audit, authorization, queue,
            NullLogger<AutomatedTicketService>.Instance,
            Options.Create(new TicketOptions { AppBaseUrl = "https://app.test" }),
            Options.Create(new AutoTicketOptions { ClaimLeaseSeconds = 300 }));
        var policyService = new AutoTicketPolicyService(
            policies, integrations, query, authorization, user, directory,
            new SystemDateTimeProvider(), audit);
        return new Harness(automation, policyService, tickets, policies, integrations, defects, jira, audit, defect.Id, integrationId);
    }

    // ---------- policy evaluation ----------

    [Fact]
    public async Task NoPolicy_NoTicket_SkippedAndAudited()
    {
        var h = Create(withPolicy: false);
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.StartsWith("skipped", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
        Assert.Empty(h.Tickets.Tickets);
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.automation.skipped");
    }

    [Fact]
    public async Task DisabledPolicy_NoTicket()
    {
        var h = Create();
        h.PolicyRows.Rows.Single().Enabled = false;
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("skipped_policy_disabled", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
        Assert.Empty(h.Tickets.Tickets);
    }

    [Fact]
    public async Task IneligibleSeverity_NoTicket()
    {
        var h = Create(defectFactory: () => new Defect
        {
            ProjectId = ProjectA, Title = "Minor", Severity = Severity.Low,
            Status = DefectStatus.Open, RootCauseType = FailureClassification.ApplicationDefect,
        });
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("skipped_severity_not_eligible", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
    }

    [Fact]
    public async Task IneligibleClassification_NoTicket()
    {
        var h = Create(defectFactory: () => new Defect
        {
            ProjectId = ProjectA, Title = "Flaky env", Severity = Severity.Critical,
            Status = DefectStatus.Open, RootCauseType = FailureClassification.EnvironmentFailure,
        });
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("skipped_classification_not_eligible", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
    }

    [Fact]
    public async Task IneligibleStatus_NoTicket()
    {
        var h = Create(defectFactory: () => new Defect
        {
            ProjectId = ProjectA, Title = "Closed", Severity = Severity.Critical,
            Status = DefectStatus.Closed, RootCauseType = FailureClassification.ApplicationDefect,
        });
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("skipped_status_not_eligible", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
    }

    [Fact]
    public async Task ConfidenceThreshold_FiltersLowConfidence_AllowsMissing()
    {
        var low = Create(defectFactory: () => new Defect
        {
            ProjectId = ProjectA, Title = "Low conf", Severity = Severity.High,
            Status = DefectStatus.Open, RootCauseType = FailureClassification.ApplicationDefect,
            AiConfidence = 0.2m,
        });
        low.PolicyRows.Rows.Single().MinimumConfidence = 0.7m;
        var result = await low.Automation.RequestAutomationAsync(ProjectA, low.DefectId, CancellationToken.None);
        Assert.Equal("skipped_confidence_below_threshold", result.Outcome);

        // Missing confidence never blocks.
        var missing = Create();
        missing.PolicyRows.Rows.Single().MinimumConfidence = 0.7m;
        var ok = await missing.Automation.RequestAutomationAsync(ProjectA, missing.DefectId, CancellationToken.None);
        Assert.Equal("queued", ok.Outcome);
    }

    // ---------- happy path + origin ----------

    [Fact]
    public async Task EligibleDefect_QueuesIntent_ThenSyncs_WithAutomaticOrigin()
    {
        var h = Create();
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("queued", result.Outcome);
        Assert.NotNull(result.TicketId);
        // Request path never calls Jira.
        Assert.Equal(0, h.Jira.Calls);
        var intent = h.Tickets.Tickets.Single();
        Assert.Equal(TicketSyncStatus.Pending, intent.SyncStatus);
        Assert.Equal(TicketOrigin.Automatic, intent.Origin);
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.automation.requested");

        var ticket = await h.Automation.ExecutePendingAsync(result.TicketId!.Value, CancellationToken.None);
        Assert.NotNull(ticket);
        Assert.Equal("Synced", ticket!.SyncStatus);
        Assert.Equal("Automatic", ticket.Origin);
        Assert.Equal("ABC-123", ticket.ExternalKey);
        Assert.False(ticket.AlreadyExisted);
        Assert.Equal(1, h.Jira.Calls);
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.automation.created");
        foreach (var (_, meta) in h.Audit.Events)
            Assert.DoesNotContain("test-token-123", meta ?? string.Empty);
    }

    [Fact]
    public async Task DuplicateTrigger_ConvergesOnSingleIntent_AndSingleJiraCall()
    {
        var h = Create();
        var first = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        var second = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("queued", first.Outcome);
        Assert.StartsWith("skipped", second.Outcome);
        Assert.Equal(first.TicketId, second.TicketId);
        Assert.Single(h.Tickets.Tickets);

        // Concurrent execution converges as well.
        var results = await Task.WhenAll(
            h.Automation.ExecutePendingAsync(first.TicketId!.Value, CancellationToken.None),
            h.Automation.ExecutePendingAsync(first.TicketId!.Value, CancellationToken.None));
        Assert.Equal(1, h.Jira.Calls);
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task ExistingManualTicket_SatisfiesAutomation_Idempotently()
    {
        var h = Create();
        h.Tickets.Tickets.Add(new Ticket
        {
            ProjectId = ProjectA, DefectId = h.DefectId, IntegrationId = h.IntegrationId,
            Provider = "jira", ExternalTicketId = "10001", ExternalKey = "ABC-1",
            ExternalUrl = "https://jira.test/browse/ABC-1", Title = "Manual",
            Status = "created", SyncStatus = TicketSyncStatus.Synced, Origin = TicketOrigin.Manual,
        });
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("skipped_already_synced", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
    }

    // ---------- failure handling ----------

    [Fact]
    public async Task MissingIntegration_RecordsFailure_WithoutJiraCall()
    {
        var h = Create(withJira: false);
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("failed_no_integration", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.automation.failed");
        // Defect itself is untouched.
        Assert.Single(h.Defects.Defects);
    }

    [Fact]
    public async Task TransientFailure_SchedulesRetry_ThenSucceeds()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        h.Jira.Handler = _ => throw new JiraProviderException(JiraErrorKind.Unavailable, "down");
        Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        var row = h.Tickets.Tickets.Single();
        Assert.Equal(TicketSyncStatus.Failed, row.SyncStatus);
        Assert.NotNull(row.NextAttemptAt);
        Assert.Equal(1, row.AttemptCount);
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.automation.retry_scheduled");

        h.Jira.Handler = null;
        // Simulate reconciliation picking the row up once due.
        h.Tickets.Tickets.Single().NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var ticket = await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None);
        Assert.NotNull(ticket);
        Assert.Equal("Synced", ticket!.SyncStatus);
        Assert.Null(h.Tickets.Tickets.Single().NextAttemptAt);
    }

    [Fact]
    public async Task RateLimited_SchedulesRetry()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        h.Jira.Handler = _ => throw new JiraProviderException(JiraErrorKind.RateLimited, "slow down");
        Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        Assert.NotNull(h.Tickets.Tickets.Single().NextAttemptAt);
    }

    [Fact]
    public async Task PermanentFailure_StopsRetrying()
    {
        foreach (var kind in new[] { JiraErrorKind.Validation, JiraErrorKind.Authentication, JiraErrorKind.Permission, JiraErrorKind.NotFound })
        {
            var h = Create();
            var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
            h.Jira.Handler = _ => throw new JiraProviderException(kind, "permanent");
            Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
            var row = h.Tickets.Tickets.Single();
            Assert.Equal(TicketSyncStatus.Failed, row.SyncStatus);
            Assert.Null(row.NextAttemptAt);
        }
    }

    [Fact]
    public async Task RetriesAreBounded_MaxAttemptsStops()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        h.Jira.Handler = _ => throw new JiraProviderException(JiraErrorKind.Timeout, "timeout");
        for (var i = 0; i < AutoTicketRetryPolicy.MaxAttempts; i++)
        {
            // Simulate reconciliation picking the row up once each retry is due.
            h.Tickets.Tickets.Single().NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        }
        var row = h.Tickets.Tickets.Single();
        Assert.Equal(AutoTicketRetryPolicy.MaxAttempts, row.AttemptCount);
        Assert.Null(row.NextAttemptAt);
    }

    // ---------- retry policy unit checks ----------

    [Fact]
    public void RetryClassification_MatchesSpec()
    {
        Assert.True(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.RateLimited));
        Assert.True(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.Unavailable));
        Assert.True(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.Timeout));
        Assert.False(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.Validation));
        Assert.False(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.Authentication));
        Assert.False(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.Permission));
        Assert.False(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.NotFound));
        Assert.False(AutoTicketRetryPolicy.IsRetryable(JiraErrorKind.MalformedResponse));
        // Backoff grows; rate limits wait longer.
        Assert.True(AutoTicketRetryPolicy.DelayForAttempt(JiraErrorKind.Unavailable, 2) >
            AutoTicketRetryPolicy.DelayForAttempt(JiraErrorKind.Unavailable, 1));
        Assert.True(AutoTicketRetryPolicy.DelayForAttempt(JiraErrorKind.RateLimited, 1) >=
            AutoTicketRetryPolicy.DelayForAttempt(JiraErrorKind.Unavailable, 1));
    }

    // ---------- policy service ----------

    [Fact]
    public async Task Policy_RequiresAdmin_AndValidates()
    {
        var viewer = new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "viewer-1",
            Roles = ["viewer"], Permissions = RolePermissions.Resolve(["viewer"]),
        };
        // Viewer is not a member; even membership would fail on settings.manage.
        var h = Create(viewer);
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Policies.UpsertAsync(
            new UpsertAutoTicketPolicyCommand(ProjectA, true, null,
                ["High"], ["Open"], ["ApplicationDefect"], null),
            CancellationToken.None));

        var admin = Create();

        // Enabled policy with empty eligibility is rejected.
        await Assert.ThrowsAsync<ValidationException>(() => admin.Policies.UpsertAsync(
            new UpsertAutoTicketPolicyCommand(ProjectA, true, null,
                [], [], [], null),
            CancellationToken.None));

        // Unknown enum values are rejected.
        await Assert.ThrowsAsync<ValidationException>(() => admin.Policies.UpsertAsync(
            new UpsertAutoTicketPolicyCommand(ProjectA, true, null,
                ["Bogus"], ["Open"], ["ApplicationDefect"], null),
            CancellationToken.None));

        // Confidence out of range is rejected.
        await Assert.ThrowsAsync<ValidationException>(() => admin.Policies.UpsertAsync(
            new UpsertAutoTicketPolicyCommand(ProjectA, true, null,
                ["High"], ["Open"], ["ApplicationDefect"], 2m),
            CancellationToken.None));
    }

    [Fact]
    public async Task Policy_RejectsCrossProjectIntegration()
    {
        var h = Create();
        var foreign = JiraRow(ProjectB);
        h.Integrations.Rows.Add(foreign);
        await Assert.ThrowsAsync<ValidationException>(() => h.Policies.UpsertAsync(
            new UpsertAutoTicketPolicyCommand(ProjectA, true, foreign.Id,
                ["High"], ["Open"], ["ApplicationDefect"], null),
            CancellationToken.None));
    }

    [Fact]
    public async Task Policy_RoundTrips_AndStatusCounts()
    {
        var h = Create(withPolicy: false);
        Assert.Null(await h.Policies.GetAsync(ProjectA, CancellationToken.None));
        var saved = await h.Policies.UpsertAsync(
            new UpsertAutoTicketPolicyCommand(ProjectA, true, null,
                ["Critical", "High"], ["Open"], ["ApplicationDefect"], 0.5m),
            CancellationToken.None);
        Assert.True(saved.Enabled);
        Assert.Equal(["Critical", "High"], saved.Severities);
        var status = await h.Policies.GetStatusAsync(ProjectA, CancellationToken.None);
        Assert.True(status.Configured);
        Assert.True(status.Enabled);
    }

    [Fact]
    public async Task CrossProject_DefectTrigger_Skipped()
    {
        var h = Create();
        h.Defects.Defects.Single().ProjectId = ProjectB;
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("skipped_cross_project", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
    }

    // ---------- claim protocol (Slice 10 audit hardening) ----------

    [Fact]
    public async Task ActiveForeignClaim_SkipsWithoutJiraCall()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("queued", queued.Outcome);
        // Another instance holds the lease.
        var row = h.Tickets.Tickets.Single();
        row.ClaimToken = Guid.NewGuid();
        row.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        Assert.Equal(0, h.Jira.Calls);
        Assert.Equal(TicketSyncStatus.Pending, row.SyncStatus);
    }

    [Fact]
    public async Task ExpiredClaim_ReclaimedAndCompletes()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        var row = h.Tickets.Tickets.Single();
        row.ClaimToken = Guid.NewGuid();
        row.ClaimExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        var ticket = await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None);
        Assert.NotNull(ticket);
        Assert.Equal("Synced", ticket!.SyncStatus);
        Assert.Equal(1, h.Jira.Calls);
    }

    [Fact]
    public async Task NotDue_FailedRow_SkippedWithoutAttempt()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        h.Jira.Handler = _ => throw new JiraProviderException(JiraErrorKind.Unavailable, "down");
        Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        var calls = h.Jira.Calls;

        // Still waiting for the scheduled retry: no new attempt.
        Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        Assert.Equal(calls, h.Jira.Calls);
    }

    [Fact]
    public async Task PolicyDisabledMidFlight_BlocksExecutionPermanently()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        h.PolicyRows.Rows.Single().Enabled = false;

        Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        Assert.Equal(0, h.Jira.Calls);
        var row = h.Tickets.Tickets.Single();
        Assert.Equal(TicketSyncStatus.Failed, row.SyncStatus);
        Assert.Null(row.NextAttemptAt);
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.automation.skipped");
    }

    [Fact]
    public async Task AttemptCap_BlocksFurtherExecution()
    {
        var h = Create();
        var queued = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        var row = h.Tickets.Tickets.Single();
        row.AttemptCount = AutoTicketRetryPolicy.MaxAttempts;

        Assert.Null(await h.Automation.ExecutePendingAsync(queued.TicketId!.Value, CancellationToken.None));
        Assert.Equal(0, h.Jira.Calls);
        Assert.Equal(TicketSyncStatus.Failed, row.SyncStatus);
        Assert.Null(row.NextAttemptAt);
    }

    [Fact]
    public async Task IntentUniqueViolation_ConvergesOnPending()
    {
        var h = Create();
        // Simulate losing the cross-instance Pending race in Postgres.
        h.Tickets.ThrowUniqueOnSave = true;
        var result = await h.Automation.RequestAutomationAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.StartsWith("skipped", result.Outcome);
        Assert.Equal(0, h.Jira.Calls);
    }

    [Fact]
    public void RetryAfter_Honored_ButBounded()
    {
        var honored = AutoTicketRetryPolicy.DelayForAttempt(
            JiraErrorKind.RateLimited, 1, TimeSpan.FromSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(90), honored);
        var capped = AutoTicketRetryPolicy.DelayForAttempt(
            JiraErrorKind.RateLimited, 1, TimeSpan.FromHours(5));
        Assert.Equal(TimeSpan.FromSeconds(3600), capped);
        var floored = AutoTicketRetryPolicy.DelayForAttempt(
            JiraErrorKind.RateLimited, 1, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(1), floored);
        // Non-rate-limit kinds ignore the hint.
        Assert.Equal(
            AutoTicketRetryPolicy.DelayForAttempt(JiraErrorKind.Unavailable, 1),
            AutoTicketRetryPolicy.DelayForAttempt(JiraErrorKind.Unavailable, 1, TimeSpan.FromSeconds(90)));
    }
}
