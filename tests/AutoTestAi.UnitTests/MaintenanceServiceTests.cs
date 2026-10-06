using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;using AutoTestAi.Application.Maintenance;
using AutoTestAi.Application.Reports;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.TestCases;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Phase 4 Slice 4: maintenance service — auth matrix, scan/dedupe,
/// approve/reject lifecycle, stale-version protection, idempotent apply,
/// audit safety, and scan throttling.
/// </summary>
public sealed class MaintenanceServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid TestA = Guid.NewGuid();
    private static readonly Guid VersionA = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore : IMaintenanceStore
    {
        public readonly List<MaintenanceProposal> Proposals = new();
        public List<HealingEvidenceRow> Evidence = new();
        public List<FailedVerdictRow> Verdicts = new();
        public List<Guid> ExcludedTests = new();
        public List<VersionSnapshotRow> Versions = new();
        public int Saves;

        public Task AddAsync(MaintenanceProposal proposal, CancellationToken ct)
        {
            Proposals.Add(proposal);
            return Task.CompletedTask;
        }

        public Task<MaintenanceProposal?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<MaintenanceProposal?>(Proposals.FirstOrDefault(p => p.Id == id));

        public Task<IReadOnlyList<MaintenanceProposalRow>> ListAsync(
            Guid projectId, MaintenanceProposalStatus? status, string? signal, string? search,
            int skip, int take, CancellationToken ct)
        {
            ProposalsSeen = (projectId, status, signal, search, skip, take);
            var items = Proposals
                .Where(p => p.ProjectId == projectId)
                .OrderByDescending(p => p.Confidence)
                .ThenByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .Skip(skip).Take(take)
                .Select(p => new MaintenanceProposalRow(
                    p.Id, p.ProjectId, p.TestCaseId, "T-1", "Title",
                    p.TestCaseVersionId, 4, p.StepOrder, p.StepAction,
                    p.OriginalStrategy, p.OriginalValue, p.ProposedStrategy, p.ProposedValue,
                    p.HealingStrategy.ToString(), p.SignalType, p.Confidence, p.OccurrenceCount,
                    p.Status.ToString(), p.ReviewedBy, p.ReviewedAt, p.RejectionReason,
                    p.CreatedVersionId, p.CreatedAt, p.UpdatedAt))
                .ToList();
            return Task.FromResult<IReadOnlyList<MaintenanceProposalRow>>(items);
        }

        public (Guid Project, MaintenanceProposalStatus? Status, string? Signal, string? Search, int Skip, int Take) ProposalsSeen;

        public Task<int> CountAsync(
            Guid projectId, MaintenanceProposalStatus? status, string? signal, string? search, CancellationToken ct)
            => Task.FromResult(Proposals.Count(p => p.ProjectId == projectId));

        public Task<MaintenanceProposal?> FindOpenAsync(
            Guid testCaseId, Guid versionId, int stepOrder,
            string proposedStrategy, string proposedValue, CancellationToken ct)
            => Task.FromResult<MaintenanceProposal?>(Proposals.FirstOrDefault(p =>
                p.TestCaseId == testCaseId && p.TestCaseVersionId == versionId &&
                p.StepOrder == stepOrder && p.ProposedStrategy == proposedStrategy &&
                p.ProposedValue == proposedValue && p.Status == MaintenanceProposalStatus.Proposed));

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HealingEvidenceRow>> ListHealingEvidenceAsync(
            Guid projectId, DateTimeOffset since, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HealingEvidenceRow>>(Evidence.Take(take).ToList());

        public Task<IReadOnlyList<FailedVerdictRow>> ListFailedVerdictsAsync(
            Guid projectId, DateTimeOffset since, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FailedVerdictRow>>(Verdicts.Take(take).ToList());

        public Task<IReadOnlyList<Guid>> ListOpenAppBugTestCaseIdsAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Guid>>(ExcludedTests.ToList());

        public Task<IReadOnlyList<VersionSnapshotRow>> ListLatestVersionsAsync(
            Guid projectId, IReadOnlyList<Guid> testCaseIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VersionSnapshotRow>>(Versions
                .Where(v => testCaseIds.Contains(v.TestCaseId)).ToList());
    }

    private sealed class FakeCases : ITestCaseService
    {
        public readonly Dictionary<Guid, TestCaseDto> Cases = new();
        public readonly Dictionary<Guid, List<TestCaseVersionDto>> Versions = new();
        public readonly List<UpdateTestCaseCommand> SeenUpdates = new();
        public int UpdateCalls;

        public Task<TestCaseDto> GetByIdAsync(Guid testCaseId, CancellationToken ct)
            => Cases.TryGetValue(testCaseId, out var c)
                ? Task.FromResult(c)
                : throw new NotFoundException("Test case not found.");

        public Task<TestCaseVersionDto> GetVersionAsync(Guid testCaseId, Guid versionId, CancellationToken ct)
        {
            var version = Versions.TryGetValue(testCaseId, out var list)
                ? list.FirstOrDefault(v => v.Id == versionId)
                : null;
            return version is null
                ? throw new NotFoundException("Test case version not found.")
                : Task.FromResult(version);
        }

        public Task<IReadOnlyList<TestCaseVersionDto>> ListVersionsAsync(Guid testCaseId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TestCaseVersionDto>>(
                Versions.TryGetValue(testCaseId, out var list)
                    ? list.OrderByDescending(v => v.VersionNumber).ToList()
                    : new List<TestCaseVersionDto>());

        public Task<TestCaseDto> UpdateAsync(Guid testCaseId, UpdateTestCaseCommand command, CancellationToken ct)
        {
            UpdateCalls++;
            SeenUpdates.Add(command);
            var list = Versions[testCaseId];
            var next = list.Max(v => v.VersionNumber) + 1;
            var steps = TestStep.Parse(command.StructuredSteps).Select(s =>
                new TestStepDto(s.Order, s.Action, s.Target, s.Value)).ToList();
            var created = new TestCaseVersionDto(Guid.NewGuid(), testCaseId, next, null, steps,
                null, null, null, ReviewStatus.Pending.ToString(), null, Now);
            list.Add(created);
            return Task.FromResult(Cases[testCaseId]);
        }

        public Task<PagedResult<TestCaseListItemDto>> ListAsync(Guid p, int a, int b, TestCaseFilters f, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TestCaseDto> CreateAsync(CreateTestCaseCommand c, CancellationToken ct)
            => throw new NotImplementedException();
        public Task ArchiveAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TestCaseVersionDto> ReviewAsync(Guid a, ReviewTestCaseCommand b, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<(string Action, string? Metadata)> Events = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        {
            Events.Add((action, metadataJson));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUsers : IUserDirectory
    {
        public Guid ReviewerId = Guid.NewGuid();
        public Task<Guid?> FindAppUserIdAsync(string externalIdentityId, CancellationToken ct)
            => Task.FromResult<Guid?>(ReviewerId);
        public Task<Guid> EnsureProvisionedAsync(string externalIdentityId, string? email, string? displayName, CancellationToken ct)
            => Task.FromResult(ReviewerId);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static StubCurrentUser Member() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "user-1",
        Roles = ["tester"], Permissions = RolePermissions.Resolve(["tester"]),
    };

    private static StubCurrentUser Viewer() => new()
    {
        IsAuthenticated = true, ExternalIdentityId = "viewer-1",
        Roles = ["viewer"], Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private sealed class Harness
    {
        public readonly FakeStore Store = new();
        public readonly FakeCases Cases = new();
        public readonly FakeAudit Audit = new();
        public readonly FakeUsers Users = new();
        public readonly FakeReportQueryStore Reports = new();
        public MaintenanceService Service = null!;
        public AuthorizationService Auth = null!;

        public Harness(ICurrentUserService? user = null, bool member = true, int scanLimit = 1000)
        {
            user ??= Member();
            var memberships = new StubMembershipStore();
            if (member && user.ExternalIdentityId is not null)
                memberships.Add(user.ExternalIdentityId, ProjectA);
            Auth = new AuthorizationService(user, memberships);
            Service = new MaintenanceService(Store, Cases, Auth, Audit, Reports,
                new AiGenerationRateLimiter(
                    Options.Create(new Application.AI.AiOptions()),
                    new FixedClock()),
                user, Users, new FixedClock());
        }

        public void SeedApprovedVersion(
            Guid? versionId = null, string target = "css=#login", int stepOrder = 3,
            string review = "Approved", int versionNumber = 4)
        {
            var vid = versionId ?? VersionA;
            Cases.Cases[TestA] = new TestCaseDto(TestA, ProjectA, "T-1", "Title", null, null,
                "playwright", "web", "High", "Active", "ai", versionNumber, review,
                null, Now, Now);
            Cases.Versions[TestA] = new List<TestCaseVersionDto>
            {
                new(vid, TestA, versionNumber, "code",
                    new[] { new TestStepDto(stepOrder, "click", target, null) },
                    null, null, null, review, null, Now),
            };
            Store.Versions.Add(new VersionSnapshotRow(TestA, vid, versionNumber, review,
                """[{"order":3,"action":"click","target":"css=#login","value":null}]""",
                "T-1", "Title"));
        }

        public void SeedHeals(int count = 3, string proposed = "testLoginBtn")
        {
            var rows = new List<HealingEvidenceRow>();
            for (var i = 0; i < count; i++)
            {
                var createdAt = Now.AddDays(-1).AddHours(i);
                rows.Add(new HealingEvidenceRow(
                    Guid.NewGuid(), ProjectA, TestA, VersionA, 3, "click",
                    "css", "#login", "testid", proposed,
                    false, true, SelfHealingStatus.Applied,
                    SelfHealingStrategy.TestAttribute,
                    Guid.NewGuid(), ExecutionStatus.Passed, FailureClassification.TestFailure,
                    createdAt));
            }
            Store.Evidence.AddRange(rows);
        }

        public void SeedHighForecast()
        {
            // Five straight failures: recent=100, deterioration=0,
            // streak=5→100 → score=70 → High.
            Reports.OutcomeRows.Add(new TestOutcomeRow(TestA, 0, 5, 0, Now.AddDays(-1)));
            Reports.VerdictRows.AddRange(new[]
            {
                new TestVerdictRow(TestA, false, Now.AddDays(-1)),
                new TestVerdictRow(TestA, false, Now.AddDays(-2)),
                new TestVerdictRow(TestA, false, Now.AddDays(-3)),
                new TestVerdictRow(TestA, false, Now.AddDays(-4)),
                new TestVerdictRow(TestA, false, Now.AddDays(-5)),
            });
        }

        public MaintenanceProposal SeedProposal(
            MaintenanceProposalStatus status = MaintenanceProposalStatus.Proposed,
            Guid? versionId = null)
        {
            var proposal = new MaintenanceProposal
            {
                ProjectId = ProjectA,
                TestCaseId = TestA,
                TestCaseVersionId = versionId ?? VersionA,
                StepOrder = 3,
                StepAction = "click",
                OriginalStrategy = "css",
                OriginalValue = "#login",
                ProposedStrategy = "testid",
                ProposedValue = "testLoginBtn",
                HealingStrategy = SelfHealingStrategy.TestAttribute,
                SignalType = MaintenanceDetector.SignalHealedLocator,
                Confidence = 70,
                OccurrenceCount = 3,
                Status = status,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            Store.Proposals.Add(proposal);
            return proposal;
        }
    }

    [Fact]
    public async Task List_Unauthenticated_Throws401()
    {
        var harness = new Harness(new StubCurrentUser { IsAuthenticated = false }, member: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.Service.ListAsync(ProjectA, new MaintenanceProposalFilters(null, null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task List_NonMember_Throws403()
    {
        var harness = new Harness(Member(), member: false);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            harness.Service.ListAsync(ProjectA, new MaintenanceProposalFilters(null, null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task List_Viewer_CanRead()
    {
        var harness = new Harness(Viewer());
        var page = await harness.Service.ListAsync(ProjectA,
            new MaintenanceProposalFilters(null, null, null), 1, 25, CancellationToken.None);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task List_InvalidStatus_Throws400()
    {
        var harness = new Harness();
        await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Service.ListAsync(ProjectA,
                new MaintenanceProposalFilters("Bogus", null, null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task List_InvalidSignal_Throws400()
    {
        var harness = new Harness();
        await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Service.ListAsync(ProjectA,
                new MaintenanceProposalFilters(null, "bogus", null), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task List_ForwardsFilters_And_ClampsPagination()
    {
        var harness = new Harness();
        var page = await harness.Service.ListAsync(ProjectA,
            new MaintenanceProposalFilters("Proposed", "healed-locator", " t-1 "), 0, 500, CancellationToken.None);
        Assert.Equal(ProjectA, harness.Store.ProposalsSeen.Project);
        Assert.Equal(MaintenanceProposalStatus.Proposed, harness.Store.ProposalsSeen.Status);
        Assert.Equal("healed-locator", harness.Store.ProposalsSeen.Signal);
        Assert.Equal("t-1", harness.Store.ProposalsSeen.Search);
        Assert.Equal(0, harness.Store.ProposalsSeen.Skip);
        Assert.Equal(100, harness.Store.ProposalsSeen.Take);
        Assert.Equal(1, page.Page);
        Assert.Equal(100, page.PageSize);
    }

    [Fact]
    public async Task Scan_Viewer_CannotScan()
    {
        var harness = new Harness(Viewer());
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            harness.Service.ScanAsync(ProjectA, CancellationToken.None));
    }

    [Fact]
    public async Task Scan_CreatesProposals_WithAudit()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        harness.SeedHeals();
        harness.SeedHighForecast();

        var summary = await harness.Service.ScanAsync(ProjectA, CancellationToken.None);

        Assert.Equal(1, summary.CandidatesDetected);
        Assert.Equal(1, summary.ProposalsCreated);
        Assert.Equal(0, summary.ProposalsAlreadyExisting);
        var proposal = Assert.Single(harness.Store.Proposals);
        Assert.Equal(MaintenanceProposalStatus.Proposed, proposal.Status);
        Assert.Equal("testid", proposal.ProposedStrategy);
        Assert.Equal("testLoginBtn", proposal.ProposedValue);
        Assert.True(proposal.Confidence >= 60);
        Assert.Null(proposal.ProposedBy);
        Assert.Contains(harness.Audit.Events, e => e.Action == "maintenance.scan");
        Assert.Contains(harness.Audit.Events, e => e.Action == "maintenance.proposed");
    }

    [Fact]
    public async Task Scan_Repeated_DoesNotDuplicate()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        harness.SeedHeals();
        harness.SeedHighForecast();

        var first = await harness.Service.ScanAsync(ProjectA, CancellationToken.None);
        var second = await harness.Service.ScanAsync(ProjectA, CancellationToken.None);

        Assert.Equal(1, first.ProposalsCreated);
        Assert.Equal(0, second.ProposalsCreated);
        Assert.Equal(1, second.ProposalsAlreadyExisting);
        Assert.Single(harness.Store.Proposals);
    }

    [Fact]
    public async Task Scan_AuditMetadata_IsSafe()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        harness.SeedHeals();
        harness.SeedHighForecast();
        await harness.Service.ScanAsync(ProjectA, CancellationToken.None);
        foreach (var (_, metadata) in harness.Audit.Events)
        {
            Assert.DoesNotContain("#login", metadata ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("testLoginBtn", metadata ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Get_WrongProject_Throws404()
    {
        var harness = new Harness();
        harness.SeedProposal();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetAsync(ProjectA, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Approve_CreatesExactlyOnePendingVersion_WithOnlyLocatorChanged()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        var proposal = harness.SeedProposal();

        var result = await harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None);

        Assert.Equal("Applied", result.Status);
        Assert.Equal(1, harness.Cases.UpdateCalls);
        var command = Assert.Single(harness.Cases.SeenUpdates);
        Assert.True(command.HasStructuredSteps);
        Assert.False(command.HasSourceCode);
        var steps = TestStep.Parse(command.StructuredSteps);
        var changed = Assert.Single(steps.Where(s => s.Order == 3));
        Assert.Equal("testid=testLoginBtn", changed.Target);
        Assert.Equal("click", changed.Action);
        Assert.Equal(MaintenanceProposalStatus.Applied, proposal.Status);
        Assert.Equal(harness.Users.ReviewerId, proposal.ReviewedBy);
        Assert.NotNull(proposal.ReviewedAt);
        Assert.NotNull(proposal.CreatedVersionId);
        var created = harness.Cases.Versions[TestA].Single(v => v.Id == proposal.CreatedVersionId);
        Assert.Equal("Pending", created.ReviewStatus);
        Assert.Equal(5, created.VersionNumber);
        // Original Approved version untouched.
        var source = harness.Cases.Versions[TestA].Single(v => v.Id == VersionA);
        Assert.Equal("Approved", source.ReviewStatus);
        Assert.Equal("css=#login", source.StructuredSteps.Single(s => s.Order == 3).Target);
        Assert.Contains(harness.Audit.Events, e => e.Action == "maintenance.approved");
        Assert.Contains(harness.Audit.Events, e => e.Action == "maintenance.applied");
        Assert.Equal(result.CreatedVersionId, proposal.CreatedVersionId);
    }

    [Fact]
    public async Task Approve_AlreadyApplied_IsIdempotent()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        var proposal = harness.SeedProposal();
        var first = await harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None);
        var second = await harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None);
        Assert.Equal(first.CreatedVersionId, second.CreatedVersionId);
        Assert.Equal(1, harness.Cases.UpdateCalls);
        Assert.Equal(1, harness.Cases.Versions[TestA].Count(v => v.VersionNumber > 4));
    }

    [Fact]
    public async Task Approve_Rejected_Throws409()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        var proposal = harness.SeedProposal(MaintenanceProposalStatus.Rejected);
        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None));
        Assert.Equal(0, harness.Cases.UpdateCalls);
    }

    [Fact]
    public async Task Approve_Superseded_Throws409()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        var proposal = harness.SeedProposal(MaintenanceProposalStatus.Superseded);
        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None));
        Assert.Equal(0, harness.Cases.UpdateCalls);
    }

    [Fact]
    public async Task Approve_StaleVersion_Supersedes_And_Returns409()
    {
        var harness = new Harness();
        // Latest is v5; the proposal pins superseded v4.
        harness.SeedApprovedVersion(versionNumber: 5);
        var v4 = Guid.NewGuid();
        harness.Cases.Versions[TestA].Add(new TestCaseVersionDto(v4, TestA, 4, "code",
            new[] { new TestStepDto(3, "click", "css=#login", null) },
            null, null, null, "Approved", null, Now.AddDays(-2)));
        var proposal = harness.SeedProposal(versionId: v4);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None));
        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MaintenanceProposalStatus.Superseded, proposal.Status);
        Assert.Equal(0, harness.Cases.UpdateCalls);
    }

    [Fact]
    public async Task Approve_ChangedAction_Supersedes()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion(target: "css=#login");
        // Simulate a concurrent edit: latest step action changed.
        harness.Cases.Versions[TestA][0] = harness.Cases.Versions[TestA][0] with
        {
            StructuredSteps = new[] { new TestStepDto(3, "fill", "css=#login", null) },
        };
        harness.Store.Versions.Clear();
        harness.Store.Versions.Add(new VersionSnapshotRow(TestA, VersionA, 4, "Approved",
            """[{"order":3,"action":"fill","target":"css=#login","value":null}]""", "T-1", "Title"));
        var proposal = harness.SeedProposal();
        proposal.StepAction = "click";

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None));
        Assert.Equal(MaintenanceProposalStatus.Superseded, proposal.Status);
        Assert.Equal(0, harness.Cases.UpdateCalls);
    }

    [Fact]
    public async Task Approve_ChangedLocator_Supersedes()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion(target: "css=#changed");
        var proposal = harness.SeedProposal();

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None));
        Assert.Equal(MaintenanceProposalStatus.Superseded, proposal.Status);
        Assert.Equal(0, harness.Cases.UpdateCalls);
    }

    [Fact]
    public async Task Approve_NonApprovedSource_Supersedes()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion(review: "ChangesRequested");
        var proposal = harness.SeedProposal();

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None));
        Assert.Equal(MaintenanceProposalStatus.Superseded, proposal.Status);
        Assert.Equal(0, harness.Cases.UpdateCalls);
    }

    [Fact]
    public async Task Approve_Viewer_CannotApprove()
    {
        var harness = new Harness(Viewer());
        var proposal = harness.SeedProposal();
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            harness.Service.ApproveAsync(ProjectA, proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Reject_PersistsReason_WithoutVersion()
    {
        var harness = new Harness();
        harness.SeedApprovedVersion();
        var proposal = harness.SeedProposal();

        var dto = await harness.Service.RejectAsync(ProjectA, proposal.Id, "  Locator still valid  ", CancellationToken.None);

        Assert.Equal("Rejected", dto.Status);
        Assert.Equal("Locator still valid", proposal.RejectionReason);
        Assert.Equal(harness.Users.ReviewerId, proposal.ReviewedBy);
        Assert.NotNull(proposal.ReviewedAt);
        Assert.Equal(0, harness.Cases.UpdateCalls);
        Assert.Contains(harness.Audit.Events, e => e.Action == "maintenance.rejected");
    }

    [Fact]
    public async Task Reject_RequiresReason()
    {
        var harness = new Harness();
        var proposal = harness.SeedProposal();
        await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Service.RejectAsync(ProjectA, proposal.Id, "   ", CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Service.RejectAsync(ProjectA, proposal.Id, new string('x', 501), CancellationToken.None));
        Assert.Equal(MaintenanceProposalStatus.Proposed, proposal.Status);
    }

    [Fact]
    public async Task Reject_NonProposed_Throws409()
    {
        var harness = new Harness();
        var proposal = harness.SeedProposal(MaintenanceProposalStatus.Applied);
        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.RejectAsync(ProjectA, proposal.Id, "nope", CancellationToken.None));
    }

    [Fact]
    public async Task Reject_Viewer_CannotReject()
    {
        var harness = new Harness(Viewer());
        var proposal = harness.SeedProposal();
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            harness.Service.RejectAsync(ProjectA, proposal.Id, "nope", CancellationToken.None));
    }

    [Fact]
    public async Task Scan_Throttle_Enforced()
    {
        var harness = new Harness(scanLimit: 1000);
        harness.SeedApprovedVersion();
        // Thirty rapid scans exhaust the 30/minute budget; the 31st is limited.
        for (var i = 0; i < 30; i++)
            await harness.Service.ScanAsync(ProjectA, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<Application.AI.AiProviderException>(() =>
            harness.Service.ScanAsync(ProjectA, CancellationToken.None));
        Assert.Equal(Application.AI.AiProviderErrorKind.RateLimited, ex.Kind);
    }
}
