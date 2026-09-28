using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 5 §47: approval gate, exact-version binding, authorization,
/// idempotency, cancellation, and audit for execution creation.</summary>
public sealed class TestExecutionServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    // ---------- fakes ----------

    private sealed class FakeExecutionStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<ExecutionStepResult> Steps = new();
        public readonly List<ExecutionLog> Logs = new();
        public readonly List<ExecutionArtifact> Artifacts = new();
        private long _logId;

        private readonly FakeTestCaseStore _cases;

        public FakeExecutionStore(FakeTestCaseStore cases) => _cases = cases;

        public Task<int> CountAsync(Guid projectId, string? status, Guid? testCaseId, CancellationToken ct)
            => Task.FromResult(Apply(projectId, status, testCaseId).Count);

        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(
            Guid projectId, string? status, Guid? testCaseId, int skip, int take, CancellationToken ct)
        {
            var rows = Apply(projectId, status, testCaseId)
                .OrderByDescending(e => e.CreatedAt).Skip(skip).Take(take)
                .Select(e =>
                {
                    var test = Tests.Where(t => t.ExecutionId == e.Id).OrderBy(t => t.CreatedAt).First();
                    var testCase = _cases.Cases.First(c => c.Id == test.TestCaseId);
                    var versionNumber = test.TestCaseVersionId is null ? 0
                        : _cases.Versions.First(v => v.Id == test.TestCaseVersionId.Value).VersionNumber;
                    return new ExecutionListRow(e, test, testCase.TestKey, testCase.Title, versionNumber);
                }).ToList();
            return Task.FromResult<IReadOnlyList<ExecutionListRow>>(rows);
        }

        private List<Execution> Apply(Guid projectId, string? status, Guid? testCaseId)
        {
            IEnumerable<Execution> query = Executions.Where(e => e.ProjectId == projectId);
            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(e => e.Status.ToString() == status);
            if (testCaseId.HasValue)
                query = query.Where(e => Tests.Any(t => t.ExecutionId == e.Id && t.TestCaseId == testCaseId.Value));
            return query.ToList();
        }

        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.Id == id));
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Tests.FirstOrDefault(t => t.Id == id));
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Tests.Where(t => t.ExecutionId == id).ToList());
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid projectId, string key, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.ProjectId == projectId && e.IdempotencyKey == key));
        public Task AddExecutionAsync(Execution e, CancellationToken ct) { Executions.Add(e); return Task.CompletedTask; }
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) { Tests.Add(t); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionStepResult>>(Steps.Where(s => s.ExecutionTestId == id).ToList());
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct)
        { Steps.AddRange(rows); return Task.CompletedTask; }
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct)
        { Steps.RemoveAll(s => s.ExecutionTestId == id); return Task.CompletedTask; }
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? afterId, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionLog>>(Logs.Where(l => l.ExecutionTestId == id && (!afterId.HasValue || l.Id > afterId.Value)).OrderBy(l => l.Id).Take(take).ToList());
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct)
        { foreach (var row in rows) { row.Id = ++_logId; Logs.Add(row); } return Task.CompletedTask; }
        public Task DeleteLogsAsync(Guid id, CancellationToken ct)
        { Logs.RemoveAll(l => l.ExecutionTestId == id); return Task.CompletedTask; }
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionArtifact>>(Artifacts.Where(a => a.ExecutionTestId == id).ToList());
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Artifacts.FirstOrDefault(a => a.Id == id));
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) { Artifacts.Add(a); return Task.CompletedTask; }
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct)
        { Artifacts.RemoveAll(a => a.ExecutionTestId == id); return Task.CompletedTask; }
    }

    private sealed class FakeProjectStore : IProjectStore
    {
        public readonly List<AuditEvent> Audits = new();
        public readonly Dictionary<Guid, TestEnvironment> Environments = new();
        public Task RecordAuditAsync(AuditEvent e, CancellationToken ct) { Audits.Add(e); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Environments.TryGetValue(id, out var env) ? env : null);
        public Task<int> CountAccessibleAsync(string? e, bool a, string? s, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(string? e, bool a, string? s, int sk, int t, CancellationToken ct) => throw new NotImplementedException();
        public Task<Project?> GetByIdAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
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
    }

    private sealed class FakeCoordinator : IExecutionWorkflowCoordinator
    {
        public bool IsConfigured { get; set; } = true;
        public Exception? StartFailure { get; set; }
        public bool CancelResult { get; set; } = true;
        public readonly List<(Guid ExecutionId, Guid ProjectId)> Started = new();
        public readonly List<string> Cancelled = new();
        public Task<string> StartAsync(Guid e, Guid p, CancellationToken ct)
        {
            if (StartFailure is not null) throw StartFailure;
            Started.Add((e, p));
            return Task.FromResult($"wf-test-{e:N}");
        }
        public Task<bool> CancelAsync(string workflowId, CancellationToken ct)
        {
            Cancelled.Add(workflowId);
            return Task.FromResult(CancelResult);
        }
    }

    private sealed record PublishedEvent(Guid ExecutionId, string EventName);

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        public readonly List<PublishedEvent> Events = new();
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
        {
            Events.Add(new PublishedEvent(executionId, eventName));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured { get; set; } = true;
        public readonly List<string> UploadedKeys = new();
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct)
        { UploadedKeys.Add(key); return Task.CompletedTask; }
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}?exp={expirySeconds}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(IsConfigured);
    }

    // ---------- builders ----------

    private sealed record Harness(
        TestExecutionService Service,
        FakeExecutionStore Store,
        FakeTestCaseStore Cases,
        FakeProjectStore Projects,
        FakeCoordinator Workflows,
        FakePublisher Events,
        FakeArtifacts Artifacts,
        Guid ApprovedVersionId,
        Guid PendingVersionId,
        Guid ForeignVersionId);

    private static Harness Create(ICurrentUserService user, bool memberA = true, bool memberB = false)
    {
        var cases = new FakeTestCaseStore();
        var store = new FakeExecutionStore(cases);
        var projects = new FakeProjectStore();
        var memberships = new StubMembershipStore();
        if (memberA) memberships.Add("user-1", ProjectA);
        if (memberB) memberships.Add("user-1", ProjectB);
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        var audit = new AuditService(projects, user, directory, NullLogger<AuditService>.Instance);
        var workflows = new FakeCoordinator();
        var events = new FakePublisher();
        var artifacts = new FakeArtifacts();

        var caseA = new TestCase
        {
            ProjectId = ProjectA, TestKey = "LOGIN-001", Title = "Login works",
            Framework = "playwright", Platform = "web",
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        var caseB = new TestCase
        {
            ProjectId = ProjectB, TestKey = "OTHER-001", Title = "Other",
            Priority = Priority.Medium, Status = TestCaseStatus.Draft, SourceType = "manual",
        };
        cases.Cases.AddRange(new[] { caseA, caseB });
        var approved = new TestCaseVersion
        {
            TestCaseId = caseA.Id, VersionNumber = 1, SourceCode = "// v1",
            StructuredSteps = JsonDocument.Parse("""[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
            ReviewStatus = ReviewStatus.Approved,
        };
        var pending = new TestCaseVersion
        {
            TestCaseId = caseA.Id, VersionNumber = 2, SourceCode = "// v2",
            StructuredSteps = JsonDocument.Parse("""[{"order":1,"action":"click","target":"#x"}]"""),
            ReviewStatus = ReviewStatus.Pending,
        };
        var foreign = new TestCaseVersion
        {
            TestCaseId = caseB.Id, VersionNumber = 1, SourceCode = "// b",
            StructuredSteps = JsonDocument.Parse("""[{"order":1,"action":"navigate","target":"https://b.test"}]"""),
            ReviewStatus = ReviewStatus.Approved,
        };
        cases.Versions.AddRange(new[] { approved, pending, foreign });

        var service = new TestExecutionService(
            store, cases, projects, user, authorization, directory,
            new SystemDateTimeProvider(), audit, events, workflows, artifacts);
        return new Harness(service, store, cases, projects, workflows, events, artifacts,
            approved.Id, pending.Id, foreign.Id);
    }

    private static StubCurrentUser Executor() => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = "user-1",
        Roles = ["tester"],
        Permissions = RolePermissions.Resolve(["tester"]),
    };

    private static StubCurrentUser Viewer() => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = "user-1",
        Roles = ["viewer"],
        Permissions = RolePermissions.Resolve(["viewer"]),
    };

    // ---------- start ----------

    [Fact]
    public async Task Start_ApprovedVersion_PersistsExactBinding_AndStartsWorkflow()
    {
        var h = Create(Executor());

        var result = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId, null, "chromium", null),
            CancellationToken.None);

        Assert.Equal("Queued", result.Status);
        Assert.False(result.Duplicated);
        Assert.StartsWith("wf-test-", result.WorkflowId);
        var execution = Assert.Single(h.Store.Executions);
        Assert.Equal(ProjectA, execution.ProjectId);
        Assert.NotNull(execution.WorkflowId);
        var test = Assert.Single(h.Store.Tests);
        Assert.Equal(execution.Id, test.ExecutionId);
        Assert.Equal(h.ApprovedVersionId, test.TestCaseVersionId); // exact binding, not v2/latest
        Assert.Equal("playwright", test.Framework);
        Assert.Equal("chromium", test.Browser);
        Assert.Equal(1, test.Attempt);
        var started = Assert.Single(h.Workflows.Started);
        Assert.Equal(execution.Id, started.ExecutionId);
        Assert.Contains(h.Projects.Audits, a => a.Action == "execution.requested");
        Assert.Contains(h.Events.Events, e => e.EventName == ExecutionEvents.ExecutionStatusChanged);
    }

    [Fact]
    public async Task Start_PendingVersion_Returns409_AndCreatesNothing()
    {
        var h = Create(Executor());
        var ex = await Assert.ThrowsAsync<ConflictException>(() => h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.PendingVersionId), CancellationToken.None));
        Assert.Contains("Pending", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Store.Executions);
        Assert.Empty(h.Workflows.Started);
    }

    [Fact]
    public async Task Start_CrossProjectVersion_Returns403()
    {
        var h = Create(Executor(), memberA: true, memberB: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ForeignVersionId), CancellationToken.None));
        Assert.Empty(h.Store.Executions);
    }

    [Fact]
    public async Task Start_UnknownVersion_Returns404()
    {
        var h = Create(Executor());
        // Admin passes authorization: proves genuine 404 after the boundary.
        var admin = Create(new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "user-1",
            Roles = ["admin"], Permissions = RolePermissions.Resolve(["admin"]),
        });
        await Assert.ThrowsAsync<NotFoundException>(() => admin.Service.StartAsync(
            new StartExecutionCommand(ProjectA, Guid.NewGuid()), CancellationToken.None));
        _ = h;
    }

    [Fact]
    public async Task Start_ViewerAndOutsider_AreForbidden()
    {
        var viewer = Create(Viewer());
        await Assert.ThrowsAsync<ForbiddenException>(() => viewer.Service.StartAsync(
            new StartExecutionCommand(ProjectA, viewer.ApprovedVersionId), CancellationToken.None));

        var outsider = Create(Executor(), memberA: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => outsider.Service.StartAsync(
            new StartExecutionCommand(ProjectA, outsider.ApprovedVersionId), CancellationToken.None));
    }

    [Fact]
    public async Task Start_InvalidInput_Returns400_WithoutWorkflow()
    {
        var h = Create(Executor());
        var ex = await Assert.ThrowsAsync<ValidationException>(() => h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId, null, "netscape", null),
            CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "browser");
        Assert.Empty(h.Workflows.Started);
    }

    [Fact]
    public async Task Start_TemporalUnconfigured_Returns503_AndCreatesNoRecord()
    {
        var h = Create(Executor());
        h.Workflows.IsConfigured = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None));
        Assert.Empty(h.Store.Executions);
    }

    [Fact]
    public async Task Start_WorkflowStartFailure_MarksError_AndRethrows()
    {
        var h = Create(Executor());
        h.Workflows.StartFailure = new InvalidOperationException("Temporal down");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None));
        var execution = Assert.Single(h.Store.Executions);
        Assert.Equal("Error", execution.Status.ToString());
        Assert.Contains(h.Projects.Audits, a => a.Action == "execution.failed");
    }

    [Fact]
    public async Task Start_IdempotencyKey_ReturnsOriginalExecution()
    {
        var h = Create(Executor());
        var first = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId, null, null, "key-1"),
            CancellationToken.None);
        var second = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId, null, null, "key-1"),
            CancellationToken.None);
        Assert.False(first.Duplicated);
        Assert.True(second.Duplicated);
        Assert.Equal(first.ExecutionId, second.ExecutionId);
        Assert.Single(h.Store.Executions);
        Assert.Single(h.Workflows.Started);
    }

    // ---------- history ----------

    [Fact]
    public async Task List_ReturnsProjectScopedHistory_WithStatusFilter()
    {
        var h = Create(Executor());
        await h.Service.StartAsync(new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None);
        var page = await h.Service.ListAsync(ProjectA, 1, 25,
            new ExecutionFilters(null, null), CancellationToken.None);
        Assert.Equal(1, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("LOGIN-001", item.TestKey);
        Assert.Equal(1, item.TestCaseVersionNumber);

        var empty = await h.Service.ListAsync(ProjectA, 1, 25,
            new ExecutionFilters("Passed", null), CancellationToken.None);
        Assert.Equal(0, empty.TotalCount);

        var bad = await Assert.ThrowsAsync<ValidationException>(() => h.Service.ListAsync(
            ProjectA, 1, 25, new ExecutionFilters("Bogus", null), CancellationToken.None));
        Assert.Contains(bad.Errors, e => e.Field == "status");
    }

    [Fact]
    public async Task Get_CrossProject_Returns403()
    {
        var h = Create(Executor(), memberA: true, memberB: false);
        var started = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None);
        // Member of A only: execution in B is forbidden even with a valid id.
        var other = Create(Executor(), memberA: false, memberB: true);
        var foreignCase = h.Cases.Cases.First(c => c.ProjectId == ProjectB);
        var execution = h.Store.Executions.First();
        _ = foreignCase;
        await Assert.ThrowsAsync<ForbiddenException>(() => other.Service.GetAsync(execution.Id, CancellationToken.None));
        _ = started;
    }

    // ---------- cancel ----------

    [Fact]
    public async Task Cancel_Queued_TransitionsImmediately_AndIsIdempotent()
    {
        var h = Create(Executor());
        var started = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None);

        var first = await h.Service.CancelAsync(started.ExecutionId, CancellationToken.None);
        Assert.Equal("Cancelled", first.Status);
        Assert.True(first.CancellationRequested);
        var second = await h.Service.CancelAsync(started.ExecutionId, CancellationToken.None);
        Assert.Equal("Cancelled", second.Status);
        Assert.False(second.CancellationRequested);
        Assert.Empty(h.Workflows.Cancelled); // never started: no workflow call needed
        Assert.Contains(h.Projects.Audits, a => a.Action == "execution.cancelled");
    }

    [Fact]
    public async Task Cancel_Running_RequestsWorkflowCancellation_WithoutMutatingState()
    {
        var h = Create(Executor());
        var started = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None);
        var execution = h.Store.Executions.First();
        execution.Status = Domain.Enums.ExecutionStatus.Running;
        h.Store.Tests.First().Status = Domain.Enums.ExecutionTestStatus.Running;

        var result = await h.Service.CancelAsync(started.ExecutionId, CancellationToken.None);
        Assert.Equal("Running", result.Status);
        Assert.True(result.CancellationRequested);
        Assert.Single(h.Workflows.Cancelled); // Temporal cancel requested
        Assert.Equal("Running", h.Store.Executions.First().Status.ToString()); // terminal state arrives via workflow
    }

    [Fact]
    public async Task Cancel_WithoutPermission_IsForbidden()
    {
        var h = Create(Executor());
        var started = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None);
        var viewer = Create(Viewer());
        // Viewer is a project member (seeded) but lacks executions.cancel.
        await Assert.ThrowsAsync<ForbiddenException>(
            () => viewer.Service.CancelAsync(started.ExecutionId, CancellationToken.None));
    }

    // ---------- artifacts ----------

    [Fact]
    public async Task ArtifactDownload_MissingArtifact_Returns404()
    {
        var h = Create(Executor());
        var started = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None);
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.GetArtifactDownloadUrlAsync(
            started.ExecutionId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ArtifactDownload_ReturnsPresignedUrl_WithoutCredentials()
    {
        var h = Create(Executor());
        var started = await h.Service.StartAsync(
            new StartExecutionCommand(ProjectA, h.ApprovedVersionId), CancellationToken.None);
        var test = h.Store.Tests.First();
        var artifact = new ExecutionArtifact
        {
            ExecutionTestId = test.Id, ArtifactType = "screenshot",
            StorageKey = "projects/x/executions/y/shot.png", FileName = "step-001.png",
            ContentType = "image/png", SizeBytes = 12,
        };
        h.Store.Artifacts.Add(artifact);

        var download = await h.Service.GetArtifactDownloadUrlAsync(
            started.ExecutionId, artifact.Id, CancellationToken.None);
        Assert.StartsWith("https://artifacts.example/", download.DownloadUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretKey", download.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- redaction ----------

    [Fact]
    public void ValueRedactor_MasksPasswordTargetsOnly()
    {
        Assert.Equal("[REDACTED]", ExecutionValueRedactor.RedactStepValue("fill", "#password", "hunter2"));
        Assert.Equal("qa-user", ExecutionValueRedactor.RedactStepValue("fill", "#username", "qa-user"));
        Assert.Null(ExecutionValueRedactor.RedactStepValue("click", "#x", null));
    }
}
