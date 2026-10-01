using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Application.Variables;
using AutoTestAi.Application.Webhooks;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3B: delivery processing — filtering, deterministic fan-out,
/// TriggerType.Ci, secret-mapping ownership, retry. No live dependencies.</summary>
public sealed class WebhookProcessingTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Suite = Guid.NewGuid();
    private static readonly Guid Environment = Guid.NewGuid();
    private static readonly Guid Integration = Guid.NewGuid();

    private sealed class AllowAuth : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    private sealed class FakeDeliveries : IWebhookDeliveryStore
    {
        public readonly Dictionary<Guid, WebhookDelivery> Rows = new();
        public Task<WebhookDelivery?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var d) ? d : null);
        public Task<WebhookDelivery?> FindByIntegrationAndDeliveryAsync(Guid i, string d, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(x => x.IntegrationId == i && x.DeliveryId == d));
        public Task<IReadOnlyList<WebhookDelivery>> ListByIntegrationAsync(Guid i, int skip, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WebhookDelivery>>(Rows.Values.Where(x => x.IntegrationId == i).ToList());
        public Task<int> CountByIntegrationAsync(Guid i, CancellationToken ct)
            => Task.FromResult(Rows.Values.Count(x => x.IntegrationId == i));
        public Task<IReadOnlyList<WebhookDelivery>> ListStaleAcceptedAsync(DateTimeOffset olderThan, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WebhookDelivery>>(Rows.Values
                .Where(x => x.ProcessingStatus == WebhookProcessingStatus.Accepted && x.UpdatedAt < olderThan).ToList());
        public Task AddAsync(WebhookDelivery d, CancellationToken ct) { Rows[d.Id] = d; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeIntegrations : IIntegrationStore
    {
        public readonly Dictionary<Guid, Integration> Rows = new();
        public Task<Integration?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var r) ? r : null);
        public Task<Integration?> FindByProjectAndProviderAsync(Guid p, string provider, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(x => x.ProjectId == p && x.Provider == provider));
        public Task AddAsync(Integration i, CancellationToken ct) { Rows[i.Id] = i; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeProjects : IProjectStore
    {
        public readonly Dictionary<Guid, TestEnvironment> Environments = new();
        public Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Environments.TryGetValue(id, out var e) ? e : null);
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<Project?>(null);
        public Task<int> CountAccessibleAsync(string? e, bool a, string? s, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(string? e, bool a, string? s, int sk, int t, CancellationToken ct) => throw new NotImplementedException();
        public Task<Project?> GetByKeyAsync(string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddProjectAsync(Project p, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetUserByIdAsync(Guid u, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetUserByEmailAsync(string e, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Role?> GetRoleByIdAsync(Guid r, CancellationToken ct) => throw new NotImplementedException();
        public Task<Role> GetOrCreateRoleAsync(string n, string? d, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> IsMemberAsync(Guid p, Guid u, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<MemberRow>> ListMembersAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task<ProjectMember?> FindMemberAsync(Guid p, Guid u, CancellationToken ct) => throw new NotImplementedException();
        public Task AddMemberAsync(ProjectMember m, CancellationToken ct) => throw new NotImplementedException();
        public Task RemoveMemberAsync(ProjectMember m, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TestEnvironment>> ListEnvironmentsAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task AddEnvironmentAsync(TestEnvironment e, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RecordAuditAsync(AuditEvent e, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSuites : ITestSuiteLookup
    {
        public readonly Dictionary<Guid, TestSuite> Suites = new();
        public Task<TestSuite?> GetSuiteByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Suites.TryGetValue(id, out var s) ? s : null);
    }

    private sealed class FakeMembers : ISuiteMemberLookup
    {
        public readonly List<SuiteMemberRow> Members = new();
        public Task<IReadOnlyList<SuiteMemberRow>> ListMembersAsync(Guid suiteId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SuiteMemberRow>>(Members.ToList());
    }

    private sealed class FakeCases : ITestCaseStore
    {
        public readonly Dictionary<Guid, TestCase> Cases = new();
        public readonly Dictionary<Guid, TestCaseVersion> Latest = new();
        public Task<IReadOnlyDictionary<Guid, TestCaseVersion>> GetLatestVersionsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, TestCaseVersion>>(
                ids.Where(Latest.ContainsKey).ToDictionary(id => id, id => Latest[id]));
        public Task<TestCase?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Cases.TryGetValue(id, out var c) ? c : null);
        public Task<int> CountAsync(Guid p, string? s, TestCaseStatusFilter f, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TestCase>> ListAsync(Guid p, string? s, TestCaseStatusFilter f, int sk, int t, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCase?> GetByKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddTestCaseAsync(TestCase c, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<TestCaseVersion?> GetVersionByIdAsync(Guid v, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TestCaseVersion>> ListVersionsAsync(Guid c, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseVersion> AddNextVersionAsync(Guid c, Func<int, TestCaseVersion> f, CancellationToken ct) => throw new NotImplementedException();
        public Task AddVersionAsync(TestCaseVersion v, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeExecutions : ITestExecutionService
    {
        public readonly List<StartExecutionCommand> Started = new();
        private readonly Dictionary<string, StartExecutionResultDto> _byKey = new();
        public Task<StartExecutionResultDto> StartAsync(StartExecutionCommand command, CancellationToken ct)
            => StartAsSystemAsync(command, ct);
        public Task<StartExecutionResultDto> StartAsSystemAsync(StartExecutionCommand command, CancellationToken ct)
        {
            Started.Add(command);
            if (command.IdempotencyKey is not null && _byKey.TryGetValue(command.IdempotencyKey, out var existing))
                return Task.FromResult(existing with { Duplicated = true });
            var result = new StartExecutionResultDto(Guid.NewGuid(), Guid.NewGuid(), command.ProjectId,
                Guid.NewGuid(), command.TestCaseVersionId, "Queued", "wf-test", DateTimeOffset.UtcNow, false);
            if (command.IdempotencyKey is not null)
                _byKey[command.IdempotencyKey] = result;
            return Task.FromResult(result);
        }
        public Task<PagedResult<ExecutionListItemDto>> ListAsync(Guid p, int page, int size, ExecutionFilters f, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionDetailDto> GetAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionStepDto>> ListStepsAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionLogDto>> ListLogsAsync(Guid e, long? a, int t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionArtifactDto>> ListArtifactsAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
        public Task<ArtifactDownloadDto> GetArtifactDownloadUrlAsync(Guid e, Guid a, CancellationToken ct) => throw new NotImplementedException();
        public Task<CancelExecutionResultDto> CancelAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public readonly Dictionary<Guid, SecretMetadata> Rows = new();
        public Task<SecretMetadata?> GetMetadataAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var m) ? m : null);
        public Task<SecretMetadata> CreateAsync(Guid p, Guid e, string n, string v, string? d, CancellationToken ct) => throw new NotImplementedException();
        public Task<SecretMetadata> UpdateAsync(Guid id, string? n, string? v, string? d, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SecretMetadata>> ListMetadataAsync(Guid p, Guid? e, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeResolver : ISecretResolver
    {
        public Task<bool> ExistsAsync(string r, CancellationToken ct) => Task.FromResult(true);
        public Task<string> ResolveAsync(string r, CancellationToken ct) => Task.FromResult("s");
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixture
    {
        public FakeDeliveries Deliveries = new();
        public FakeIntegrations Integrations = new();
        public FakeProjects Projects = new();
        public FakeSuites Suites = new();
        public FakeMembers Members = new();
        public FakeCases Cases = new();
        public FakeExecutions Executions = new();
        public FakeSecrets Secrets = new();
        public FakeAudit Audit = new();
        public WebhookQueue Queue = new();
        public WebhookProcessingService Service = null!;

        public Fixture()
        {
            Service = new WebhookProcessingService(Deliveries, Integrations, Projects, Suites, Members,
                Cases, Executions, new FakeResolver(), Secrets, Queue, Audit, new FixedClock(),
                Options.Create(new WebhookOptions()), NullLogger<WebhookProcessingService>.Instance);
            Suites.Suites[Suite] = new TestSuite { Id = Suite, ProjectId = Project, Name = "Smoke", Status = ProjectStatus.Active };
            Projects.Environments[Environment] = new TestEnvironment { Id = Environment, ProjectId = Project, Name = "prod", Status = ProjectStatus.Active };
        }

        public void AddIntegration(string configJson, string provider = "github")
        {
            Integrations.Rows[Integration] = new Integration
            {
                Id = Integration,
                ProjectId = Project,
                Provider = provider,
                IntegrationType = CiProviderNames.IntegrationType,
                Configuration = JsonDocument.Parse(configJson),
                SecretReference = "env_secret:11111111-1111-1111-1111-111111111111",
                Status = IntegrationStatus.Active,
            };
        }

        public WebhookDelivery AddDelivery(string eventType = "push", string? branch = "main")
        {
            var metadata = JsonSerializer.Serialize(new
            {
                provider = "github",
                deliveryId = "d-1",
                eventType,
                branch,
                commitSha = "abc",
                repository = "octo/repo",
            });
            var delivery = new WebhookDelivery
            {
                IntegrationId = Integration,
                ProjectId = Project,
                Provider = "github",
                DeliveryId = "d-1",
                EventType = eventType,
                ReceivedAt = new FixedClock().UtcNow,
                PayloadHash = "hash",
                VerificationStatus = WebhookVerificationStatus.Verified,
                ProcessingStatus = WebhookProcessingStatus.Accepted,
                NormalizedMetadataJson = metadata,
            };
            Deliveries.Rows[delivery.Id] = delivery;
            return delivery;
        }

        public void AddExecutableMember(Guid caseId, int order)
        {
            Members.Members.Add(new SuiteMemberRow(caseId, order));
            Cases.Cases[caseId] = new TestCase { Id = caseId, ProjectId = Project, TestKey = "K-" + order, Title = "t", Status = TestCaseStatus.Active };
            Cases.Latest[caseId] = new TestCaseVersion { Id = Guid.NewGuid(), TestCaseId = caseId, VersionNumber = 1, ReviewStatus = ReviewStatus.Approved };
        }
    }

    private static string Config(bool withSuite = true, string events = """["push"]""")
        => "{\"defaultSuiteId\":\"" + (withSuite ? Suite.ToString() : "") + "\",\"defaultEnvironmentId\":\"" + Environment +
           "\",\"eventAllowlist\":" + events +
           ",\"branchAllowlist\":[],\"repositoryAllowlist\":[],\"variableMapping\":{},\"username\":null,\"secretMapping\":{}}";

    [Fact]
    public async Task UnsupportedEvent_IsIgnoredWithoutExecution()
    {
        var f = new Fixture();
        f.AddIntegration(Config());
        var delivery = f.AddDelivery("pull_request");
        await f.Service.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(WebhookProcessingStatus.Ignored, delivery.ProcessingStatus);
        Assert.Empty(f.Executions.Started);
        Assert.Contains(f.Audit.Actions, a => a == "webhook.rejected");
    }

    [Fact]
    public async Task MissingSuite_IsRejected()
    {
        var f = new Fixture();
        f.AddIntegration(Config(withSuite: false));
        var delivery = f.AddDelivery();
        await f.Service.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(WebhookProcessingStatus.Rejected, delivery.ProcessingStatus);
        Assert.Equal("missing_suite", delivery.FailureReason);
        Assert.Empty(f.Executions.Started);
    }

    [Fact]
    public async Task HappyPath_FansOutDeterministicallyWithCiTrigger()
    {
        var f = new Fixture();
        f.AddIntegration(Config());
        // Insert out of order: deterministic (ExecutionOrder, TestCaseId) wins.
        var caseB = Guid.NewGuid();
        var caseA = Guid.NewGuid();
        f.AddExecutableMember(caseB, 2);
        f.AddExecutableMember(caseA, 1);
        var delivery = f.AddDelivery();
        await f.Service.ProcessAsync(delivery.Id, CancellationToken.None);

        Assert.Equal(WebhookProcessingStatus.Triggered, delivery.ProcessingStatus);
        Assert.Equal(2, delivery.TriggeredCount);
        Assert.Equal(2, f.Executions.Started.Count);
        // Deterministic member order.
        Assert.Equal(f.Cases.Latest[caseA].Id, f.Executions.Started[0].TestCaseVersionId);
        Assert.Equal(f.Cases.Latest[caseB].Id, f.Executions.Started[1].TestCaseVersionId);
        foreach (var command in f.Executions.Started)
        {
            Assert.Equal(TriggerType.Ci, command.Trigger);
            Assert.Equal(Suite, command.SuiteId);
            Assert.Equal(Environment, command.EnvironmentId);
            Assert.NotNull(command.IdempotencyKey);
            Assert.StartsWith("wh:", command.IdempotencyKey, StringComparison.Ordinal);
        }
        Assert.Equal(f.Executions.Started[0].IdempotencyKey,
            WebhookProcessingService.IdempotencyKeyFor(Integration, "d-1", 0));
        Assert.Contains(f.Audit.Actions, a => a == "webhook.execution_requested");
    }

    [Fact]
    public async Task Retry_ConvergesOnSameIdempotencyKeys()
    {
        var f = new Fixture();
        f.AddIntegration(Config());
        f.AddExecutableMember(Guid.NewGuid(), 1);
        var delivery = f.AddDelivery();
        await f.Service.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(WebhookProcessingStatus.Triggered, delivery.ProcessingStatus);

        // Terminal deliveries are not reprocessed.
        await f.Service.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.Single(f.Executions.Started);

        // Failed deliveries retry and converge via execution idempotency.
        delivery.ProcessingStatus = WebhookProcessingStatus.Failed;
        await f.Service.RetryAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(WebhookProcessingStatus.Accepted, delivery.ProcessingStatus);
        await f.Service.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(2, f.Executions.Started.Count);
        Assert.Equal(f.Executions.Started[0].IdempotencyKey, f.Executions.Started[1].IdempotencyKey);
    }

    [Fact]
    public async Task Retry_RejectsNonFailedDeliveries()
    {
        var f = new Fixture();
        f.AddIntegration(Config());
        var delivery = f.AddDelivery();
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.RetryAsync(delivery.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UnownedSecretMapping_FailsDelivery()
    {
        var f = new Fixture();
        var otherSecret = Guid.NewGuid();
        f.Secrets.Rows[otherSecret] = new SecretMetadata(otherSecret, Project, Guid.NewGuid(),
            "OTHER", null, SecretReference.Create(otherSecret), true, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var configJson = "{\"defaultSuiteId\":\"" + Suite + "\",\"defaultEnvironmentId\":\"" + Environment +
            "\",\"eventAllowlist\":[],\"branchAllowlist\":[],\"repositoryAllowlist\":[],\"variableMapping\":{}," +
            "\"username\":null,\"secretMapping\":{\"API_KEY\":\"" + SecretReference.Create(otherSecret) + "\"}}";
        f.AddIntegration(configJson);
        f.AddExecutableMember(Guid.NewGuid(), 1);
        var delivery = f.AddDelivery();
        await f.Service.ProcessAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(WebhookProcessingStatus.Failed, delivery.ProcessingStatus);
        Assert.Equal("invalid_secret_mapping", delivery.FailureReason);
        Assert.Empty(f.Executions.Started);
    }
}
