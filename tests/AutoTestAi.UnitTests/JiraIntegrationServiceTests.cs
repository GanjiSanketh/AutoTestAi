using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 7 §7: Jira configuration — auth, validation, secret retention/safety.</summary>
public sealed class JiraIntegrationServiceTests
{
    private sealed class FakeStore : IIntegrationStore
    {
        public readonly List<Integration> Rows = new();
        public Task<Integration?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
        public Task<Integration?> FindByProjectAndProviderAsync(Guid projectId, string provider, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.ProjectId == projectId && r.Provider == provider));
        public Task AddAsync(Integration r, CancellationToken ct) { Rows.Add(r); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string a, string e, string? id, Guid? p, string? m, CancellationToken ct)
        {
            Actions.Add(a);
            if (m is not null)
            {
                Assert.DoesNotContain("super-secret", m);
                Assert.DoesNotContain("Basic", m);
            }
            return Task.CompletedTask;
        }
    }

    private static readonly Guid Project = Guid.NewGuid();

    private static JiraIntegrationService Create(
        FakeStore store, FakeAudit audit, ICurrentUserService? user = null, bool member = true)
    {
        user ??= new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "admin-1",
            Roles = ["admin"], Permissions = RolePermissions.Resolve(["admin"]),
        };
        var memberships = new StubMembershipStore();
        if (member) memberships.Add("admin-1", Project);
        return new JiraIntegrationService(store, new AuthorizationService(user, memberships),
            user, new SystemDateTimeProvider(), audit);
    }

    [Fact]
    public async Task Upsert_Valid_PersistsConfig_AndReportsStatus_WithoutSecret()
    {
        var store = new FakeStore();
        var audit = new FakeAudit();
        var service = Create(store, audit);
        var dto = await service.UpsertAsync(new UpsertJiraIntegrationCommand(
            Project, "https://jira.example.com", "abc", "qa@example.com", "super-secret", "Bug", null, true),
            CancellationToken.None);
        Assert.True(dto.HasSecret);
        Assert.Equal("ABC", dto.ProjectKey);
        var status = await service.GetStatusAsync(Project, CancellationToken.None);
        Assert.True(status.Configured);
        Assert.True(status.Enabled);
        Assert.Equal("ABC", status.ProjectKey);
        // Raw secret never appears in stored configuration JSON.
        var raw = store.Rows.Single().Configuration!.RootElement.GetRawText();
        Assert.DoesNotContain("super-secret", raw);
        Assert.Contains(audit.Actions, a => a == "integration.jira_configured");
    }

    [Fact]
    public async Task Upsert_RetainsSecret_WhenTokenOmitted()
    {
        var store = new FakeStore();
        var audit = new FakeAudit();
        var service = Create(store, audit);
        await service.UpsertAsync(new UpsertJiraIntegrationCommand(
            Project, "https://jira.example.com", "ABC", "qa@example.com", "super-secret", "Bug", null, true),
            CancellationToken.None);
        var second = await service.UpsertAsync(new UpsertJiraIntegrationCommand(
            Project, "https://jira.example.com", "ABC", "qa@example.com", null, "Task", null, true),
            CancellationToken.None);
        Assert.Equal("Task", second.IssueType);
        Assert.Equal("super-secret", store.Rows.Single().SecretReference);
    }

    [Fact]
    public async Task Upsert_InvalidUrl_AndMissingToken_Rejected()
    {
        var service = Create(new FakeStore(), new FakeAudit());
        await Assert.ThrowsAsync<ValidationException>(() => service.UpsertAsync(
            new UpsertJiraIntegrationCommand(Project, "javascript:alert(1)", "ABC", "qa@example.com", "x", null, null, true),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.UpsertAsync(
            new UpsertJiraIntegrationCommand(Project, "https://jira.example.com", "ABC", "qa@example.com", null, null, null, true),
            CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_NonAdmin_Forbidden()
    {
        var tester = new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "user-1",
            Roles = ["tester"], Permissions = RolePermissions.Resolve(["tester"]),
        };
        var memberships = new StubMembershipStore();
        memberships.Add("user-1", Project);
        var service = new JiraIntegrationService(new FakeStore(),
            new AuthorizationService(tester, memberships), tester,
            new SystemDateTimeProvider(), new FakeAudit());
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpsertAsync(
            new UpsertJiraIntegrationCommand(Project, "https://jira.example.com", "ABC", "qa@example.com", "x", null, null, true),
            CancellationToken.None));
    }

    [Fact]
    public async Task GetStatus_Unconfigured_ProjectScoped()
    {
        var service = Create(new FakeStore(), new FakeAudit());
        var status = await service.GetStatusAsync(Project, CancellationToken.None);
        Assert.False(status.Configured);
        Assert.Equal("jira", status.Provider);
    }
}
