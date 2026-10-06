using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Phase 4 Slice 4: human-gated web-locator maintenance — scan, list/detail,
/// approve (Pending version), reject, stale/supersede, isolation, auth
/// matrix, audit persistence, and execution-gate preservation.
/// </summary>
public sealed class MaintenanceApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    private Guid _caseScan;
    private Guid _versionScan;
    private Guid _caseApprove;
    private Guid _caseReject;
    private Guid _caseStale;
    private Guid _caseBug;
    private Guid _caseGate;
    private Guid _caseReview;

    public MaintenanceApiTests(Slice1ApiFactory factory) => _factory = factory;

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
                var manager = new User { ExternalIdentityId = "mnt-manager", Email = "m@x", DisplayName = "M" };
                var viewer = new User { ExternalIdentityId = "mnt-viewer", Email = "v@x", DisplayName = "V" };
                var admin = new User { ExternalIdentityId = "mnt-admin", Email = "a@x", DisplayName = "A" };
                var outsider = new User { ExternalIdentityId = "mnt-outsider", Email = "o@x", DisplayName = "O" };
                db.Users.AddRange(manager, viewer, admin, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Maint Alpha", Key = "MNA" },
                    new Project { Id = pb, Name = "Maint Beta", Key = "MNB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = manager.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });
                await db.SaveChangesAsync();

                var now = DateTimeOffset.UtcNow;
                _caseScan = await SeedHealingCaseAsync(db, pa, "MNT-SCAN", "Scan case", now, withFailures: true);
                _versionScan = await LatestVersionIdAsync(db, _caseScan);
                _caseApprove = await SeedHealingCaseAsync(db, pa, "MNT-APPR", "Approve case", now, withFailures: true);
                _caseReject = await SeedHealingCaseAsync(db, pa, "MNT-REJ", "Reject case", now, withFailures: true);
                _caseStale = await SeedHealingCaseAsync(db, pa, "MNT-STALE", "Stale case", now, withFailures: true);
                _caseBug = await SeedHealingCaseAsync(db, pa, "MNT-BUG", "Bug case", now, withFailures: false);
                await SeedOpenAppBugAsync(db, pa, _caseBug);
                _caseGate = await SeedPlainCaseAsync(db, pa, "MNT-GATE", "Gate case");
                _caseReview = await SeedPlainCaseAsync(db, pa, "MNT-REV", "Review case");
                await db.SaveChangesAsync();
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static async Task<Guid> SeedPlainCaseAsync(
        AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext db, Guid projectId, string key, string title)
    {
        var tc = new TestCase
        {
            ProjectId = projectId, TestKey = key, Title = title,
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
        };
        db.TestCases.Add(tc);
        db.TestCaseVersions.Add(new TestCaseVersion
        {
            TestCaseId = tc.Id, VersionNumber = 1,
            ReviewStatus = ReviewStatus.Approved,
            StructuredSteps = JsonDocument.Parse(
                """[{"order":1,"action":"navigate","target":"https://example.test","value":null},{"order":2,"action":"click","target":"css=#login","value":null}]"""),
        });
        await db.SaveChangesAsync();
        return tc.Id;
    }

    private static async Task<Guid> SeedHealingCaseAsync(
        AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext db, Guid projectId,
        string key, string title, DateTimeOffset now, bool withFailures)
    {
        var caseId = await SeedPlainCaseAsync(db, projectId, key, title);
        var version = await db.TestCaseVersions.SingleAsync(v => v.TestCaseId == caseId);
        for (var i = 0; i < 3; i++)
        {
            var execution = new Execution { ProjectId = projectId, Status = ExecutionStatus.Passed, CreatedAt = now.AddDays(-1).AddHours(i) };
            db.Executions.Add(execution);
            await db.SaveChangesAsync();
            var test = new ExecutionTest
            {
                ExecutionId = execution.Id, TestCaseId = caseId, TestCaseVersionId = version.Id,
                Status = ExecutionTestStatus.Passed, FailureClassification = FailureClassification.Unknown,
                CreatedAt = now.AddDays(-1).AddHours(i),
            };
            db.ExecutionTests.Add(test);
            await db.SaveChangesAsync();
            db.SelfHealingAttempts.Add(new SelfHealingAttempt
            {
                ProjectId = projectId, ExecutionId = execution.Id, ExecutionTestId = test.Id,
                TestCaseId = caseId, TestCaseVersionId = version.Id,
                StepOrder = 2, StepAction = "click",
                OriginalStrategy = "css", OriginalValue = "#login",
                RecoveredStrategy = "testid", RecoveredValue = "testLoginBtn",
                HealingStrategy = SelfHealingStrategy.TestAttribute,
                Status = SelfHealingStatus.Applied, CandidateCount = 1,
                WasApplied = true, IsAiAssisted = false,
                CreatedAt = now.AddDays(-1).AddHours(i),
            });
        }
        if (withFailures)
        {
            for (var i = 0; i < 2; i++)
            {
                var execution = new Execution { ProjectId = projectId, Status = ExecutionStatus.Failed, CreatedAt = now.AddDays(-2).AddHours(i) };
                db.Executions.Add(execution);
                await db.SaveChangesAsync();
                db.ExecutionTests.Add(new ExecutionTest
                {
                    ExecutionId = execution.Id, TestCaseId = caseId, TestCaseVersionId = version.Id,
                    Status = ExecutionTestStatus.Failed, FailureClassification = FailureClassification.AutomationFailure,
                    CreatedAt = now.AddDays(-2).AddHours(i),
                });
            }
        }
        await db.SaveChangesAsync();
        return caseId;
    }

    private static async Task SeedOpenAppBugAsync(
        AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext db, Guid projectId, Guid caseId)
    {
        var execution = new Execution { ProjectId = projectId, Status = ExecutionStatus.Failed };
        db.Executions.Add(execution);
        await db.SaveChangesAsync();
        var test = new ExecutionTest
        {
            ExecutionId = execution.Id, TestCaseId = caseId,
            TestCaseVersionId = await LatestVersionIdAsync(db, caseId),
            Status = ExecutionTestStatus.Failed, FailureClassification = FailureClassification.ApplicationDefect,
        };
        db.ExecutionTests.Add(test);
        await db.SaveChangesAsync();
        db.Defects.Add(new Defect
        {
            ProjectId = projectId, ExecutionTestId = test.Id, Title = "Real app bug",
            Severity = Severity.High, Status = DefectStatus.Open,
            RootCauseType = FailureClassification.ApplicationDefect,
        });
        await db.SaveChangesAsync();
    }

    private static Task<Guid> LatestVersionIdAsync(
        AutoTestAi.Infrastructure.Persistence.AutoTestAiDbContext db, Guid caseId)
        => db.TestCaseVersions.Where(v => v.TestCaseId == caseId)
            .OrderByDescending(v => v.VersionNumber).Select(v => v.Id).FirstAsync();

    private HttpClient Client(string sub, string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private static JsonElement Prop(JsonDocument doc, params string[] path)
    {
        var current = doc.RootElement;
        foreach (var segment in path)
            current = current.GetProperty(segment);
        return current;
    }

    private async Task<Guid> ScanAndGetProposalAsync(HttpClient client, string testKey)
    {
        var scan = await client.PostAsync($"/api/v1/projects/{_projectA}/maintenance/scan", null);
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var list = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals?search={testKey}&pageSize=10");
        using var doc = await list.Content.ReadFromJsonAsync<JsonDocument>();
        var item = Prop(doc!, "items").EnumerateArray().Single();
        return Guid.Parse(item.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task Scan_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsync(
                $"/api/v1/projects/{_projectA}/maintenance/scan", null)).StatusCode);
    }

    [Fact]
    public async Task Scan_Outsider_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("mnt-outsider", ["tester"]).PostAsync(
                $"/api/v1/projects/{_projectA}/maintenance/scan", null)).StatusCode);
    }

    [Fact]
    public async Task Scan_Viewer_Returns403()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("mnt-viewer", ["viewer"]).PostAsync(
                $"/api/v1/projects/{_projectA}/maintenance/scan", null)).StatusCode);
    }

    [Fact]
    public async Task Scan_CreatesProposal_WithEvidence()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        var scan = await client.PostAsync($"/api/v1/projects/{_projectA}/maintenance/scan", null);
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        using var summary = await scan.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.True(Prop(summary!, "proposalsCreated").GetInt32() >= 1);

        var list = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals?search=MNT-SCAN&pageSize=10");
        using var doc = await list.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, Prop(doc!, "totalCount").GetInt32());
        var item = Prop(doc!, "items").EnumerateArray().Single();
        Assert.Equal("Proposed", item.GetProperty("status").GetString());
        Assert.Equal("css", item.GetProperty("originalStrategy").GetString());
        Assert.Equal("#login", item.GetProperty("originalValue").GetString());
        Assert.Equal("testid", item.GetProperty("proposedStrategy").GetString());
        Assert.Equal("testLoginBtn", item.GetProperty("proposedValue").GetString());
        Assert.True(item.GetProperty("confidence").GetInt32() >= 60);
        Assert.Equal(3, item.GetProperty("occurrenceCount").GetInt32());

        var detail = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{item.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailDoc = await detail.Content.ReadFromJsonAsync<JsonDocument>();
        var evidence = Prop(detailDoc!, "evidence");
        Assert.Equal(3, evidence.GetProperty("executionIds").GetArrayLength());
        Assert.Equal(3, evidence.GetProperty("healingAttemptIds").GetArrayLength());
        Assert.NotEmpty(evidence.GetProperty("confidenceFactors").EnumerateArray());
        var body = detailDoc!.RootElement.GetRawText();
        Assert.DoesNotContain("GenerationRequest", body);
    }

    [Fact]
    public async Task Scan_DuplicateSuppressed_And_AppBugExcluded()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        await client.PostAsync($"/api/v1/projects/{_projectA}/maintenance/scan", null);
        var again = await client.PostAsync($"/api/v1/projects/{_projectA}/maintenance/scan", null);
        using var summary = await again.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(0, Prop(summary!, "proposalsCreated").GetInt32());
        Assert.True(Prop(summary, "proposalsAlreadyExisting").GetInt32() >= 1);

        var bug = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals?search=MNT-BUG&pageSize=10");
        using var bugDoc = await bug.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(0, Prop(bugDoc!, "totalCount").GetInt32());
    }

    [Fact]
    public async Task List_Viewer_CanRead_And_FiltersWork()
    {
        await SeedOnceAsync();
        var viewer = Client("mnt-viewer", ["viewer"]);
        await Client("mnt-manager", ["qa-lead"]).PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/scan", null);
        var list = await viewer.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals?status=Proposed&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = await list.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.True(Prop(doc!, "totalCount").GetInt32() >= 1);
        Assert.All(Prop(doc, "items").EnumerateArray(),
            i => Assert.Equal("Proposed", i.GetProperty("status").GetString()));

        var bad = await viewer.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals?status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task List_CrossProject_Forbidden_ForMember()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("mnt-manager", ["qa-lead"]).GetAsync(
                $"/api/v1/projects/{_projectB}/maintenance/proposals")).StatusCode);
    }

    [Fact]
    public async Task Detail_WrongProject_Returns404()
    {
        await SeedOnceAsync();
        var manager = Client("mnt-manager", ["qa-lead"]);
        var proposalId = await ScanAndGetProposalAsync(manager, "MNT-SCAN");
        var admin = Client("mnt-admin", ["admin"]);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync(
                $"/api/v1/projects/{_projectB}/maintenance/proposals/{proposalId}")).StatusCode);
    }

    [Fact]
    public async Task Approve_CreatesPendingVersion_OnlyLocatorChanged()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        var proposalId = await ScanAndGetProposalAsync(client, "MNT-APPR");

        var approve = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var result = await approve.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Applied", Prop(result!, "status").GetString());
        var createdVersionId = Guid.Parse(Prop(result, "createdVersionId").GetString()!);

        using var versions = await (await client.GetAsync($"/api/v1/test-cases/{_caseApprove}/versions"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, versions!.RootElement.GetArrayLength());
        var created = versions.RootElement.EnumerateArray().First(v => v.GetProperty("id").GetString() == createdVersionId.ToString());
        Assert.Equal("Pending", created.GetProperty("reviewStatus").GetString());
        var steps = created.GetProperty("structuredSteps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        var changed = steps.Single(s => s.GetProperty("order").GetInt32() == 2);
        Assert.Equal("testid=testLoginBtn", changed.GetProperty("target").GetString());
        Assert.Equal("click", changed.GetProperty("action").GetString());
        var untouched = steps.Single(s => s.GetProperty("order").GetInt32() == 1);
        Assert.Equal("https://example.test", untouched.GetProperty("target").GetString());

        // Original Approved version unchanged.
        var original = versions.RootElement.EnumerateArray().First(v => v.GetProperty("versionNumber").GetInt32() == 1);
        Assert.Equal("Approved", original.GetProperty("reviewStatus").GetString());

        // Idempotent re-approval converges without a third version.
        var again = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var versions2 = await (await client.GetAsync($"/api/v1/test-cases/{_caseApprove}/versions"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, versions2!.RootElement.GetArrayLength());

        // Audit trail persisted with safe metadata.
        await _factory.SeedAsync(db =>
        {
            var actions = db.AuditEvents.Where(e => e.ProjectId == _projectA).Select(e => e.Action).ToList();
            Assert.Contains("maintenance.scan", actions);
            Assert.Contains("maintenance.proposed", actions);
            Assert.Contains("maintenance.approved", actions);
            Assert.Contains("maintenance.applied", actions);
            foreach (var meta in db.AuditEvents.Where(e => e.ProjectId == _projectA).Select(e => e.MetadataJson))
                Assert.DoesNotContain("testLoginBtn", meta ?? string.Empty);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Approve_PendingVersion_BlockedByExecutionGate_ThenReviewApproves()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        var proposalId = await ScanAndGetProposalAsync(client, "MNT-APPR");
        var approve = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/approve", null);
        using var result = await approve.Content.ReadFromJsonAsync<JsonDocument>();
        var createdVersionId = Prop(result!, "createdVersionId").GetString()!;

        var gated = await client.PostAsync($"/api/v1/projects/{_projectA}/executions",
            JsonContent.Create(new { testCaseVersionId = createdVersionId }));
        Assert.Equal(HttpStatusCode.Conflict, gated.StatusCode);

        var review = await client.PostAsync($"/api/v1/test-cases/{_caseApprove}/review",
            JsonContent.Create(new { versionId = createdVersionId, reviewStatus = "Approved" }));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
    }

    [Fact]
    public async Task Approve_Viewer_Forbidden()
    {
        await SeedOnceAsync();
        var manager = Client("mnt-manager", ["qa-lead"]);
        var proposalId = await ScanAndGetProposalAsync(manager, "MNT-REJ");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("mnt-viewer", ["viewer"]).PostAsync(
                $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Reject_PersistsReason_WithoutVersion()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        var proposalId = await ScanAndGetProposalAsync(client, "MNT-REJ");

        var reject = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/reject",
            JsonContent.Create(new { reason = "Locator still valid in staging." }));
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        using var doc = await reject.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Rejected", Prop(doc!, "status").GetString());
        Assert.Equal("Locator still valid in staging.", Prop(doc, "rejectionReason").GetString());

        using var versions = await (await client.GetAsync($"/api/v1/test-cases/{_caseReject}/versions"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, versions!.RootElement.GetArrayLength());

        var again = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var empty = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/reject",
            JsonContent.Create(new { reason = " " }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Approve_StaleVersion_Returns409_And_Supersedes()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        var proposalId = await ScanAndGetProposalAsync(client, "MNT-STALE");

        // Concurrent human edit changes the locator first.
        var edit = await client.PutAsJsonAsync($"/api/v1/test-cases/{_caseStale}", new
        {
            title = "Stale case",
            structuredSteps = new[]
            {
                new { order = 1, action = "navigate", target = "https://example.test", value = (string?)null },
                new { order = 2, action = "click", target = "css=#changed", value = (string?)null },
            },
            hasStructuredSteps = true,
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var approve = await client.PostAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);

        var detail = await client.GetAsync(
            $"/api/v1/projects/{_projectA}/maintenance/proposals/{proposalId}");
        using var doc = await detail.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Superseded", Prop(doc!, "proposal", "status").GetString());

        using var versions = await (await client.GetAsync($"/api/v1/test-cases/{_caseStale}/versions"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, versions!.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task PendingGate_ExecutionBlocked_ForManuallyCreatedVersion()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        var edit = await client.PutAsJsonAsync($"/api/v1/test-cases/{_caseGate}", new
        {
            title = "Gate case",
            structuredSteps = new[]
            {
                new { order = 1, action = "navigate", target = "https://example.test", value = (string?)null },
                new { order = 2, action = "click", target = "css=#gate", value = (string?)null },
            },
            hasStructuredSteps = true,
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        string? versionId = null;
        using (var edited = await edit.Content.ReadFromJsonAsync<JsonDocument>())
        {
            if (edited!.RootElement.TryGetProperty("latestVersionId", out var latest))
                versionId = latest.GetString();
        }
        // Fall back to versions list when the details payload shape differs.
        if (string.IsNullOrEmpty(versionId))
        {
            using var list = await (await client.GetAsync($"/api/v1/test-cases/{_caseGate}/versions"))
                .Content.ReadFromJsonAsync<JsonDocument>();
            versionId = list!.RootElement.EnumerateArray().First().GetProperty("id").GetString();
        }
        var gated = await client.PostAsync($"/api/v1/projects/{_projectA}/executions",
            JsonContent.Create(new { testCaseVersionId = versionId }));
        Assert.Equal(HttpStatusCode.Conflict, gated.StatusCode);
    }

    [Fact]
    public async Task ReviewFlow_Approves_MaintenanceCreatedVersion()
    {
        await SeedOnceAsync();
        var client = Client("mnt-manager", ["qa-lead"]);
        var edit = await client.PutAsJsonAsync($"/api/v1/test-cases/{_caseReview}", new
        {
            title = "Review case",
            structuredSteps = new[]
            {
                new { order = 1, action = "navigate", target = "https://example.test", value = (string?)null },
                new { order = 2, action = "click", target = "testid=testReviewBtn", value = (string?)null },
            },
            hasStructuredSteps = true,
        });
        using var versions = await (await client.GetAsync($"/api/v1/test-cases/{_caseReview}/versions"))
            .Content.ReadFromJsonAsync<JsonDocument>();
        var pendingId = versions!.RootElement.EnumerateArray()
            .First(v => v.GetProperty("reviewStatus").GetString() == "Pending")
            .GetProperty("id").GetString();
        var review = await client.PostAsync($"/api/v1/test-cases/{_caseReview}/review",
            JsonContent.Create(new { versionId = pendingId, reviewStatus = "Approved" }));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using var reviewed = await review.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Approved", Prop(reviewed!, "reviewStatus").GetString());
    }
}
