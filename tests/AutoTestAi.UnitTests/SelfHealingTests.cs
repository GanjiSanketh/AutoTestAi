using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.SelfHealing;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Phase 2 Slice 11: self-healing policy, eligibility, AI validation,
/// suggestion fencing/isolation, attempt persistence fencing/concurrency,
/// evidence redaction, and immutable test versions. No live providers.
/// </summary>
public sealed class SelfHealingTests
{
    private static readonly Guid ProjectA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProjectB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ExecutionA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid VersionA = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    // ---------- fakes ----------

    private sealed class FakePolicyStore : ISelfHealingPolicyStore
    {
        public readonly Dictionary<Guid, SelfHealingPolicy> Rows = new();
        public Task<SelfHealingPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(projectId, out var row) ? row : null);
        public Task AddAsync(SelfHealingPolicy policy, CancellationToken ct)
        {
            Rows[policy.ProjectId] = policy;
            return Task.CompletedTask;
        }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAttemptStore : ISelfHealingAttemptStore
    {
        public readonly List<SelfHealingAttempt> Rows = new();
        public Task<IReadOnlyList<SelfHealingAttempt>> ListByTestAsync(Guid testId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SelfHealingAttempt>>(
                Rows.Where(r => r.ExecutionTestId == testId).OrderBy(r => r.StepOrder).ToList());
        public Task<IReadOnlyList<SelfHealingAttempt>> ListByExecutionAsync(Guid executionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SelfHealingAttempt>>(
                Rows.Where(r => r.ExecutionId == executionId).OrderBy(r => r.StepOrder).ToList());
        public Task<SelfHealingAttempt?> FindByTestAndStepAsync(Guid testId, int order, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.ExecutionTestId == testId && r.StepOrder == order));
        public Task<HealingAttemptCounts> CountByProjectAsync(Guid projectId, CancellationToken ct)
        {
            var rows = Rows.Where(r => r.ProjectId == projectId).ToList();
            return Task.FromResult(new HealingAttemptCounts(
                rows.Count, rows.Count(r => r.WasApplied),
                rows.Where(r => r.WasApplied).OrderByDescending(r => r.CreatedAt)
                    .Select(r => (DateTimeOffset?)r.CreatedAt).FirstOrDefault()));
        }
        public Task AddAsync(SelfHealingAttempt attempt, CancellationToken ct)
        {
            Rows.Add(attempt);
            return Task.CompletedTask;
        }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeExecutionStore : IExecutionStore
    {
        public Execution? Execution { get; set; }
        public ExecutionTest? Test { get; set; }
        public Task<int> CountAsync(Guid p, string? s, Guid? t, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionListRow>> ListAsync(Guid p, string? s, Guid? t, int sk, int ta, CancellationToken ct) => throw new NotImplementedException();
        public Task<Execution?> GetExecutionByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Execution?.Id == id ? Execution : null);
        public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExecutionTest>>(
                Test is not null && Execution?.Id == id ? new[] { Test } : Array.Empty<ExecutionTest>());
        public Task<Execution?> FindByIdempotencyKeyAsync(Guid p, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionAsync(Execution e, CancellationToken ct) => throw new NotImplementedException();
        public Task AddExecutionTestAsync(ExecutionTest t, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteStepResultsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(Guid id, long? a, int t, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteLogsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddArtifactAsync(ExecutionArtifact a, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteArtifactsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Domain.Entities.FailureAnalysis>> ListAnalysesAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Domain.Entities.FailureAnalysis?> GetAnalysisByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddAnalysisAsync(Domain.Entities.FailureAnalysis a, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeAssignments : IGridAssignmentStore
    {
        public GridAssignment? Active { get; set; }
        public Task<GridAssignment?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<GridAssignment?> FindActiveByTestAsync(Guid testId, CancellationToken ct)
            => Task.FromResult(Active?.ExecutionTestId == testId ? Active : null);
        public Task<GridAssignment?> FindActiveByRefAsync(string workerRef, CancellationToken ct)
            => Task.FromResult(Active?.WorkerAssignmentRef == workerRef ? Active : null);
        public Task<IReadOnlyList<GridAssignment>> ListActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<GridAssignment>> ListExpiredActiveAsync(DateTimeOffset n, int t, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountActiveByProjectAsync(Guid p, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountQueuedExecutionsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task AddAsync(GridAssignment a, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeProvider : IAiProvider
    {
        public string Name => "stub";
        public AiHealingResult? HealingResult { get; set; }
        public Exception? HealingFailure { get; set; }
        public AiHealingRequest? SeenHealingRequest { get; private set; }
        public int HealingCalls { get; private set; }
        public Task<AiGenerationResult> GenerateTestAsync(AiGenerationRequest r, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AiAnalysisResult> AnalyzeFailureAsync(AiFailureAnalysisRequest r, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AiHealingResult> SuggestHealingCandidatesAsync(AiHealingRequest request, CancellationToken ct)
        {
            HealingCalls++;
            SeenHealingRequest = request;
            ct.ThrowIfCancellationRequested();
            if (HealingFailure is not null) throw HealingFailure;
            return Task.FromResult(HealingResult!);
        }
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        public IAiProvider Provider { get; set; } = null!;
        public IAiProvider Resolve() => Provider;
        public AiProviderStatus GetStatus()
            => new(Provider?.Name ?? "stub", null, true, null, AiPromptVersions.SelfHealingV1);
    }

    private sealed class FakeAuth : IAuthorizationService
    {
        public bool Authenticated { get; set; } = true;
        public HashSet<string> Permissions { get; } = new();
        public HashSet<Guid> Projects { get; } = new();
        public bool HasPermission(string p) => Permissions.Contains(p);
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult(Authenticated && Projects.Contains(projectId));
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct)
        {
            if (!Authenticated) throw new UnauthorizedAccessException("Authentication is required.");
            if (permission is not null && !Permissions.Contains(permission))
                throw new ForbiddenException($"Missing required permission '{permission}'.");
            if (!Projects.Contains(projectId)) throw new ForbiddenException("No access.");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUsers : IUserDirectory
    {
        public Task<Guid?> FindAppUserIdAsync(string externalId, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<Guid> EnsureProvisionedAsync(string externalId, string? email, string? displayName, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public string? ExternalIdentityId => "tester";
        public bool IsAuthenticated => true;
        public string? Email => null;
        public string? DisplayName => "Tester";
        public IReadOnlyCollection<string> Roles => new[] { "tester" };
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public readonly List<string> Metadata = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        {
            Actions.Add(action);
            Metadata.Add(metadataJson ?? string.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        public readonly List<string> Events = new();
        public Task PublishAsync(Guid executionId, string eventName, object payload, CancellationToken ct)
        {
            Events.Add(eventName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private static (FakeExecutionStore Executions, ExecutionTest Test) ExecutionFixture()
    {
        var executions = new FakeExecutionStore
        {
            Execution = new Execution { Id = ExecutionA, ProjectId = ProjectA, Status = ExecutionStatus.Running },
        };
        var test = new ExecutionTest
        {
            ExecutionId = ExecutionA,
            TestCaseId = Guid.NewGuid(),
            TestCaseVersionId = VersionA,
            Status = ExecutionTestStatus.Running,
        };
        executions.Test = test;
        return (executions, test);
    }

    private static SelfHealingService HealingService(
        FakePolicyStore policies, FakeAttemptStore attempts, FakeExecutionStore executions,
        FakeAssignments assignments, FakeProvider provider, FakeAudit? audit = null,
        FakePublisher? publisher = null)
        => new(policies, attempts, executions, assignments,
            new FakeResolver { Provider = provider },
            new SelfHealingAiPromptBuilder(),
            Options.Create(new SelfHealingOptions()),
            audit ?? new FakeAudit(),
            publisher ?? new FakePublisher(),
            new FakeClock(),
            NullLogger<SelfHealingService>.Instance);

    private static SelfHealingPolicyService PolicyService(
        FakePolicyStore policies, FakeAttemptStore attempts, FakeAuth auth,
        FakeExecutionStore? executions = null)
        => new(policies, attempts, executions ?? new FakeExecutionStore(),
            auth, new FakeCurrentUser(), new FakeUsers(), new FakeClock(), new FakeAudit());

    private static WorkerHealingAttemptDto Attempt(
        int order = 1, string action = "click", string status = "Applied", bool applied = true)
        => new(order, action, "css", "#old", "testid", "new-btn", "test-attribute",
            status, 1, applied, false, null);

    // ---------- eligibility ----------

    [Theory]
    [InlineData("click", "Timeout waiting for locator '#x'.", true)]
    [InlineData("fill", "Element not found: #user.", true)]
    [InlineData("assertVisible", "Selector no longer matching.", true)]
    [InlineData("assertText", "Timeout waiting for locator.", false)]
    [InlineData("assertValue", "Timeout waiting for locator.", false)]
    [InlineData("navigate", "net::ERR_NAME_NOT_RESOLVED", false)]
    [InlineData("click", "Request failed with HTTP 500.", false)]
    [InlineData("click", "Unauthorized (401).", false)]
    [InlineData("click", "Cancelled via API.", false)]
    [InlineData("click", "Expected text containing 'Hi'.", false)]
    [InlineData("wait", "Timeout.", false)]
    [InlineData("screenshot", "Timeout.", false)]
    [InlineData("eval", "Timeout.", false)]
    public void Eligibility_MatchesContract(string action, string message, bool expected)
        => Assert.Equal(expected, SelfHealingEligibility.IsHealingEligible(action, message));

    // ---------- AI validator ----------

    [Fact]
    public void AiValidator_RejectsCode_Strategies_And_OversizedValues()
    {
        var result = new AiHealingResult("stub", "m", new[]
        {
            new AiHealingCandidate("css", "javascript:alert(1)", "x", null),
            new AiHealingCandidate("js", "document.x", "y", null),
            new AiHealingCandidate("css", "eval(foo)", "z", null),
            new AiHealingCandidate("css", new string('a', 2001), "big", null),
            new AiHealingCandidate("testid", "ok-btn", "stable", 0.9m),
        });
        var (accepted, rejected) = SelfHealingAiValidator.Validate(
            result, new[] { "css", "xpath", "role", "text", "testid" }, null, 8);
        var single = Assert.Single(accepted);
        Assert.Equal("ok-btn", single.Value);
        Assert.True(rejected.Count >= 4);
    }

    [Fact]
    public void AiValidator_Enforces_MinConfidence_And_Allowlist()
    {
        var result = new AiHealingResult("stub", "m", new[]
        {
            new AiHealingCandidate("css", ".low", "low", 0.2m),
            new AiHealingCandidate("css", ".high", "high", 0.9m),
        });
        var (accepted, _) = SelfHealingAiValidator.Validate(
            result, new[] { "testid" }, 0.5m, 8);
        Assert.Empty(accepted);
    }

    [Fact]
    public void AiValidator_ParseOrThrow_RejectsMalformed()
    {
        Assert.Throws<ValidationException>(() =>
            SelfHealingAiValidator.ParseOrThrow("stub", "m", "not-json", "self-healing-v1"));
        Assert.Throws<ValidationException>(() =>
            SelfHealingAiValidator.ParseOrThrow("stub", "m", """{"foo": []}""", "self-healing-v1"));
        var parsed = SelfHealingAiValidator.ParseOrThrow(
            "stub", "m", """{"candidates": [{"strategy": "testid", "value": "a"}]}""", "self-healing-v1");
        Assert.Single(parsed.Candidates);
    }

    // ---------- policy service ----------

    [Fact]
    public async Task Policy_DisabledByDefault_And_RequiresPermissions()
    {
        var policies = new FakePolicyStore();
        var auth = new FakeAuth();
        auth.Projects.Add(ProjectA);
        auth.Permissions.Add(Permissions.ExecutionsRead);
        var service = PolicyService(policies, new FakeAttemptStore(), auth);

        Assert.Null(await service.GetAsync(ProjectA, CancellationToken.None));

        // Write without settings.manage → forbidden.
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpsertAsync(
            new UpsertSelfHealingPolicyCommand(ProjectA, true, false, null, null, null),
            CancellationToken.None));

        auth.Permissions.Add(Permissions.SettingsManage);
        var saved = await service.UpsertAsync(
            new UpsertSelfHealingPolicyCommand(ProjectA, true, false, null, null, null),
            CancellationToken.None);
        Assert.True(saved.Enabled);
        Assert.False(saved.AiFallbackEnabled);
        Assert.Equal(1, saved.MaxAttemptsPerStep);
    }

    [Fact]
    public async Task Policy_Rejects_AiWithoutEnabled_And_BadThresholds()
    {
        var auth = new FakeAuth();
        auth.Projects.Add(ProjectA);
        auth.Permissions.Add(Permissions.SettingsManage);
        var service = PolicyService(new FakePolicyStore(), new FakeAttemptStore(), auth);

        await Assert.ThrowsAsync<ValidationException>(() => service.UpsertAsync(
            new UpsertSelfHealingPolicyCommand(ProjectA, false, true, null, null, null),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.UpsertAsync(
            new UpsertSelfHealingPolicyCommand(ProjectA, true, false, 101, null, null),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.UpsertAsync(
            new UpsertSelfHealingPolicyCommand(ProjectA, true, false, null, 2m, null),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => service.UpsertAsync(
            new UpsertSelfHealingPolicyCommand(ProjectA, true, false, null, null, new[] { "js" }),
            CancellationToken.None));
    }

    [Fact]
    public async Task Policy_CrossProject_Isolated()
    {
        var auth = new FakeAuth();
        auth.Projects.Add(ProjectA);
        auth.Permissions.Add(Permissions.ExecutionsRead);
        auth.Permissions.Add(Permissions.SettingsManage);
        var service = PolicyService(new FakePolicyStore(), new FakeAttemptStore(), auth);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.GetAsync(ProjectB, CancellationToken.None));
    }

    // ---------- suggest ----------

    [Fact]
    public async Task Suggest_DisabledPolicy_NeverCallsProvider()
    {
        var (executions, _) = ExecutionFixture();
        var provider = new FakeProvider
        {
            HealingResult = new AiHealingResult("stub", "m", new[]
            {
                new AiHealingCandidate("testid", "x", "r", null),
            }),
        };
        var service = HealingService(new FakePolicyStore(), new FakeAttemptStore(),
            executions, new FakeAssignments(), provider);
        await Assert.ThrowsAsync<ConflictException>(() => service.SuggestCandidatesAsync(
            ExecutionA, new HealingEvidenceDto("click", "css=#o", "frag", new[] { "a" }, new[] { "t" }),
            CancellationToken.None));
        Assert.Equal(0, provider.HealingCalls);
    }

    [Fact]
    public async Task Suggest_Bounds_Redacts_And_ValidatesProviderOutput()
    {
        var (executions, _) = ExecutionFixture();
        var policies = new FakePolicyStore();
        policies.Rows[ProjectA] = new SelfHealingPolicy
        {
            ProjectId = ProjectA, Enabled = true, AiFallbackEnabled = true,
        };
        var provider = new FakeProvider
        {
            HealingResult = new AiHealingResult("stub", "m", new[]
            {
                new AiHealingCandidate("css", "eval(x)", "evil", null),
                new AiHealingCandidate("testid", "good-btn", "stable", 0.8m),
            }),
        };
        var audit = new FakeAudit();
        var service = HealingService(policies, new FakeAttemptStore(), executions,
            new FakeAssignments(), provider, audit);

        var accepted = await service.SuggestCandidatesAsync(ExecutionA,
            new HealingEvidenceDto("click", "css=#o",
                "password=hunter2 dom", new[] { "a" }, new[] { "t" }),
            CancellationToken.None);
        Assert.Equal(1, provider.HealingCalls);
        var single = Assert.Single(accepted);
        Assert.Equal("good-btn", single.Value);
        // Secrets redacted before the provider sees evidence.
        Assert.DoesNotContain("hunter2", provider.SeenHealingRequest!.Evidence.DomFragment);
        Assert.Contains("[REDACTED]", provider.SeenHealingRequest.Evidence.DomFragment);
        Assert.Contains("self-healing.ai_suggested", audit.Actions);
    }

    [Fact]
    public async Task Suggest_ProviderFailure_Isolated_ToEmptyResult()
    {
        var (executions, _) = ExecutionFixture();
        var policies = new FakePolicyStore();
        policies.Rows[ProjectA] = new SelfHealingPolicy
        {
            ProjectId = ProjectA, Enabled = true, AiFallbackEnabled = true,
        };
        var provider = new FakeProvider
        {
            HealingFailure = AiProviderException.Unavailable("stub", "down"),
        };
        var audit = new FakeAudit();
        var service = HealingService(policies, new FakeAttemptStore(), executions,
            new FakeAssignments(), provider, audit);
        var accepted = await service.SuggestCandidatesAsync(ExecutionA,
            new HealingEvidenceDto("click", "css=#o", "frag", Array.Empty<string>(), Array.Empty<string>()),
            CancellationToken.None);
        Assert.Empty(accepted);
        Assert.Contains("self-healing.ai_failed", audit.Actions);
    }

    [Fact]
    public async Task Suggest_Cancellation_Preserved()
    {
        var (executions, _) = ExecutionFixture();
        var policies = new FakePolicyStore();
        policies.Rows[ProjectA] = new SelfHealingPolicy
        {
            ProjectId = ProjectA, Enabled = true, AiFallbackEnabled = true,
        };
        var provider = new FakeProvider
        {
            HealingFailure = new OperationCanceledException(),
        };
        var service = HealingService(policies, new FakeAttemptStore(), executions,
            new FakeAssignments(), provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SuggestCandidatesAsync(
            ExecutionA, new HealingEvidenceDto("click", "css=#o", "frag",
                Array.Empty<string>(), Array.Empty<string>()), cts.Token));
    }

    // ---------- record ----------

    [Fact]
    public async Task Record_StaleWorker_Rejected_NothingPersisted()
    {
        var (executions, test) = ExecutionFixture();
        var live = new GridAssignment
        {
            ExecutionId = ExecutionA, ExecutionTestId = test.Id,
            WorkerAssignmentRef = test.Id.ToString("N"),
            Status = GridAssignmentStatus.Running,
        };
        test.StartedAssignmentId = live.Id;
        var service = HealingService(new FakePolicyStore(), new FakeAttemptStore(),
            executions, new FakeAssignments { Active = live }, new FakeProvider());
        var staleId = Guid.NewGuid();
        await Assert.ThrowsAsync<ConflictException>(() => service.RecordAttemptsAsync(
            ExecutionA, staleId, new[] { Attempt() }, CancellationToken.None));
    }

    [Fact]
    public async Task Record_AppliesOnce_ConvergesOnDuplicates_RejectsBadRows()
    {
        var (executions, test) = ExecutionFixture();
        var live = new GridAssignment
        {
            ExecutionId = ExecutionA, ExecutionTestId = test.Id,
            WorkerAssignmentRef = test.Id.ToString("N"),
            Status = GridAssignmentStatus.Running,
        };
        test.StartedAssignmentId = live.Id;
        var attempts = new FakeAttemptStore();
        var publisher = new FakePublisher();
        var service = HealingService(new FakePolicyStore(), attempts, executions,
            new FakeAssignments { Active = live }, new FakeProvider(), publisher: publisher);

        var persisted = await service.RecordAttemptsAsync(ExecutionA, live.Id,
            new[]
            {
                Attempt(),
                Attempt(), // duplicate step → converged, not duplicated
                Attempt(order: 2, action: "assertText"), // non-healable action → ignored
                Attempt(order: 3, status: "Bogus", applied: false), // bad status → ignored
            }, CancellationToken.None);
        Assert.Equal(1, persisted);
        Assert.Single(attempts.Rows);
        var row = attempts.Rows[0];
        Assert.Equal(ProjectA, row.ProjectId);
        Assert.Equal(ExecutionA, row.ExecutionId);
        Assert.Equal(VersionA, row.TestCaseVersionId);
        Assert.Equal(live.Id, row.AssignmentId);
        Assert.True(row.WasApplied);
        Assert.Contains("SelfHealingApplied", publisher.Events);
    }

    [Fact]
    public async Task Record_LegacyLeaseFree_Path_Persists()
    {
        var (executions, _) = ExecutionFixture();
        var attempts = new FakeAttemptStore();
        var service = HealingService(new FakePolicyStore(), attempts, executions,
            new FakeAssignments { Active = null }, new FakeProvider());
        var persisted = await service.RecordAttemptsAsync(
            ExecutionA, null, new[] { Attempt() }, CancellationToken.None);
        Assert.Equal(1, persisted);
        Assert.Null(attempts.Rows[0].AssignmentId);
    }

    [Fact]
    public async Task Record_LegacyPath_RejectsClaimedLeaseId()
    {
        var (executions, _) = ExecutionFixture();
        var service = HealingService(new FakePolicyStore(), new FakeAttemptStore(), executions,
            new FakeAssignments { Active = null }, new FakeProvider());
        await Assert.ThrowsAsync<ConflictException>(() => service.RecordAttemptsAsync(
            ExecutionA, Guid.NewGuid(), new[] { Attempt() }, CancellationToken.None));
    }

    [Fact]
    public async Task Record_RedactsSecrets_BeforePersistence()
    {
        var (executions, test) = ExecutionFixture();
        var live = new GridAssignment
        {
            ExecutionId = ExecutionA, ExecutionTestId = test.Id,
            WorkerAssignmentRef = test.Id.ToString("N"),
            Status = GridAssignmentStatus.Running,
        };
        test.StartedAssignmentId = live.Id;
        var attempts = new FakeAttemptStore();
        var service = HealingService(new FakePolicyStore(), attempts, executions,
            new FakeAssignments { Active = live }, new FakeProvider());
        await service.RecordAttemptsAsync(ExecutionA, live.Id,
            new[]
            {
                new WorkerHealingAttemptDto(1, "click", "css", "#o", "testid", "b",
                    "test-attribute", "Failed", 1, false, false, "password=hunter2 boom"),
            }, CancellationToken.None);
        Assert.DoesNotContain("hunter2", attempts.Rows[0].ErrorMessage!);
    }

    // ---------- stub provider ----------

    [Fact]
    public async Task StubHealing_NeverFabricates_And_EchoesTextOnlyWhenAllowed()
    {
        var stub = new StubAiProvider(new FakeClockProvider());
        var empty = await stub.SuggestHealingCandidatesAsync(
            new AiHealingRequest(
                new AiHealingEvidence("click", "css=#o", "frag",
                    Array.Empty<string>(), Array.Empty<string>()),
                new[] { "testid" }), CancellationToken.None);
        Assert.Empty(empty.Candidates);

        var echoed = await stub.SuggestHealingCandidatesAsync(
            new AiHealingRequest(
                new AiHealingEvidence("click", "css=#o", "frag",
                    Array.Empty<string>(), new[] { "Submit" }),
                new[] { "text" }), CancellationToken.None);
        Assert.Single(echoed.Candidates);
    }

    private sealed class FakeClockProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
