using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 2 Slice 10: policy-controlled automatic Jira ticket creation —
/// defect-triggered automation, policy API, idempotency, failure handling,
/// manual interop, isolation. Jira HTTP is faked; no live Jira tenant.
/// </summary>
public sealed class AutoTicketApiTests : IClassFixture<AutoTicketApiTests.AutoTicketFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public sealed class FakeJira : IJiraTicketProvider
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

    public sealed class AutoTicketFactory : Slice1ApiFactory
    {
        public FakeJira Jira { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJiraTicketProvider>();
                services.AddSingleton<IJiraTicketProvider>(Jira);
            });
        }
    }

    private readonly AutoTicketFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private Guid _failedExecutionA = Guid.Empty;
    private Guid _policyIntegrationA = Guid.Empty;

    public AutoTicketApiTests(AutoTicketFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(async db =>
            {
                if (await db.Projects.AnyAsync(p => p.Id == pa)) return;
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var mgr = new User { ExternalIdentityId = "at-manager", Email = "m@x", DisplayName = "Manager" };
                var viewer = new User { ExternalIdentityId = "at-viewer", Email = "v@x", DisplayName = "V" };
                var admin = new User { ExternalIdentityId = "at-admin", Email = "a@x", DisplayName = "A" };
                var outsider = new User { ExternalIdentityId = "at-outsider", Email = "o@x", DisplayName = "O" };
                db.Users.AddRange(mgr, viewer, admin, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Auto Alpha", Key = "ATA" },
                    new Project { Id = pb, Name = "Auto Beta", Key = "ATB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                var tc = new TestCase
                {
                    ProjectId = pa, TestKey = "LOGIN-001", Title = "Login",
                    Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(tc);
                var exec = new Execution { ProjectId = pa, Status = ExecutionStatus.Failed };
                db.Executions.Add(exec);
                db.ExecutionTests.Add(new ExecutionTest
                {
                    ExecutionId = exec.Id, TestCaseId = tc.Id, Status = ExecutionTestStatus.Failed,
                    FailureClassification = FailureClassification.ApplicationDefect,
                });
                var integration = new Integration
                {
                    ProjectId = pa, Provider = "jira", IntegrationType = "ticketing",
                    Configuration = JsonDocument.Parse(
                        """{"baseUrl":"https://jira.test","projectKey":"ABC","email":"qa@example.com","issueType":"Bug"}"""),
                    SecretReference = "integration-secret-xyz",
                    Status = IntegrationStatus.Active,
                };
                db.Integrations.Add(integration);
                await db.SaveChangesAsync();
                _failedExecutionA = exec.Id;
                _policyIntegrationA = integration.Id;
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(string sub, string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private sealed record DefectPayload(Guid Id, string Title, string Severity, string Status);
    private sealed record TicketPayload(
        Guid Id, Guid ProjectId, Guid? DefectId, string Provider,
        string? ExternalId, string? ExternalKey, string? ExternalUrl,
        string SyncStatus, bool AlreadyExisted, string Origin);
    private sealed record PolicyPayload(
        Guid ProjectId, bool Enabled, Guid? IntegrationId,
        IReadOnlyList<string> Severities, IReadOnlyList<string> DefectStatuses,
        IReadOnlyList<string> Classifications, decimal? MinimumConfidence);
    private sealed record StatusPayload(
        Guid ProjectId, bool Enabled, bool Configured, Guid? IntegrationId,
        int PendingCount, int FailedCount, int SyncedAutomaticCount);

    private async Task<HttpResponseMessage> PutPolicyAsync(HttpClient client, object body)
        => await client.PutAsJsonAsync($"/api/v1/projects/{_projectA}/auto-ticket-policy", body);

    private static object EnabledPolicyBody => new
    {
        enabled = true,
        severities = new[] { "Critical", "High" },
        defectStatuses = new[] { "Open" },
        classifications = new[] { "ApplicationDefect" },
    };

    private async Task<Guid> CreateDefectAsync(HttpClient client, string title, string severity = "High")
    {
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/defects",
            new { executionId = _failedExecutionA, title, severity });
        response.EnsureSuccessStatusCode();
        var defect = await response.Content.ReadFromJsonAsync<DefectPayload>();
        return defect!.Id;
    }

    private async Task<TicketPayload?> WaitForTicketAsync(Guid defectId, TimeSpan timeout)
    {
        using var client = Client("at-viewer", ["viewer"]);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ticket = await client.GetFromJsonAsync<TicketPayload>(
                $"/api/v1/projects/{_projectA}/defects/{defectId}/ticket");
            if (ticket?.ExternalKey is not null)
                return ticket;
            await Task.Delay(200);
        }
        return null;
    }

    private async Task<Ticket?> ReadTicketRowAsync(Guid defectId)
    {
        Ticket? row = null;
        await _factory.SeedAsync(async db =>
        {
            row = await db.Tickets
                .Where(t => t.DefectId == defectId)
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync();
        });
        return row;
    }

    private async Task<Ticket?> WaitForTicketRowAsync(Guid defectId, Func<Ticket?, bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var row = await ReadTicketRowAsync(defectId);
            if (ready(row))
                return row;
            await Task.Delay(200);
        }
        return await ReadTicketRowAsync(defectId);
    }

    private async Task<IReadOnlyList<Ticket>> ListTicketRowsAsync(Guid defectId)
    {
        IReadOnlyList<Ticket> rows = Array.Empty<Ticket>();
        await _factory.SeedAsync(async db =>
        {
            rows = await db.Tickets
                .Where(t => t.DefectId == defectId)
                .OrderBy(t => t.CreatedAt)
                .ToListAsync();
        });
        return rows;
    }

    [Fact]
    public async Task Policy_Anonymous_401_MemberRead_AdminWrite()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient()
            .GetAsync($"/api/v1/projects/{_projectA}/auto-ticket-policy")).StatusCode);

        // qa-lead cannot configure (settings.manage is admin-only).
        var denied = await PutPolicyAsync(Client("at-manager", ["qa-lead"]), EnabledPolicyBody);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var admin = Client("at-admin", ["admin"]);
        var ok = await PutPolicyAsync(admin, EnabledPolicyBody);
        Assert.True(ok.IsSuccessStatusCode, $"got {ok.StatusCode}");
        var body = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("integration-secret-xyz", body);

        // Viewer member can read policy and status (tickets.read).
        var policy = await Client("at-viewer", ["viewer"])
            .GetFromJsonAsync<PolicyPayload>($"/api/v1/projects/{_projectA}/auto-ticket-policy");
        Assert.NotNull(policy);
        Assert.True(policy!.Enabled);
        var status = await Client("at-viewer", ["viewer"])
            .GetFromJsonAsync<StatusPayload>($"/api/v1/projects/{_projectA}/auto-ticket-policy/status");
        Assert.NotNull(status);
        Assert.True(status!.Configured);
    }

    [Fact]
    public async Task Policy_Validation_RejectsBadInput_AndCrossProjectIntegration()
    {
        await SeedOnceAsync();
        var admin = Client("at-admin", ["admin"]);

        var empty = await PutPolicyAsync(admin, new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var badEnum = await PutPolicyAsync(admin, new
        {
            enabled = true,
            severities = new[] { "Bogus" },
            defectStatuses = new[] { "Open" },
            classifications = new[] { "ApplicationDefect" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badEnum.StatusCode);

        // Cross-project integration pin is rejected.
        Guid foreignIntegration = Guid.Empty;
        await _factory.SeedAsync(async db =>
        {
            var row = new Integration
            {
                ProjectId = _projectB, Provider = "jira", IntegrationType = "ticketing",
                Configuration = JsonDocument.Parse(
                    """{"baseUrl":"https://jira.test","projectKey":"XYZ","email":"qa@example.com","issueType":"Bug"}"""),
                SecretReference = "other-secret",
                Status = IntegrationStatus.Active,
            };
            db.Integrations.Add(row);
            await db.SaveChangesAsync();
            foreignIntegration = row.Id;
        });
        var cross = await PutPolicyAsync(admin, new
        {
            enabled = true,
            integrationId = foreignIntegration,
            severities = new[] { "High" },
            defectStatuses = new[] { "Open" },
            classifications = new[] { "ApplicationDefect" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
    }

    [Fact]
    public async Task EligibleDefect_AutoCreatesJiraTicket_WithAutomaticOrigin()
    {
        await SeedOnceAsync();
        _factory.Jira.Handler = null;
        var admin = Client("at-admin", ["admin"]);
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);

        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto happy {Guid.NewGuid():N}");
        var ticket = await WaitForTicketAsync(defectId, TimeSpan.FromSeconds(15));
        Assert.NotNull(ticket);
        Assert.Equal("ABC-123", ticket!.ExternalKey);
        Assert.Equal("Synced", ticket.SyncStatus);
        Assert.Equal("Automatic", ticket.Origin);

        // Manual creation afterwards is idempotent (no duplicate external ticket).
        var callsBefore = _factory.Jira.Calls;
        var manual = await Client("at-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{defectId}/ticket", null);
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var again = await manual.Content.ReadFromJsonAsync<TicketPayload>();
        Assert.Equal(ticket.Id, again!.Id);
        Assert.True(again.AlreadyExisted);
        Assert.Equal(callsBefore, _factory.Jira.Calls);

        await _factory.SeedAsync(async db =>
        {
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "ticket.automation.requested");
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "ticket.automation.created");
            foreach (var meta in db.AuditEvents.Select(e => e.MetadataJson))
                Assert.DoesNotContain("integration-secret-xyz", meta ?? string.Empty);
        });
    }

    [Fact]
    public async Task IneligibleDefect_NoTicket_PolicyDisabled_NoTicket()
    {
        await SeedOnceAsync();
        _factory.Jira.Handler = null;
        var admin = Client("at-admin", ["admin"]);
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);

        // Low severity is outside the eligible set.
        var lowId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto low {Guid.NewGuid():N}", "Low");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Null(await ReadTicketRowAsync(lowId));

        // Disabling the policy stops automation for new defects.
        Assert.True((await PutPolicyAsync(admin, new { enabled = false })).IsSuccessStatusCode);
        var afterId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto off {Guid.NewGuid():N}");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Null(await ReadTicketRowAsync(afterId));
    }

    [Fact]
    public async Task JiraDown_DefectSurvives_ExecutionUnaffected_RetrySucceeds()
    {
        await SeedOnceAsync();
        var admin = Client("at-admin", ["admin"]);
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);

        _factory.Jira.Handler = _ => throw new JiraProviderException(JiraErrorKind.Unavailable, "down");
        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto down {Guid.NewGuid():N}");

        // Automation fails retryably; the defect itself is intact.
        var failed = await WaitForTicketRowAsync(defectId,
            r => r is not null && r.SyncStatus == TicketSyncStatus.Failed, TimeSpan.FromSeconds(15));
        Assert.NotNull(failed);
        Assert.NotNull(failed!.NextAttemptAt);
        Assert.Contains("retry", failed.LastError, StringComparison.OrdinalIgnoreCase);

        await _factory.SeedAsync(async db =>
        {
            Assert.NotNull(await db.Defects.FirstOrDefaultAsync(d => d.Id == defectId));
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "ticket.automation.failed");
            Assert.Contains(db.AuditEvents.Select(e => e.Action), a => a == "ticket.automation.retry_scheduled");
        });

        // Operator retry after Jira recovers succeeds.
        _factory.Jira.Handler = null;
        var retry = await Client("at-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{defectId}/ticket/automation/retry", null);
        Assert.True(retry.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, $"got {retry.StatusCode}");
        var synced = await WaitForTicketAsync(defectId, TimeSpan.FromSeconds(15));
        Assert.NotNull(synced);
        Assert.Equal("Automatic", synced!.Origin);
    }

    [Fact]
    public async Task PermanentFailure_StopsRetrying_AndManualRetryStillPossible()
    {
        await SeedOnceAsync();
        var admin = Client("at-admin", ["admin"]);
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);

        _factory.Jira.Handler = _ => throw JiraProviderException.Validation("bad project");
        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto perm {Guid.NewGuid():N}");
        var failed = await WaitForTicketRowAsync(defectId,
            r => r is not null && r.SyncStatus == TicketSyncStatus.Failed, TimeSpan.FromSeconds(15));
        Assert.NotNull(failed);
        Assert.Null(failed!.NextAttemptAt);
        _factory.Jira.Handler = null;
    }

    [Fact]
    public async Task ManualTicketFirst_AutomationConverges_Idempotently()
    {
        await SeedOnceAsync();
        _factory.Jira.Handler = null;
        // No policy yet: create a defect and ticket it manually.
        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Manual first {Guid.NewGuid():N}");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Null(await ReadTicketRowAsync(defectId));
        var manual = await Client("at-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{defectId}/ticket", null);
        // Manual creation requires a Jira integration: project A has one.
        Assert.True(manual.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"got {manual.StatusCode}");

        // Enabling the policy afterwards must not duplicate the ticket.
        var admin = Client("at-admin", ["admin"]);
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);
        var callsBefore = _factory.Jira.Calls;
        var retry = await Client("at-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectA}/defects/{defectId}/ticket/automation/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(callsBefore, _factory.Jira.Calls);
    }

    [Fact]
    public async Task StatusEndpoint_ReportsAutomationCounts_WithoutSecrets()
    {
        await SeedOnceAsync();
        var viewer = Client("at-viewer", ["viewer"]);
        var status = await viewer.GetFromJsonAsync<StatusPayload>(
            $"/api/v1/projects/{_projectA}/auto-ticket-policy/status");
        Assert.NotNull(status);
    }

    [Fact]
    public async Task ConcurrentTriggers_ConvergeOnSingleIntent_AndSingleJiraCall()
    {
        await SeedOnceAsync();
        _factory.Jira.Handler = null;
        var admin = Client("at-admin", ["admin"]);

        // Creation happens while automation is disabled: no intent, no queue.
        Assert.True((await PutPolicyAsync(admin, new { enabled = false })).IsSuccessStatusCode);
        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto race {Guid.NewGuid():N}");
        Assert.Null(await ReadTicketRowAsync(defectId));
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);
        var callsBefore = _factory.Jira.Calls;

        // Two logical workers race the same defect: at most one intent may
        // proceed toward Jira creation; both callers converge.
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var svcA = scopeA.ServiceProvider.GetRequiredService<IAutomatedTicketService>();
        var svcB = scopeB.ServiceProvider.GetRequiredService<IAutomatedTicketService>();
        var results = await Task.WhenAll(
            svcA.RequestAutomationAsync(_projectA, defectId, CancellationToken.None),
            svcB.RequestAutomationAsync(_projectA, defectId, CancellationToken.None));

        var synced = await WaitForTicketAsync(defectId, TimeSpan.FromSeconds(15));
        Assert.NotNull(synced);
        // Exactly one racer queues the intent; the other converges on it.
        Assert.Contains(results, r => r.Outcome == "queued");
        Assert.Contains(results, r => r.Outcome.StartsWith("skipped", StringComparison.Ordinal));
        var queuedId = results.Single(r => r.Outcome == "queued").TicketId;
        Assert.All(results, r => Assert.Equal(queuedId, r.TicketId));
        // Same intent for both racers (the second trigger converged).
        var rows = await ListTicketRowsAsync(defectId);
        Assert.Single(rows);
        Assert.Equal(_factory.Jira.Calls - callsBefore, 1);
    }

    [Fact]
    public async Task ClaimRecovery_StaleClaimantCannotOverwrite_ReclaimCompletes()
    {
        await SeedOnceAsync();
        var admin = Client("at-admin", ["admin"]);
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);

        var gate = new TaskCompletionSource<JiraCreateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _factory.Jira.Handler = _ =>
        {
            calls++;
            return calls == 1
                ? gate.Task
                : Task.FromResult(new JiraCreateResult("20002", "BBB-2", "https://jira.test/browse/BBB-2"));
        };

        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto claim {Guid.NewGuid():N}");

        // Worker A (background) claims the intent and blocks inside Jira.
        var claimed = await WaitForTicketRowAsync(defectId,
            r => r is not null && r.ClaimToken != null, TimeSpan.FromSeconds(15));
        Assert.NotNull(claimed);
        var ticketId = claimed!.Id;
        Assert.Equal(1, calls);

        // Worker B observes the live claim and refuses to execute alongside it.
        Guid reacquireTicketId;
        {
            using var scopeB = _factory.Services.CreateScope();
            var svcB = scopeB.ServiceProvider.GetRequiredService<IAutomatedTicketService>();
            Assert.Null(await svcB.ExecutePendingAsync(ticketId, CancellationToken.None));
            Assert.Equal(1, calls);
            reacquireTicketId = ticketId;
        }

        // Worker A "crashes": its lease expires while blocked.
        await _factory.SeedAsync(async db =>
        {
            var row = await db.Tickets.FirstAsync(t => t.Id == ticketId);
            row.ClaimExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        });

        // A's late Jira success must not land: the claim is stale.
        gate.SetResult(new JiraCreateResult("10001", "AAA-1", "https://jira.test/browse/AAA-1"));
        await Task.Delay(TimeSpan.FromSeconds(3));
        var afterStale = await ReadTicketRowAsync(defectId);
        Assert.NotNull(afterStale);
        Assert.NotEqual(TicketSyncStatus.Synced, afterStale!.SyncStatus);
        Assert.Null(afterStale.ExternalKey);

        // Worker B (fresh scope: no stale tracked entities) reclaims the
        // expired lease and completes exactly once.
        using var scopeC = _factory.Services.CreateScope();
        var svcC = scopeC.ServiceProvider.GetRequiredService<IAutomatedTicketService>();
        var done = await svcC.ExecutePendingAsync(reacquireTicketId, CancellationToken.None);
        Assert.NotNull(done);
        Assert.Equal("Synced", done!.SyncStatus);
        Assert.Equal("BBB-2", done.ExternalKey);
        Assert.Equal(2, calls);

        var rows = await ListTicketRowsAsync(defectId);
        var single = Assert.Single(rows);
        Assert.Equal("BBB-2", single.ExternalKey);
        Assert.Equal(2, single.AttemptCount);

        await _factory.SeedAsync(async db =>
        {
            var actions = db.AuditEvents.Where(e => e.ProjectId == _projectA).Select(e => e.Action).ToList();
            Assert.Contains(actions, a => a == "ticket.automation.recovered");
            Assert.Contains(actions, a => a == "ticket.automation.superseded");
            Assert.Contains(actions, a => a == "ticket.automation.created");
        });
        _factory.Jira.Handler = null;
    }

    [Fact]
    public async Task ManualAndAutomaticConcurrently_ConvergeOnSingleTicket()
    {
        await SeedOnceAsync();
        _factory.Jira.Handler = null;
        var admin = Client("at-admin", ["admin"]);

        // Creation happens while automation is disabled so the test drives
        // both paths explicitly afterwards.
        Assert.True((await PutPolicyAsync(admin, new { enabled = false })).IsSuccessStatusCode);
        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Manual auto race {Guid.NewGuid():N}");
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);

        using var scope = _factory.Services.CreateScope();
        var automation = scope.ServiceProvider.GetRequiredService<IAutomatedTicketService>();
        var manager = Client("at-manager", ["qa-lead"]);

        // Race the human path against the automation path for the same defect.
        var request = await automation.RequestAutomationAsync(_projectA, defectId, CancellationToken.None);
        Assert.Equal("queued", request.Outcome);
        var manualCall = manager.PostAsync($"/api/v1/projects/{_projectA}/defects/{defectId}/ticket", null);
        var autoCall = automation.ExecutePendingAsync(request.TicketId!.Value, CancellationToken.None);
        await Task.WhenAll(manualCall, autoCall);

        Assert.True(manualCall.Result.IsSuccessStatusCode, $"got {manualCall.Result.StatusCode}");
        var manual = await manualCall.Result.Content.ReadFromJsonAsync<TicketPayload>();
        var auto = await autoCall;
        Assert.NotNull(manual);
        Assert.NotNull(auto);
        // One authoritative internal ticket regardless of who won the race.
        Assert.Equal(manual!.Id, auto!.Id);
        var rows = await ListTicketRowsAsync(defectId);
        var synced = rows.Where(t => t.SyncStatus == TicketSyncStatus.Synced).ToList();
        Assert.Single(synced);
        Assert.Equal(manual.ExternalKey, synced[0].ExternalKey);
        _factory.Jira.Handler = null;
    }

    [Fact]
    public async Task RetryEndpoint_CrossProject_Forbidden_AndOutsider_Denied()
    {
        await SeedOnceAsync();
        // Manager belongs to A only: retrying defect A under project B is forbidden.
        var cross = await Client("at-manager", ["qa-lead"])
            .PostAsync($"/api/v1/projects/{_projectB}/defects/{Guid.NewGuid()}/ticket/automation/retry", null);
        Assert.True(cross.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"got {cross.StatusCode}");

        // Outsider cannot read policy or status.
        var outsider = Client("at-outsider", ["tester"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider
            .GetAsync($"/api/v1/projects/{_projectA}/auto-ticket-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider
            .GetAsync($"/api/v1/projects/{_projectA}/auto-ticket-policy/status")).StatusCode);
    }

    [Fact]
    public async Task RateLimited_RetryAfterHint_SchedulesHintedRetry()
    {
        await SeedOnceAsync();
        var admin = Client("at-admin", ["admin"]);
        Assert.True((await PutPolicyAsync(admin, EnabledPolicyBody)).IsSuccessStatusCode);

        _factory.Jira.Handler = _ => throw new JiraProviderException(
            JiraErrorKind.RateLimited, "slow down", null, TimeSpan.FromSeconds(90));
        var defectId = await CreateDefectAsync(Client("at-manager", ["qa-lead"]), $"Auto 429 {Guid.NewGuid():N}");
        var failed = await WaitForTicketRowAsync(defectId,
            r => r is not null && r.SyncStatus == TicketSyncStatus.Failed, TimeSpan.FromSeconds(15));
        Assert.NotNull(failed);
        Assert.NotNull(failed!.NextAttemptAt);
        var delay = failed.NextAttemptAt.Value - DateTimeOffset.UtcNow;
        Assert.True(delay > TimeSpan.FromSeconds(60), $"delay was {delay}");
        Assert.True(delay <= TimeSpan.FromSeconds(120), $"delay was {delay}");
        _factory.Jira.Handler = null;
    }
}
