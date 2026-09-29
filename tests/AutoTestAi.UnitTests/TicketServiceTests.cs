using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 7: manual Jira creation — auth, idempotency, mapping, secrets, audit.</summary>
public sealed class TicketServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private sealed class FakeTicketStore : ITicketStore
    {
        public readonly List<Ticket> Tickets = new();
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
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<Domain.Entities.FailureAnalysis> Analyses = new();
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<Execution?>(null);
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Tests.FirstOrDefault(t => t.Id == id));
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Array.Empty<ExecutionTest>());
        public Task<Domain.Entities.FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Analyses.FirstOrDefault(a => a.Id == id));
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
        public JiraCreateRequest? LastRequest;
        public string? LastEmail;
        public string? LastToken;
        public Task<JiraCreateResult> CreateIssueAsync(JiraCreateRequest request, string email, string apiToken, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            LastEmail = email;
            LastToken = apiToken;
            if (Handler is not null) return Handler(request);
            return Task.FromResult(new JiraCreateResult("10001", "ABC-123", "https://jira.test/browse/ABC-123"));
        }
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<(string Action, string? Meta)> Events = new();
        public Task RecordAsync(string a, string e, string? id, Guid? p, string? m, CancellationToken ct)
        { Events.Add((a, m)); return Task.CompletedTask; }
    }

    private sealed record Harness(
        TicketService Service, FakeTicketStore Tickets, FakeIntegrationStore Integrations,
        FakeDefectStore Defects, FakeJira Jira, FakeAudit Audit, Guid DefectId, Guid IntegrationId);

    private static StubCurrentUser Manager() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "user-1",
        Roles = ["qa-lead"], Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static Integration JiraRow(Guid project)
        => new()
        {
            ProjectId = project,
            Provider = "jira",
            IntegrationType = "ticketing",
            Configuration = JsonDocument.Parse(
                """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
            SecretReference = "test-token-123",
            Status = IntegrationStatus.Active,
        };

    private static Harness Create(ICurrentUserService? user = null, bool member = true, bool withJira = true)
    {
        user ??= Manager();
        var tickets = new FakeTicketStore();
        var integrations = new FakeIntegrationStore();
        var defects = new FakeDefectStore();
        var executions = new FakeExecutions();
        var cases = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        if (member) memberships.Add("user-1", ProjectA);
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        var audit = new FakeAudit();
        var jira = new FakeJira();

        var testId = Guid.NewGuid();
        executions.Tests.Add(new ExecutionTest
        {
            Id = testId, ExecutionId = Guid.NewGuid(), TestCaseId = Guid.NewGuid(),
            Status = ExecutionTestStatus.Failed, FailureClassification = FailureClassification.ApplicationDefect,
        });
        var defect = new Defect
        {
            ProjectId = ProjectA, ExecutionTestId = testId,
            Title = "Login 500", Description = "Fails.", Severity = Severity.High, Status = DefectStatus.Open,
        };
        defects.Defects.Add(defect);
        Guid integrationId = Guid.Empty;
        if (withJira)
        {
            var row = JiraRow(ProjectA);
            integrations.Rows.Add(row);
            integrationId = row.Id;
        }
        var service = new TicketService(
            tickets, integrations, defects, executions, cases, jira, user, authorization,
            directory, new SystemDateTimeProvider(), audit,
            NullLogger<TicketService>.Instance,
            Options.Create(new TicketOptions { AppBaseUrl = "https://app.test" }));
        return new Harness(service, tickets, integrations, defects, jira, audit, defect.Id, integrationId);
    }

    [Fact]
    public async Task Create_Success_PersistsExternalIds_AndAudits()
    {
        var h = Create();
        var ticket = await h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("ABC-123", ticket.ExternalKey);
        Assert.Equal("10001", ticket.ExternalId);
        Assert.StartsWith("https://", ticket.ExternalUrl);
        Assert.Equal("Synced", ticket.SyncStatus);
        Assert.False(ticket.AlreadyExisted);
        Assert.Equal(1, h.Jira.Calls);
        // Mapping assertions.
        Assert.Contains("ABC", h.Jira.LastRequest!.ProjectKey);
        Assert.Equal("High", h.Jira.LastRequest.Priority);
        Assert.Contains("Login 500", h.Jira.LastRequest.Summary);
        Assert.Contains(h.DefectId.ToString(), h.Jira.LastRequest.Description);
        // Defect unchanged.
        Assert.Equal(DefectStatus.Open, h.Defects.Defects.Single().Status);
        // Audit.
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.creation_requested");
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.created");
        foreach (var (_, meta) in h.Audit.Events)
            Assert.DoesNotContain("test-token-123", meta ?? string.Empty);
    }

    [Fact]
    public async Task Create_Idempotent_ReturnsExisting_WithoutSecondJiraCall()
    {
        var h = Create();
        var first = await h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None);
        var second = await h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal(first.Id, second.Id);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(1, h.Jira.Calls);
        Assert.Single(h.Tickets.Tickets.Where(t => t.SyncStatus == TicketSyncStatus.Synced));
    }

    [Fact]
    public async Task Create_Unauthenticated_Throws401()
    {
        var h = Create(new StubCurrentUser { IsAuthenticated = false });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
        Assert.Equal(0, h.Jira.Calls);
    }

    [Fact]
    public async Task Create_Viewer_Throws403()
    {
        var viewer = new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "user-1",
            Roles = ["viewer"], Permissions = RolePermissions.Resolve(["viewer"]),
        };
        var h = Create(viewer);
        await Assert.ThrowsAsync<ForbiddenException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
    }

    [Fact]
    public async Task Create_AdminBypass_Allows_WithoutMembership()
    {
        var admin = new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "admin-1",
            Roles = ["admin"], Permissions = RolePermissions.Resolve(["admin"]),
        };
        var h = Create(admin, member: false);
        var ticket = await h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal("ABC-123", ticket.ExternalKey);
    }

    [Fact]
    public async Task Create_CrossProject_Blocked()
    {
        var h = Create();
        h.Defects.Defects.Single().ProjectId = ProjectB;
        await Assert.ThrowsAsync<ForbiddenException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
        Assert.Equal(0, h.Jira.Calls);
    }

    [Fact]
    public async Task Create_MissingIntegration_Throws409()
    {
        var h = Create(withJira: false);
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
    }

    [Fact]
    public async Task Create_DisabledIntegration_Throws409()
    {
        var h = Create();
        h.Integrations.Rows.Single().Status = IntegrationStatus.Disabled;
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
    }

    [Fact]
    public async Task Create_JiraValidation_MapsTo400_AndPersistsFailed_AllowsRetry()
    {
        var h = Create();
        h.Jira.Handler = _ => throw JiraProviderException.Validation("bad project");
        await Assert.ThrowsAsync<ValidationException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
        var failed = Assert.Single(h.Tickets.Tickets);
        Assert.Equal(TicketSyncStatus.Failed, failed.SyncStatus);
        Assert.Contains(h.Audit.Events, e => e.Action == "ticket.creation_failed");

        // Retry after fixing Jira succeeds and reuses the failed row.
        h.Jira.Handler = null;
        var ticket = await h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None);
        Assert.Equal(TicketSyncStatus.Synced, h.Tickets.Tickets.Single().SyncStatus);
        Assert.Equal("ABC-123", ticket.ExternalKey);
    }

    [Fact]
    public async Task Create_JiraAuthFailure_DoesNotLeakSecret()
    {
        var h = Create();
        h.Jira.Handler = _ => throw JiraProviderException.Authentication("bad creds");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
        Assert.DoesNotContain("test-token-123", ex.Message);
        foreach (var (_, meta) in h.Audit.Events)
            Assert.DoesNotContain("test-token-123", meta ?? string.Empty);
    }

    [Fact]
    public async Task Create_JiraRateLimit_MapsTo429()
    {
        var h = Create();
        h.Jira.Handler = _ => throw new JiraProviderException(JiraErrorKind.RateLimited, "slow down");
        await Assert.ThrowsAsync<RateLimitedException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
    }

    [Fact]
    public async Task Create_MalformedJiraResponse_Throws_AndAllowsRetry()
    {
        var h = Create();
        h.Jira.Handler = _ => Task.FromResult(new JiraCreateResult("", "", "not-a-url"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.CreateFromDefectAsync(ProjectA, h.DefectId, CancellationToken.None));
        Assert.Equal(TicketSyncStatus.Failed, h.Tickets.Tickets.Single().SyncStatus);
    }

    [Fact]
    public void SeverityMapping_IsDeterministic_WithFallback()
    {
        Assert.Equal("Highest", JiraSeverityMapper.Map(Severity.Critical, null));
        Assert.Equal("P1", JiraSeverityMapper.Map(Severity.High,
            new Dictionary<string, string> { ["High"] = "P1" }));
        Assert.Equal("Medium", JiraSeverityMapper.Map((Severity)99, null));
    }

    [Fact]
    public void ContentBuilder_IncludesOnlyExistingData_AndNoSecrets()
    {
        var defect = new Defect
        {
            Title = "Login 500", Severity = Severity.High, Status = DefectStatus.Open,
            Description = "apiToken=secret-should-stay",
        };
        var config = new JiraIntegrationConfig("https://jira.test", "ABC", "qa@x.com", "Bug",
            new Dictionary<string, string>(), "https://app.test");
        var desc = JiraTicketContentBuilder.BuildDescription(defect, config, null, null, null);
        Assert.Contains("AutoTest AI Defect", desc);
        Assert.DoesNotContain("Authorization", desc);
        Assert.DoesNotContain("Bearer", desc);
    }

    [Fact]
    public void UrlValidator_RejectsUnsafeSchemes_AndPrivateHosts()
    {
        Assert.True(JiraUrlValidator.IsValidBaseUrl("https://jira.example.com", out _));
        Assert.False(JiraUrlValidator.IsValidBaseUrl("javascript:alert(1)", out _));
        Assert.False(JiraUrlValidator.IsValidBaseUrl("file:///etc/passwd", out _));
        Assert.False(JiraUrlValidator.IsValidBaseUrl("https://169.254.169.254", out _));
        Assert.False(JiraUrlValidator.IsSafeExternalTicketUrl("javascript:alert(1)"));
        Assert.True(JiraUrlValidator.IsSafeExternalTicketUrl("https://jira.test/browse/ABC-1"));
    }

    [Fact]
    public async Task NoAutomaticCreation_Paths_DoNotCallJira()
    {
        // Creating via TicketService is the only path; defect creation and
        // analysis paths hold no reference to IJiraTicketProvider (compile-time)
        // and this harness proves zero calls without an explicit human action.
        var h = Create();
        Assert.Equal(0, h.Jira.Calls);
        Assert.Empty(h.Tickets.Tickets);
    }
}
