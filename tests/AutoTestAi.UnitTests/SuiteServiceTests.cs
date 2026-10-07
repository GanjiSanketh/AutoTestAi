using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>In-memory ISuiteStore fake: no EF, no database.</summary>
internal sealed class FakeSuiteStore : ISuiteStore
{
    public readonly List<TestSuite> Suites = new();
    public readonly List<(Guid SuiteId, Guid TestCaseId, int Order)> Members = new();
    public readonly List<Execution> Executions = new();
    public readonly List<ExecutionTest> Tests = new();

    public Task<int> CountAsync(Guid projectId, string? search, string? status, CancellationToken ct)
        => Task.FromResult(Apply(projectId, search, status).Count);

    public Task<IReadOnlyList<SuiteListItemDto>> ListAsync(
        Guid projectId, string? search, string? status, int skip, int take, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SuiteListItemDto>>(Apply(projectId, search, status)
            .OrderByDescending(s => s.UpdatedAt).Skip(skip).Take(take)
            .Select(s => new SuiteListItemDto(
                s.Id, s.ProjectId, s.Name, s.Description, s.Status.ToString(),
                Members.Count(m => m.SuiteId == s.Id), s.UpdatedAt))
            .ToList());

    private List<TestSuite> Apply(Guid projectId, string? search, string? status)
    {
        IEnumerable<TestSuite> query = Suites.Where(s => s.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s =>
                s.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (s.Description != null && s.Description.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<ProjectStatus>(status.Trim(), true, out var parsed))
            query = query.Where(s => s.Status == parsed);
        return query.ToList();
    }

    public Task<TestSuite?> GetByIdRawAsync(Guid suiteId, CancellationToken ct)
        => Task.FromResult(Suites.FirstOrDefault(s => s.Id == suiteId));

    public Task<bool> ExistsWithNameAsync(Guid projectId, string name, Guid? excludeSuiteId, CancellationToken ct)
        => Task.FromResult(Suites.Any(s =>
            s.ProjectId == projectId &&
            string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (!excludeSuiteId.HasValue || s.Id != excludeSuiteId.Value)));

    public Task AddAsync(TestSuite suite, CancellationToken ct)
    {
        Suites.Add(suite);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<SuiteDetailDto?> GetByIdWithMembersAsync(Guid suiteId, CancellationToken ct)
    {
        var suite = Suites.FirstOrDefault(s => s.Id == suiteId);
        if (suite is null)
            return Task.FromResult<SuiteDetailDto?>(null);
        return GetMembersAsync(suiteId, ct).ContinueWith<SuiteDetailDto?>(t => new SuiteDetailDto(
            suite.Id, suite.ProjectId, suite.Name, suite.Description, suite.Status.ToString(),
            suite.CreatedAt, suite.UpdatedAt, t.Result));
    }

    public Task<IReadOnlyList<SuiteMemberDto>> GetMembersAsync(Guid suiteId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SuiteMemberDto>>(Members
            .Where(m => m.SuiteId == suiteId)
            .OrderBy(m => m.Order).ThenBy(m => m.TestCaseId)
            .Select(m => new SuiteMemberDto(suiteId, m.TestCaseId, string.Empty, string.Empty, m.Order, null, null))
            .ToList());

    public Task AddMemberAsync(Guid suiteId, Guid testCaseId, int executionOrder, CancellationToken ct)
    {
        Members.Add((suiteId, testCaseId, executionOrder));
        return Task.CompletedTask;
    }

    public Task<bool> RemoveMemberAsync(Guid suiteId, Guid testCaseId, CancellationToken ct)
    {
        var index = Members.FindIndex(m => m.SuiteId == suiteId && m.TestCaseId == testCaseId);
        if (index < 0)
            return Task.FromResult(false);
        Members.RemoveAt(index);
        return Task.FromResult(true);
    }

    public Task UpdateMemberOrdersAsync(Guid suiteId, IReadOnlyDictionary<Guid, int> orders, CancellationToken ct)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (Members[i].SuiteId == suiteId && orders.TryGetValue(Members[i].TestCaseId, out var order))
                Members[i] = (Members[i].SuiteId, Members[i].TestCaseId, order);
        }
        return Task.CompletedTask;
    }

    private IEnumerable<Execution> FilteredExecutions(Guid suiteId, ExecutionStatus? status, TriggerType? trigger)
    {
        IEnumerable<Execution> query = Executions.Where(e => e.SuiteId == suiteId);
        if (status.HasValue) query = query.Where(e => e.Status == status.Value);
        if (trigger.HasValue) query = query.Where(e => e.TriggerType == trigger.Value);
        return query;
    }

    public Task<int> GetExecutionCountAsync(Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, CancellationToken ct)
        => Task.FromResult(FilteredExecutions(suiteId, status, triggerType).Count());

    public Task<IReadOnlyList<SuiteExecutionSummaryDto>> GetExecutionHistoryAsync(
        Guid suiteId, ExecutionStatus? status, TriggerType? triggerType, int skip, int take, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SuiteExecutionSummaryDto>>(FilteredExecutions(suiteId, status, triggerType)
            .OrderByDescending(e => e.CreatedAt).Skip(skip).Take(take)
            .Select(e => new SuiteExecutionSummaryDto(
                e.Id, e.Status.ToString(), e.TriggerType.ToString(), e.CreatedAt, e.StartedAt, e.CompletedAt,
                Tests.Count(t => t.ExecutionId == e.Id),
                Tests.Count(t => t.ExecutionId == e.Id && t.Status == ExecutionTestStatus.Passed),
                Tests.Count(t => t.ExecutionId == e.Id && t.Status == ExecutionTestStatus.Failed)))
            .ToList());

    public Task<SuiteReportDto?> GetReportDataAsync(Guid suiteId, DateTimeOffset? from, DateTimeOffset? to, TriggerType? trigger, CancellationToken ct)
    {
        var suite = Suites.FirstOrDefault(s => s.Id == suiteId);
        if (suite is null)
            return Task.FromResult<SuiteReportDto?>(null);
        var query = Executions
            .Where(e => e.SuiteId == suiteId && e.Status != ExecutionStatus.Queued && e.Status != ExecutionStatus.Running);
        if (trigger.HasValue) query = query.Where(e => e.TriggerType == trigger.Value);
        if (from.HasValue) query = query.Where(e => e.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.CreatedAt <= to.Value);
        var ids = query.Select(e => e.Id).ToList();
        if (ids.Count == 0)
            return Task.FromResult<SuiteReportDto?>(null);
        var tests = Tests.Where(t => ids.Contains(t.ExecutionId)).ToList();
        var passed = tests.Count(t => t.Status == ExecutionTestStatus.Passed);
        var failed = tests.Count(t => t.Status == ExecutionTestStatus.Failed);
        var cancelled = tests.Count(t => t.Status == ExecutionTestStatus.Cancelled);
        var timedOut = tests.Count(t => t.Status == ExecutionTestStatus.TimedOut);
        var error = tests.Count(t => t.Status == ExecutionTestStatus.Error);
        var total = passed + failed + cancelled + timedOut + error;
        return Task.FromResult<SuiteReportDto?>(new SuiteReportDto(
            suiteId, suite.Name, ids.Count, passed, failed, cancelled, timedOut, error,
            total > 0 ? (double)passed / total * 100 : null, 0, 0,
            Executions.Where(e => e.SuiteId == suiteId).Max(e => (DateTimeOffset?)e.CreatedAt),
            Array.Empty<TriggerBreakdownItem>(),
            Array.Empty<SuiteReportTrendPoint>()));
    }

    public Task<IReadOnlyList<TriggerBreakdownItem>> GetTriggerBreakdownAsync(Guid suiteId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var query = Executions
            .Where(e => e.SuiteId == suiteId && e.Status != ExecutionStatus.Queued && e.Status != ExecutionStatus.Running);
        if (from.HasValue) query = query.Where(e => e.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.CreatedAt <= to.Value);
        return Task.FromResult<IReadOnlyList<TriggerBreakdownItem>>(query
            .GroupBy(e => e.TriggerType)
            .OrderBy(g => g.Key.ToString())
            .Select(g =>
            {
                var tests = Tests.Where(t => g.Select(e => e.Id).Contains(t.ExecutionId)).ToList();
                var passed = tests.Count(t => t.Status == ExecutionTestStatus.Passed);
                var failed = tests.Count(t => t.Status == ExecutionTestStatus.Failed);
                var total = passed + failed;
                return new TriggerBreakdownItem(
                    g.Key.ToString(), g.Count(), passed, failed,
                    total > 0 ? (double)passed / total * 100 : null);
            })
            .ToList());
    }

    public Task<SuiteTrendData> GetTrendDataAsync(Guid suiteId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var executions = Executions
            .Where(e => e.SuiteId == suiteId && e.Status != ExecutionStatus.Queued && e.Status != ExecutionStatus.Running)
            .Where(e => e.CreatedAt >= from && e.CreatedAt <= to)
            .Select(e => new SuiteTrendExecution(e.Id, e.TriggerType.ToString(), e.CreatedAt))
            .ToList();
        var ids = executions.Select(e => e.ExecutionId).ToList();
        var tests = Tests
            .Where(t => ids.Contains(t.ExecutionId))
            .GroupBy(t => t.ExecutionId)
            .ToDictionary(
                g => g.Key,
                g => new SuiteTrendTests(
                    g.Count(t => t.Status == ExecutionTestStatus.Passed),
                    g.Count(t => t.Status == ExecutionTestStatus.Failed),
                    g.Count(t => t.Status == ExecutionTestStatus.Cancelled),
                    g.Count(t => t.Status == ExecutionTestStatus.TimedOut),
                    g.Count(t => t.Status == ExecutionTestStatus.Error),
                    g.Where(t => t.DurationMs.HasValue).Sum(t => (long)t.DurationMs!.Value)));
        return Task.FromResult(new SuiteTrendData(executions, tests));
    }

    public Task<Execution?> GetExecutionByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken ct)
        => Task.FromResult(Executions.FirstOrDefault(e => e.ProjectId == projectId && e.IdempotencyKey == idempotencyKey));
}

/// <summary>Recording ITestExecutionService fake: captures StartAsSystemAsync fan-out.</summary>
internal sealed class RecordingExecutionService : ITestExecutionService
{
    public readonly List<StartExecutionCommand> StartedAsSystem = new();
    public readonly List<StartExecutionCommand> StartedAsUser = new();

    public Task<StartExecutionResultDto> StartAsSystemAsync(StartExecutionCommand command, CancellationToken ct)
    {
        StartedAsSystem.Add(command);
        var executionId = Guid.NewGuid();
        return Task.FromResult(new StartExecutionResultDto(
            executionId, Guid.NewGuid(), command.ProjectId, Guid.NewGuid(),
            command.TestCaseVersionId, "Queued", null, DateTimeOffset.UtcNow, false));
    }

    public Task<StartExecutionResultDto> StartAsync(StartExecutionCommand command, CancellationToken ct)
    {
        StartedAsUser.Add(command);
        throw new InvalidOperationException("Suite fan-out must use StartAsSystemAsync.");
    }

    public Task<PagedResult<ExecutionListItemDto>> ListAsync(Guid p, int page, int size, ExecutionFilters f, CancellationToken ct) => throw new NotImplementedException();
    public Task<ExecutionDetailDto> GetAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<ExecutionStepDto>> ListStepsAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<ExecutionLogDto>> ListLogsAsync(Guid e, long? a, int t, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<ExecutionArtifactDto>> ListArtifactsAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
    public Task<ArtifactDownloadDto> GetArtifactDownloadUrlAsync(Guid e, Guid a, CancellationToken ct) => throw new NotImplementedException();
    public Task<CancelExecutionResultDto> CancelAsync(Guid e, CancellationToken ct) => throw new NotImplementedException();
}

public sealed class SuiteServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    /// <summary>No-op schedule service for 9A suite tests (records archive cascades).</summary>
    internal sealed class StubScheduleService : ISuiteScheduleService
    {
        public readonly List<Guid> DisabledSuites = new();
        public Task<SuiteScheduleDto> CreateAsync(Guid p, Guid s, CreateSuiteScheduleCommand c, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SuiteScheduleDto>> ListBySuiteAsync(Guid p, Guid s, CancellationToken ct) => throw new NotImplementedException();
        public Task<SuiteScheduleDto?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<SuiteScheduleDto?> UpdateAsync(Guid id, UpdateSuiteScheduleCommand c, CancellationToken ct) => throw new NotImplementedException();
        public Task PauseAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task ResumeAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task ArchiveAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecuteSuiteResult> RunNowAsync(Guid id, RunScheduleNowCommand c, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> DisableForSuiteAsync(Guid suiteId, CancellationToken ct)
        {
            DisabledSuites.Add(suiteId);
            return Task.FromResult(0);
        }
        public Task<ExecuteSuiteResult> FireAsync(Guid id, string key, CancellationToken ct) => throw new NotImplementedException();
    }

    private static (SuiteService Service, FakeSuiteStore Suites, FakeTestCaseStore Cases, StubAuditProjectStore Audits, StubScheduleService Schedules) Create(
        StubCurrentUser user,
        Action<FakeSuiteStore, FakeTestCaseStore, StubMembershipStore>? seed = null,
        string userSub = "user-1",
        Guid? userAppId = null)
    {
        var suites = new FakeSuiteStore();
        var cases = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        seed?.Invoke(suites, cases, memberships);
        var directory = new FakeUserDirectory();
        if (userAppId.HasValue) directory.Add(userSub, userAppId.Value);
        var authorization = new AuthorizationService(user, memberships);
        var audits = new StubAuditProjectStore();
        var schedules = new StubScheduleService();
        var service = new SuiteService(
            suites, cases, schedules, user, authorization, directory,
            new SystemDateTimeProvider(),
            new AuditService(audits, user, directory, NullLogger<AuditService>.Instance));
        return (service, suites, cases, audits, schedules);
    }

    private static StubCurrentUser Manager(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["qa-lead"],
        Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static StubCurrentUser Viewer(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["viewer"],
        Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private static StubCurrentUser Admin(string sub = "admin-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["admin"],
        Permissions = RolePermissions.Resolve(["admin"]),
    };

    private static TestCase SeedCase(FakeTestCaseStore cases, Guid projectId, string key,
        TestCaseStatus status = TestCaseStatus.Active, ReviewStatus review = ReviewStatus.Approved, int versions = 1)
    {
        var testCase = new TestCase
        {
            ProjectId = projectId, TestKey = key, Title = $"Title {key}",
            Priority = Priority.Medium, Status = status, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        for (var i = 1; i <= versions; i++)
            cases.Versions.Add(new TestCaseVersion
            {
                TestCaseId = testCase.Id, VersionNumber = i,
                SourceCode = $"// {key}-v{i}",
                ReviewStatus = i == versions ? review : ReviewStatus.Approved,
            });
        return testCase;
    }

    private static TestSuite SeedSuite(FakeSuiteStore suites, Guid projectId, string name,
        ProjectStatus status = ProjectStatus.Active)
    {
        var suite = new TestSuite
        {
            ProjectId = projectId, Name = name, Status = status,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        suites.Suites.Add(suite);
        return suite;
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_Succeeds_AndAudits_WithCreator()
    {
        var appId = Guid.NewGuid();
        var (service, suites, _, audits, _) = Create(Manager(),
            (s, c, m) => m.Add("user-1", ProjectA), userAppId: appId);

        var created = await service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "Regression", "Nightly set", "Active", null),
            CancellationToken.None);

        Assert.Equal("Regression", created.Name);
        Assert.Single(suites.Suites);
        Assert.Equal(appId, suites.Suites[0].CreatedBy);
        Assert.Contains(audits.Audits, a => a.Action == "suite.created" && a.ProjectId == ProjectA);
    }

    [Fact]
    public async Task Create_DuplicateNameSameProject_Conflict_CaseInsensitive()
    {
        var (service, _, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            SeedSuite(s, ProjectA, "Regression");
        });

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "  regression ", null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_SameNameDifferentProject_Allowed()
    {
        var (service, suites, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            m.Add("user-1", ProjectB);
            SeedSuite(s, ProjectA, "Regression");
        });

        await service.CreateAsync(
            new CreateSuiteCommand(ProjectB, "Regression", null, null, null), CancellationToken.None);

        Assert.Equal(2, suites.Suites.Count);
    }

    [Fact]
    public async Task Create_Validation_NameRequired_And_BadStatus()
    {
        var (service, _, _, _, _) = Create(Manager(), (s, c, m) => m.Add("user-1", ProjectA));

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "  ", null, null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "Ok", null, "Bogus", null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_MemberCrossProject_Forbidden_MemberMissing_NotFound()
    {
        TestCase? foreign = null;
        var (service, _, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            m.Add("user-1", ProjectB);
            foreign = SeedCase(c, ProjectB, "B-001");
        });

        // Cross-project member -> 403
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "S", null, null,
                [new CreateSuiteMemberCommand(foreign!.Id, 1)]),
            CancellationToken.None));

        // Missing member -> 404
        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "S", null, null,
                [new CreateSuiteMemberCommand(Guid.NewGuid(), 1)]),
            CancellationToken.None));
    }

