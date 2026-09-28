using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 6 §54: defect creation authority, lifecycle, audit, and isolation.</summary>
public sealed class DefectServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    // ---------- fakes ----------

    private sealed class FakeDefectStore : IDefectStore
    {
        public readonly List<Defect> Defects = new();
        public Task<int> CountAsync(Guid p, string? s, string? sev, string? c, Guid? t, string? q, CancellationToken ct)
            => Task.FromResult(Defects.Where(d => d.ProjectId == p).ToList().Count);
        public Task<IReadOnlyList<Defect>> ListAsync(Guid p, string? s, string? sev, string? c, Guid? t, string? q, int sk, int ta, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Defect>>(Defects.Where(d => d.ProjectId == p).OrderByDescending(d => d.CreatedAt).Skip(sk).Take(ta).ToList());
        public Task<Defect?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Defects.FirstOrDefault(d => d.Id == id));
        public Task AddAsync(Defect d, CancellationToken ct) { Defects.Add(d); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeExecutionStore : IExecutionStore
    {
        public readonly List<Execution> Executions = new();
        public readonly List<ExecutionTest> Tests = new();
        public readonly List<FailureAnalysis> Analyses = new();
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Executions.FirstOrDefault(e => e.Id == id));
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Tests.FirstOrDefault(t => t.Id == id));
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(Tests.Where(t => t.ExecutionId == id).ToList());
        public Task<FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct)
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
        public Task<IReadOnlyList<FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAnalysisAsync(FailureAnalysis a, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string a, string e, string? id, Guid? p, string? m, CancellationToken ct)
        { Actions.Add(a); return Task.CompletedTask; }
    }

    // ---------- builders ----------

    private sealed record Harness(
        DefectService Service, FakeDefectStore Store, FakeExecutionStore Executions,
        FakeAudit Audit, Guid ExecutionId, Guid TestId, Guid AnalysisId);

    private static StubCurrentUser Manager() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "user-1",
        Roles = ["qa-lead"], Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static Harness Create(ICurrentUserService? user = null, bool member = true)
    {
        user ??= Manager();
        var store = new FakeDefectStore();
        var executions = new FakeExecutionStore();
        var cases = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        if (member) memberships.Add("user-1", ProjectA);
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        var audit = new FakeAudit();

        var testCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "LOGIN-001", Title = "Login",
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        var execution = new Execution { ProjectId = ProjectA, Status = ExecutionStatus.Failed };
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = testCase.Id,
            TestCaseVersionId = Guid.NewGuid(), Status = ExecutionTestStatus.Failed,
            FailureClassification = FailureClassification.ApplicationDefect, Attempt = 1,
        };
        executions.Executions.Add(execution);
        executions.Tests.Add(test);
        var analysis = new FailureAnalysis
        {
            ExecutionTestId = test.Id, Attempt = 1, Status = AnalysisStatus.Completed,
            Classification = FailureClassification.ApplicationDefect,
            Summary = "App returned 500.", Confidence = 0.8m,
        };
        executions.Analyses.Add(analysis);

        var service = new DefectService(
            store, executions, cases, user, authorization, directory,
            new SystemDateTimeProvider(), audit);
        return new Harness(service, store, executions, audit, execution.Id, test.Id, analysis.Id);
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_DerivesRelationships_FromExecution()
    {
        var h = Create();
        var created = await h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "Login 500", "Fails on staging.", "High", h.AnalysisId),
            CancellationToken.None);

        Assert.Equal("Login 500", created.Title);
        Assert.Equal("High", created.Severity);
        Assert.Equal("Open", created.Status);
        Assert.Equal("ApplicationDefect", created.FailureClassification);
        Assert.Equal(h.ExecutionId, created.ExecutionId);
        Assert.Equal(h.TestId, created.ExecutionTestId);
        Assert.NotNull(created.TestCaseVersionId);
        Assert.Equal("LOGIN-001", created.TestKey);
        Assert.Equal(h.AnalysisId, created.FailureAnalysisId);
        Assert.NotNull(created.Analysis);
        Assert.Equal(0.8m, created.AiConfidence);
        Assert.Contains(h.Audit.Actions, a => a == "defect.created_from_analysis");
    }

    [Fact]
    public async Task Create_WithoutAnalysis_Succeeds()
    {
        var h = Create();
        var created = await h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "Login 500", null, null),
            CancellationToken.None);
        Assert.Equal("Medium", created.Severity);
        Assert.Null(created.FailureAnalysisId);
        Assert.Contains(h.Audit.Actions, a => a == "defect.created");
    }

    [Fact]
    public async Task Create_NonFailedExecution_Returns409()
    {
        var h = Create();
        h.Executions.Tests.First().Status = ExecutionTestStatus.Passed;
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "X", null, null), CancellationToken.None));
        Assert.Empty(h.Store.Defects);
    }

    [Fact]
    public async Task Create_CrossProjectExecution_Returns403()
    {
        var h = Create();
        h.Executions.Executions.First().ProjectId = ProjectB;
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "X", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_ForeignAnalysis_Returns400()
    {
        var h = Create();
        await Assert.ThrowsAsync<ValidationException>(() => h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "X", null, null, Guid.NewGuid()),
            CancellationToken.None));
    }

    [Fact]
    public async Task Create_InvalidInput_Returns400()
    {
        var h = Create();
        var ex = await Assert.ThrowsAsync<ValidationException>(() => h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "", null, "Bogus"), CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "title");
        Assert.Contains(ex.Errors, e => e.Field == "severity");
    }

    [Fact]
    public async Task Create_ViewerForbidden_OutsiderForbidden()
    {
        var viewer = Create(new StubCurrentUser
        {
            IsAuthenticated = true, ExternalIdentityId = "user-1",
            Roles = ["viewer"], Permissions = RolePermissions.Resolve(["viewer"]),
        });
        await Assert.ThrowsAsync<ForbiddenException>(() => viewer.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, viewer.ExecutionId, "X", null, null), CancellationToken.None));

        var outsider = Create(Manager(), member: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => outsider.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, outsider.ExecutionId, "X", null, null), CancellationToken.None));
    }

    // ---------- lifecycle ----------

    [Fact]
    public async Task Status_Transitions_Validated_AndAudited()
    {
        var h = Create();
        var created = await h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "X", null, null), CancellationToken.None);

        var progress = await h.Service.ChangeStatusAsync(
            created.Id, new ChangeDefectStatusCommand("InProgress"), CancellationToken.None);
        Assert.Equal("InProgress", progress.Status);
        var resolved = await h.Service.ChangeStatusAsync(
            created.Id, new ChangeDefectStatusCommand("Resolved"), CancellationToken.None);
        Assert.Equal("Resolved", resolved.Status);
        var reopened = await h.Service.ChangeStatusAsync(
            created.Id, new ChangeDefectStatusCommand("Open"), CancellationToken.None);
        Assert.Equal("Open", reopened.Status);

        await Assert.ThrowsAsync<ValidationException>(() => h.Service.ChangeStatusAsync(
            created.Id, new ChangeDefectStatusCommand("Bogus"), CancellationToken.None));
        // Closed -> Resolved is not a valid transition.
        await h.Service.ChangeStatusAsync(created.Id, new ChangeDefectStatusCommand("Closed"), CancellationToken.None);
        await Assert.ThrowsAsync<ValidationException>(() => h.Service.ChangeStatusAsync(
            created.Id, new ChangeDefectStatusCommand("Resolved"), CancellationToken.None));

        Assert.Contains(h.Audit.Actions, a => a == "defect.status_changed");
        Assert.Contains(h.Audit.Actions, a => a == "defect.reopened");
    }

    [Fact]
    public async Task Update_EditsContent_AuditsSeverityChange()
    {
        var h = Create();
        var created = await h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "X", null, null), CancellationToken.None);
        var updated = await h.Service.UpdateAsync(
            created.Id, new UpdateDefectCommand("X fixed?", "More detail.", "Critical"), CancellationToken.None);
        Assert.Equal("Critical", updated.Severity);
        Assert.Contains(h.Audit.Actions, a => a == "defect.updated");
        Assert.Contains(h.Audit.Actions, a => a == "defect.severity_changed");
    }

    [Fact]
    public async Task Get_CrossProject_Returns403_And_List_IsProjectScoped()
    {
        var h = Create();
        var created = await h.Service.CreateAsync(
            new CreateDefectCommand(ProjectA, h.ExecutionId, "X", null, null), CancellationToken.None);
        var outsider = Create(Manager(), member: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => outsider.Service.GetAsync(created.Id, CancellationToken.None));

        var page = await h.Service.ListAsync(ProjectA, 1, 25,
            new DefectFilters(null, null, null, null, null), CancellationToken.None);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("LOGIN-001", page.Items.Single().TestKey);
    }
}
