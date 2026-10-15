using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>
/// Phase 4 Slice 11: authentication/authorization event auditing —
/// decorator emission, bounded in-memory dedup, failure containment, and
/// attribution safety. The inner authorization contract is never altered.
/// </summary>
public sealed class AuthEventAuditTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    private sealed class MutableClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ThrowingAudit : IAuditService
    {
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
            => throw new InvalidOperationException("audit store down");
    }

    private sealed class Harness
    {
        public readonly FakeProjectStore Projects = new();
        public readonly MutableClock Clock = new();
        public readonly AuthorizationDenialDedup Dedup;
        public readonly StubMembershipStore Memberships = new();
        public readonly FakeUserDirectory Directory = new();
        public AuditingAuthorizationService Service = null!;
        public AuditService Audits = null!;

        public Harness(StubCurrentUser user, IAuditService? audit = null, TimeSpan? window = null, int maxEntries = 4096)
        {
            Projects.Projects.Add(new Project { Id = ProjectA, Name = "Alpha", Key = "A" });
            Dedup = new AuthorizationDenialDedup(Clock, window ?? AuthorizationDenialDedup.DefaultWindow, maxEntries);
            var authorization = new AuthorizationService(user, Memberships);
            Audits = new AuditService(Projects, user, Directory, NullLogger<AuditService>.Instance);
            Service = new AuditingAuthorizationService(
                authorization, audit ?? Audits, Projects, user, Dedup);
        }
    }

    private static StubCurrentUser Viewer(string sub = "viewer-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["viewer"],
        Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private static StubCurrentUser Manager(string sub = "user-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["qa-lead"],
        Permissions = RolePermissions.Resolve(["qa-lead"]),
    };

    private static void AddMembership(Harness h, string sub, Guid projectId, Guid roleId)
    {
        var user = new User { ExternalIdentityId = sub, Email = "e@x", DisplayName = "U" };
        h.Projects.Users.Add(user);
        h.Projects.Members.Add(new ProjectMember { ProjectId = projectId, UserId = user.Id, RoleId = roleId });
        h.Memberships.Add(sub, projectId);
        h.Directory.Add(sub, user.Id);
    }

    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ---------- decorator emission ----------

    [Fact]
    public async Task PermissionDenial_RecordsEvent_WithPermissionReason()
    {
        var h = new Harness(Viewer());
        AddMembership(h, "viewer-1", ProjectA, ViewerRoleId);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
            ProjectA, Permissions.TestCasesManage, CancellationToken.None));

        Assert.Contains("permission", ex.Message, StringComparison.OrdinalIgnoreCase);
        var recorded = Assert.Single(h.Projects.Audits);
        Assert.Equal("authz.project_denied", recorded.Action);
        Assert.Equal("project", recorded.EntityType);
        Assert.Equal(ProjectA.ToString(), recorded.EntityId);
        Assert.Equal(ProjectA, recorded.ProjectId);
        Assert.NotNull(recorded.ActorUserId);
        Assert.Contains("permission", recorded.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("testcases.manage", recorded.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MembershipDenial_RecordsEvent_WithMembershipReason()
    {
        var h = new Harness(Manager());
        // qa-lead holds testcases.manage but belongs to no project here.
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
            ProjectA, Permissions.TestCasesManage, CancellationToken.None));

        var recorded = Assert.Single(h.Projects.Audits);
        Assert.Equal("authz.project_denied", recorded.Action);
        Assert.Contains("membership", recorded.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullPermissionDenial_ClassifiedAsMembership_WithoutCodes()
    {
        var h = new Harness(Manager());
        // requiredPermission is contract-optional (string?); with null the
        // only possible denial is membership-based.
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
            ProjectA, null, CancellationToken.None));

        var recorded = Assert.Single(h.Projects.Audits);
        Assert.Equal("authz.project_denied", recorded.Action);
        Assert.Contains("membership", recorded.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousChallenge_RecordsNothing()
    {
        var h = new Harness(new StubCurrentUser { IsAuthenticated = false });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Service.RequireProjectAccessAsync(
            ProjectA, Permissions.TestCasesManage, CancellationToken.None));
        Assert.Empty(h.Projects.Audits);
    }

    [Fact]
    public async Task UnknownProject_Skipped_NoRow_ButStillForbidden()
    {
        var h = new Harness(Manager());
        var unknown = Guid.NewGuid();
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
            unknown, Permissions.TestCasesManage, CancellationToken.None));
        Assert.Empty(h.Projects.Audits);
    }

    [Fact]
    public async Task AllowedAccess_RecordsNothing()
    {
        var h = new Harness(Manager());
        AddMembership(h, "user-1", ProjectA, Guid.Parse("22222222-2222-2222-2222-222222222222"));
        await h.Service.RequireProjectAccessAsync(ProjectA, Permissions.TestCasesManage, CancellationToken.None);
        Assert.Empty(h.Projects.Audits);
    }

    [Fact]
    public async Task Dedup_CollapsesRepeats_AndReopensAfterWindow()
    {
        var h = new Harness(Viewer(), window: TimeSpan.FromMinutes(5));
        AddMembership(h, "viewer-1", ProjectA, ViewerRoleId);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
                ProjectA, Permissions.TestCasesManage, CancellationToken.None));
        Assert.Single(h.Projects.Audits);

        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(6);
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
            ProjectA, Permissions.TestCasesManage, CancellationToken.None));
        Assert.Equal(2, h.Projects.Audits.Count);
    }

    [Fact]
    public async Task AuditFailure_PreservesDenialContract()
    {
        var h = new Harness(Viewer(), audit: new ThrowingAudit());
        AddMembership(h, "viewer-1", ProjectA, ViewerRoleId);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
            ProjectA, Permissions.TestCasesManage, CancellationToken.None));
        Assert.Contains("permission", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passthrough_Methods_Unchanged()
    {
        var h = new Harness(Viewer());
        AddMembership(h, "viewer-1", ProjectA, ViewerRoleId);

        Assert.True(h.Service.HasPermission(Permissions.TestCasesRead));
        Assert.False(h.Service.HasPermission(Permissions.TestCasesManage));
        Assert.False(h.Service.IsAdmin());
        Assert.True(await h.Service.CanAccessProjectAsync(ProjectA, CancellationToken.None));
        Assert.Empty(h.Projects.Audits);
    }

    [Fact]
    public async Task Metadata_ContainsNoSensitiveMaterial()
    {
        var h = new Harness(Viewer());
        AddMembership(h, "viewer-1", ProjectA, ViewerRoleId);

        // Drive one denial to inspect the stored row.
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.RequireProjectAccessAsync(
            ProjectA, Permissions.TestCasesManage, CancellationToken.None));
        var recorded = Assert.Single(h.Projects.Audits);
        var meta = recorded.MetadataJson ?? string.Empty;
        Assert.DoesNotContain("Bearer", meta, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("viewer-1", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("http", meta, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- dedup gate ----------

    [Fact]
    public void Dedup_FirstSeen_Records()
    {
        var clock = new MutableClock();
        var gate = new AuthorizationDenialDedup(clock);
        Assert.True(gate.ShouldRecord(ProjectA, "actor-1", "membership"));
    }

    [Fact]
    public void Dedup_CapacityBound_SuppressesNewKeys()
    {
        var clock = new MutableClock();
        var gate = new AuthorizationDenialDedup(clock, TimeSpan.FromMinutes(5), maxEntries: 3);
        Assert.True(gate.ShouldRecord(ProjectA, "a", "membership"));
        Assert.True(gate.ShouldRecord(ProjectA, "b", "membership"));
        Assert.True(gate.ShouldRecord(ProjectA, "c", "membership"));
        Assert.False(gate.ShouldRecord(ProjectA, "d", "membership"));
        // Repeats of admitted keys still collapse, not grow.
        Assert.False(gate.ShouldRecord(ProjectA, "a", "membership"));
    }

    [Fact]
    public void Dedup_ExpiryEvicts_UnderPressure()
    {
        var clock = new MutableClock();
        var gate = new AuthorizationDenialDedup(clock, TimeSpan.FromMinutes(5), maxEntries: 2);
        Assert.True(gate.ShouldRecord(ProjectA, "a", "membership"));
        Assert.True(gate.ShouldRecord(ProjectA, "b", "membership"));
        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        Assert.True(gate.ShouldRecord(ProjectA, "c", "membership"));
    }

    [Fact]
    public void Dedup_ConcurrentUse_RespectsBound()
    {
        var clock = new MutableClock();
        const int cap = 64;
        var gate = new AuthorizationDenialDedup(clock, TimeSpan.FromMinutes(5), cap);
        var admitted = 0;
        Parallel.For(0, 500, i =>
        {
            if (gate.ShouldRecord(ProjectA, $"actor-{i}", "membership"))
                Interlocked.Increment(ref admitted);
        });
        Assert.True(admitted <= cap, $"admitted {admitted} exceeds cap {cap}");
        Assert.True(admitted > 0);
    }
}