    [Fact]
    public async Task Create_Unauthorized_ThrowsForbidden()
    {
        // Viewer (no testcases.manage) cannot create.
        var (service, _, _, _, _) = Create(Viewer(), (s, c, m) => m.Add("user-1", ProjectA));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "S", null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_DuplicateMember_Validation()
    {
        TestCase? member = null;
        var (service, _, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            member = SeedCase(c, ProjectA, "A-001");
        });

        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new CreateSuiteCommand(ProjectA, "S", null, null,
                [new CreateSuiteMemberCommand(member!.Id, 1), new CreateSuiteMemberCommand(member.Id, 2)]),
            CancellationToken.None));
    }

    // ---------- read / project isolation ----------

    [Fact]
    public async Task GetById_NonMember_Forbidden_NoLeak()
    {
        TestSuite? suite = null;
        var (service, _, _, _, _) = Create(Viewer(), (s, c, m) =>
        {
            // Viewer is a member of ProjectB only; suite lives in ProjectA.
            m.Add("user-1", ProjectB);
            suite = SeedSuite(s, ProjectA, "Hidden");
        });

        await Assert.ThrowsAsync<ForbiddenException>(() => service.GetByIdAsync(suite!.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetById_UnknownSuite_Admin_NotFound_NonAdmin_Forbidden()
    {
        // Slice-1 opaque-scope convention: unknown ids authorize against the
        // id itself, so non-admins get 403 (no existence leak); admins get 404.
        var (memberService, _, _, _, _) = Create(Manager(), (s, c, m) => m.Add("user-1", ProjectA));
        await Assert.ThrowsAsync<ForbiddenException>(() => memberService.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));

        var (adminService, _, _, _, _) = Create(Admin(), null);
        await Assert.ThrowsAsync<NotFoundException>(() => adminService.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task List_NonMember_Forbidden()
    {
        var (service, _, _, _, _) = Create(Viewer(), (_, _, _) => { });

        await Assert.ThrowsAsync<ForbiddenException>(() => service.ListAsync(
            ProjectA, new SuiteListFilters(null, null), 1, 25, CancellationToken.None));
    }

    // ---------- update / archive ----------

    [Fact]
    public async Task Update_DuplicateName_Conflict_AndAudits()
    {
        TestSuite? target = null;
        var (service, _, _, audits, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            SeedSuite(s, ProjectA, "Taken");
            target = SeedSuite(s, ProjectA, "Mine");
        });

        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateAsync(
            target!.Id, new UpdateSuiteCommand("taken", null, null), CancellationToken.None));

        var updated = await service.UpdateAsync(
            target.Id, new UpdateSuiteCommand("Mine 2", "Desc", "Active"), CancellationToken.None);
        Assert.Equal("Mine 2", updated!.Name);
        Assert.Contains(audits.Audits, a => a.Action == "suite.updated");
    }

    [Fact]
    public async Task Archive_SetsArchived_AndAudits()
    {
        TestSuite? suite = null;
        var (service, suites, _, audits, schedules) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "Mine");
        });

        await service.ArchiveAsync(suite!.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Archived, suites.Suites.Single().Status);
        Assert.Contains(audits.Audits, a => a.Action == "suite.archived");
        Assert.Contains(suite.Id, schedules.DisabledSuites);
    }

    // ---------- membership ----------

    [Fact]
    public async Task AddTestCase_Duplicate_Conflict_CrossProject_Forbidden()
    {
        TestSuite? suite = null;
        TestCase? mine = null;
        TestCase? foreign = null;
        var (service, _, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            m.Add("user-1", ProjectB);
            suite = SeedSuite(s, ProjectA, "S");
            mine = SeedCase(c, ProjectA, "A-001");
            foreign = SeedCase(c, ProjectB, "B-001");
        });

        await service.AddTestCaseAsync(suite!.Id, new CreateSuiteMemberCommand(mine!.Id, 1), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.AddTestCaseAsync(
            suite.Id, new CreateSuiteMemberCommand(mine.Id, 2), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.AddTestCaseAsync(
            suite.Id, new CreateSuiteMemberCommand(foreign!.Id, 1), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.AddTestCaseAsync(
            suite.Id, new CreateSuiteMemberCommand(Guid.NewGuid(), 1), CancellationToken.None));
    }

    [Fact]
    public async Task AddTestCase_ArchivedSuite_Conflict()
    {
        TestSuite? suite = null;
        TestCase? mine = null;
        var (service, _, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "S", ProjectStatus.Archived);
            mine = SeedCase(c, ProjectA, "A-001");
        });

        await Assert.ThrowsAsync<ConflictException>(() => service.AddTestCaseAsync(
            suite!.Id, new CreateSuiteMemberCommand(mine!.Id, 1), CancellationToken.None));
    }

    [Fact]
    public async Task RemoveTestCase_NonMember_NotFound_Removal_Audits()
    {
        TestSuite? suite = null;
        TestCase? mine = null;
        var (service, _, _, audits, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "S");
            mine = SeedCase(c, ProjectA, "A-001");
        });

        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveTestCaseAsync(
            suite!.Id, mine!.Id, CancellationToken.None));

        await service.AddTestCaseAsync(suite.Id, new CreateSuiteMemberCommand(mine.Id, 1), CancellationToken.None);
        await service.RemoveTestCaseAsync(suite.Id, mine.Id, CancellationToken.None);

        Assert.Contains(audits.Audits, a => a.Action == "suite.test_added");
        Assert.Contains(audits.Audits, a => a.Action == "suite.test_removed");
    }

    [Fact]
    public async Task Reorder_SubsetOrUnknown_Validation_ExactSet_Succeeds()
    {
        TestSuite? suite = null;
        TestCase? a = null;
        TestCase? b = null;
        var (service, suites, _, audits, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "S");
            a = SeedCase(c, ProjectA, "A-001");
            b = SeedCase(c, ProjectA, "A-002");
            s.Members.Add((suite.Id, a.Id, 1));
            s.Members.Add((suite.Id, b.Id, 2));
        });

        // Subset (drops a member) -> 400
        await Assert.ThrowsAsync<ValidationException>(() => service.ReorderAsync(suite!.Id,
            new ReorderSuiteMembersCommand([new SuiteMemberReorderItem(a!.Id, 1)]), CancellationToken.None));

        // Unknown id -> 400
        await Assert.ThrowsAsync<ValidationException>(() => service.ReorderAsync(suite.Id,
            new ReorderSuiteMembersCommand(
                [new SuiteMemberReorderItem(a.Id, 1), new SuiteMemberReorderItem(Guid.NewGuid(), 2)]),
            CancellationToken.None));

        // Duplicate orders -> 400
        await Assert.ThrowsAsync<ValidationException>(() => service.ReorderAsync(suite.Id,
            new ReorderSuiteMembersCommand(
                [new SuiteMemberReorderItem(a.Id, 1), new SuiteMemberReorderItem(b!.Id, 1)]),
            CancellationToken.None));

        // Exact set, swapped -> applies deterministically
        await service.ReorderAsync(suite.Id,
            new ReorderSuiteMembersCommand(
                [new SuiteMemberReorderItem(a.Id, 2), new SuiteMemberReorderItem(b.Id, 1)]),
            CancellationToken.None);

        var detail = await service.GetByIdAsync(suite.Id, CancellationToken.None);
        Assert.Equal(b.Id, detail!.Members[0].TestCaseId);
        Assert.Equal(a.Id, detail.Members[1].TestCaseId);
        Assert.Contains(audits.Audits, a2 => a2.Action == "suite.test_reordered");
        _ = suites;
    }

    // ---------- history / report ----------

    [Fact]
    public async Task History_AggregatesCounts_And_FiltersStatus()
    {
        TestSuite? suite = null;
        var (service, suites, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "S");
            var passed = new Execution
            {
                ProjectId = ProjectA, SuiteId = suite.Id, Status = ExecutionStatus.Passed,
                TriggerType = TriggerType.Manual, CreatedAt = DateTimeOffset.UtcNow,
            };
            var failed = new Execution
            {
                ProjectId = ProjectA, SuiteId = suite.Id, Status = ExecutionStatus.Failed,
                TriggerType = TriggerType.Manual, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            s.Executions.AddRange([passed, failed]);
            s.Tests.AddRange(
            [
                new ExecutionTest { ExecutionId = passed.Id, Status = ExecutionTestStatus.Passed },
                new ExecutionTest { ExecutionId = passed.Id, Status = ExecutionTestStatus.Passed },
                new ExecutionTest { ExecutionId = failed.Id, Status = ExecutionTestStatus.Failed },
            ]);
        });
        _ = suites;

        var all = await service.GetExecutionHistoryAsync(suite!.Id,
            new SuiteExecutionHistoryFilters(null, null), 1, 25, CancellationToken.None);
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(2, all.Items.Single(i => i.Status == "Passed").PassedCount);

        var onlyFailed = await service.GetExecutionHistoryAsync(suite.Id,
            new SuiteExecutionHistoryFilters("Failed", null), 1, 25, CancellationToken.None);
        Assert.Equal(1, onlyFailed.TotalCount);

        await Assert.ThrowsAsync<ValidationException>(() => service.GetExecutionHistoryAsync(suite.Id,
            new SuiteExecutionHistoryFilters("Bogus", null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task Report_Aggregates_And_RespectsDateRange()
    {
        TestSuite? suite = null;
        var (service, suites, _, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = SeedSuite(s, ProjectA, "S");
            var old = new Execution
            {
                ProjectId = ProjectA, SuiteId = suite.Id, Status = ExecutionStatus.Passed,
                TriggerType = TriggerType.Manual, CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };
            var recent = new Execution
            {
                ProjectId = ProjectA, SuiteId = suite.Id, Status = ExecutionStatus.Failed,
                TriggerType = TriggerType.Manual, CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            };
            s.Executions.AddRange([old, recent]);
            s.Tests.AddRange(
            [
                new ExecutionTest { ExecutionId = old.Id, Status = ExecutionTestStatus.Passed },
                new ExecutionTest { ExecutionId = recent.Id, Status = ExecutionTestStatus.Failed },
            ]);
        });
        _ = suites;

        var full = await service.GetReportAsync(suite!.Id,
            new SuiteReportFilters(null, null), CancellationToken.None);
        Assert.Equal(2, full!.TotalExecutions);
        Assert.Equal(50, full.PassRate);

        var recentOnly = await service.GetReportAsync(suite.Id,
            new SuiteReportFilters(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), null),
            CancellationToken.None);
        Assert.Equal(1, recentOnly!.TotalExecutions);
        Assert.Equal(0, recentOnly.PassRate);

        await Assert.ThrowsAsync<ValidationException>(() => service.GetReportAsync(suite.Id,
            new SuiteReportFilters(
                new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None));
    }

    [Fact]
    public async Task History_UnknownSuite_Admin_NotFound_Report_UnknownSuite_Admin_NotFound()
    {
        var (service, _, _, _, _) = Create(Admin(), null);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetExecutionHistoryAsync(
            Guid.NewGuid(), new SuiteExecutionHistoryFilters(null, null), 1, 25, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetReportAsync(
            Guid.NewGuid(), new SuiteReportFilters(null, null), CancellationToken.None));
    }
}

public sealed class SuiteExecutionServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private static (SuiteExecutionService Service, FakeSuiteStore Suites, FakeTestCaseStore Cases, RecordingExecutionService Executions, StubAuditProjectStore Audits) Create(
        StubCurrentUser user,
        Action<FakeSuiteStore, FakeTestCaseStore, StubMembershipStore>? seed = null,
        string userSub = "user-1")
    {
        var suites = new FakeSuiteStore();
        var cases = new FakeTestCaseStore();
        var memberships = new StubMembershipStore();
        seed?.Invoke(suites, cases, memberships);
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        var audits = new StubAuditProjectStore();
        var executions = new RecordingExecutionService();
        var service = new SuiteExecutionService(
            suites, cases, executions, authorization,
            new SystemDateTimeProvider(),
            new AuditService(audits, user, directory, NullLogger<AuditService>.Instance));
        return (service, suites, cases, executions, audits);
    }

    private static StubCurrentUser Manager(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["qa-lead"],
        Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static StubCurrentUser Admin(string sub = "admin-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["admin"],
        Permissions = RolePermissions.Resolve(["admin"]),
    };

    private static TestCase SeedApproved(FakeTestCaseStore cases, Guid projectId, string key)
    {
        var testCase = new TestCase
        {
            ProjectId = projectId, TestKey = key, Title = $"Title {key}",
            Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        cases.Cases.Add(testCase);
        cases.Versions.Add(new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 1, SourceCode = "// v1", ReviewStatus = ReviewStatus.Approved,
        });
        return testCase;
    }

    private static TestSuite SeedSuiteWith(FakeSuiteStore suites, Guid projectId, string name, params TestCase[] members)
    {
        var suite = new TestSuite
        {
            ProjectId = projectId, Name = name, Status = ProjectStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        suites.Suites.Add(suite);
        for (var i = 0; i < members.Length; i++)
            suites.Members.Add((suite.Id, members[i].Id, i + 1));
        return suite;
    }

    [Fact]
    public async Task Execute_EmptySuite_Conflict_StartsNothing()
    {
        TestSuite? suite = null;
        var (service, _, _, executions, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            suite = new TestSuite
            {
                ProjectId = ProjectA, Name = "Empty", Status = ProjectStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            s.Suites.Add(suite);
        });

        await Assert.ThrowsAsync<ConflictException>(() => service.ExecuteAsync(
            ProjectA, suite!.Id, new ExecuteSuiteCommand(ProjectA, suite.Id, null), CancellationToken.None));
        Assert.Empty(executions.StartedAsSystem);
    }

    [Fact]
    public async Task Execute_NoApprovedVersion_Validation_StartsNothing()
    {
        TestSuite? suite = null;
        var (service, _, _, executions, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var pending = new TestCase
            {
                ProjectId = ProjectA, TestKey = "P-001", Title = "Pending",
                Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
            };
            c.Cases.Add(pending);
            c.Versions.Add(new TestCaseVersion
            {
                TestCaseId = pending.Id, VersionNumber = 1, SourceCode = "// v1", ReviewStatus = ReviewStatus.Pending,
            });
            suite = SeedSuiteWith(s, ProjectA, "S", pending);
        });

        await Assert.ThrowsAsync<ValidationException>(() => service.ExecuteAsync(
            ProjectA, suite!.Id, new ExecuteSuiteCommand(ProjectA, suite.Id, null), CancellationToken.None));
        Assert.Empty(executions.StartedAsSystem);
    }

    [Fact]
    public async Task Execute_ArchivedMember_Conflict_StartsNothing()
    {
        TestSuite? suite = null;
        var (service, _, cases, executions, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var ok = SeedApproved(c, ProjectA, "A-001");
            var archived = SeedApproved(c, ProjectA, "A-002");
            archived.Status = TestCaseStatus.Archived;
            suite = SeedSuiteWith(s, ProjectA, "S", ok, archived);
        });
        _ = cases;

        await Assert.ThrowsAsync<ConflictException>(() => service.ExecuteAsync(
            ProjectA, suite!.Id, new ExecuteSuiteCommand(ProjectA, suite.Id, null), CancellationToken.None));
        Assert.Empty(executions.StartedAsSystem);
    }

    [Fact]
    public async Task Execute_BindsExactLatestApprovedVersion_InDeterministicOrder()
    {
        TestSuite? suite = null;
        TestCase? first = null;
        TestCase? second = null;
        var (service, _, cases, executions, audits) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            first = SeedApproved(c, ProjectA, "A-001");
            second = SeedApproved(c, ProjectA, "A-002");
            // A newer Approved version must be the one bound (never v1).
            c.Versions.Add(new TestCaseVersion
            {
                TestCaseId = second.Id, VersionNumber = 2, SourceCode = "// v2-approved",
                ReviewStatus = ReviewStatus.Approved,
            });
            suite = SeedSuiteWith(s, ProjectA, "S", first, second);
        });

        var result = await service.ExecuteAsync(
            ProjectA, suite!.Id, new ExecuteSuiteCommand(ProjectA, suite.Id, null), CancellationToken.None);

        Assert.Equal(2, result.TestCount);
        Assert.Equal(2, executions.StartedAsSystem.Count);
        Assert.Empty(executions.StartedAsUser);
        // Deterministic member order.
        Assert.Equal(first!.Id, cases.Versions
            .Where(v => v.Id == executions.StartedAsSystem[0].TestCaseVersionId)
            .Select(v => v.TestCaseId).Single());
        // Second member binds version 2 (latest Approved), never v1.
        var boundSecond = executions.StartedAsSystem[1].TestCaseVersionId;
        Assert.Equal(2, cases.Versions.Single(v => v.Id == boundSecond).VersionNumber);
        // Delegation carries suite id + manual trigger through the existing seam.
        Assert.All(executions.StartedAsSystem, cmd =>
        {
            Assert.Equal(ProjectA, cmd.ProjectId);
            Assert.Equal(suite.Id, cmd.SuiteId);
            Assert.Equal(TriggerType.Manual, cmd.Trigger);
        });
        Assert.Contains(audits.Audits, a => a.Action == "suite.executed" && a.ProjectId == ProjectA);
    }

    [Fact]
    public async Task Execute_NewerPendingVersion_RejectsSuite()
    {
        // Strict binding: the executable version is the latest version, and
        // it must be Approved. A member whose latest version is still Pending
        // rejects the whole suite — never silently runs a stale version.
        TestSuite? suite = null;
        var (service, _, cases, executions, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var member = SeedApproved(c, ProjectA, "A-001");
            c.Versions.Add(new TestCaseVersion
            {
                TestCaseId = member.Id, VersionNumber = 2, SourceCode = "// v2-pending",
                ReviewStatus = ReviewStatus.Pending,
            });
            suite = SeedSuiteWith(s, ProjectA, "S", member);
        });
        _ = cases;

        await Assert.ThrowsAsync<ValidationException>(() => service.ExecuteAsync(
            ProjectA, suite!.Id, new ExecuteSuiteCommand(ProjectA, suite.Id, null), CancellationToken.None));
        Assert.Empty(executions.StartedAsSystem);
    }

    [Fact]
    public async Task Execute_CrossProjectSuite_Forbidden_ArchivedSuite_Conflict()
    {
        TestSuite? suiteA = null;
        TestSuite? archived = null;
        var (service, _, cases, executions, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            m.Add("user-1", ProjectB);
            var ok = SeedApproved(c, ProjectA, "A-001");
            suiteA = SeedSuiteWith(s, ProjectA, "S", ok);
            archived = new TestSuite
            {
                ProjectId = ProjectA, Name = "Old", Status = ProjectStatus.Archived,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            s.Suites.Add(archived);
            s.Members.Add((archived.Id, ok.Id, 1));
        });
        _ = cases;

        // Suite from another project -> 403.
        await Assert.ThrowsAsync<ForbiddenException>(() => service.ExecuteAsync(
            ProjectB, suiteA!.Id, new ExecuteSuiteCommand(ProjectB, suiteA.Id, null), CancellationToken.None));
        // Archived suite -> 409.
        await Assert.ThrowsAsync<ConflictException>(() => service.ExecuteAsync(
            ProjectA, archived!.Id, new ExecuteSuiteCommand(ProjectA, archived.Id, null), CancellationToken.None));
        Assert.Empty(executions.StartedAsSystem);
    }

    [Fact]
    public async Task Execute_UnknownSuite_Admin_NotFound()
    {
        var (service, _, _, executions, _) = Create(Admin(), null);

        var unknown = Guid.NewGuid();
        await Assert.ThrowsAsync<NotFoundException>(() => service.ExecuteAsync(
            ProjectA, unknown, new ExecuteSuiteCommand(ProjectA, unknown, null),
            CancellationToken.None));
        Assert.Empty(executions.StartedAsSystem);
    }

    [Fact]
    public async Task Execute_SuiteIdMismatch_And_LongKey_Validation()
    {
        TestSuite? suite = null;
        var (service, _, cases, _, _) = Create(Manager(), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var ok = SeedApproved(c, ProjectA, "A-001");
            suite = SeedSuiteWith(s, ProjectA, "S", ok);
        });
        _ = cases;

        await Assert.ThrowsAsync<ValidationException>(() => service.ExecuteAsync(
            ProjectA, suite!.Id, new ExecuteSuiteCommand(ProjectA, Guid.NewGuid(), null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.ExecuteAsync(
            ProjectA, suite.Id, new ExecuteSuiteCommand(ProjectA, suite.Id, new string('k', 61)),
            CancellationToken.None));
    }

    [Fact]
    public async Task Execute_Outsider_Forbidden()
    {
        TestSuite? suite = null;
        var (service, _, cases, _, _) = Create(Manager("outsider"), (s, c, m) =>
        {
            m.Add("user-1", ProjectA);
            var ok = SeedApproved(c, ProjectA, "A-001");
            suite = SeedSuiteWith(s, ProjectA, "S", ok);
        });
        _ = cases;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.ExecuteAsync(
            ProjectA, suite!.Id, new ExecuteSuiteCommand(ProjectA, suite.Id, null), CancellationToken.None));
    }
}
