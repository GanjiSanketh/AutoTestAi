using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Sla;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>In-memory ISlaTargetStore fake with project-uniqueness enforcement.</summary>
internal sealed class FakeSlaTargetStore : ISlaTargetStore
{
    public readonly List<SlaTargetPolicy> Rows = new();

    public Task<SlaTargetPolicy?> GetByProjectAsync(Guid projectId, CancellationToken ct)
        => Task.FromResult(Rows.FirstOrDefault(r => r.ProjectId == projectId));

    public Task AddAsync(SlaTargetPolicy row, CancellationToken ct)
    {
        if (Rows.Any(r => r.ProjectId == row.ProjectId))
            throw new InvalidOperationException("Duplicate SLA targets for project.");
        Rows.Add(row);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Phase 4 Slice 13: SLA target validation/upsert, current-breach math,
/// authorization matrix, and audit behavior. Boundary timing (strictly
/// greater-than) is proven against the real store in integration tests.
/// </summary>
public sealed class SlaTargetServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<string> Actions = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Harness
    {
        public readonly FakeSlaTargetStore Targets = new();
        public readonly FakeReportQueryStore Reports = new();
        public readonly FakeAudit Audit = new();
        public readonly SlaTargetService Service;

        public Harness(StubCurrentUser user, bool member = true)
        {
            var memberships = new StubMembershipStore();
            if (member && user.ExternalIdentityId is not null)
                memberships.Add(user.ExternalIdentityId, ProjectA);
            var authorization = new AuthorizationService(user, memberships);
            var directory = new FakeUserDirectory();
            Service = new SlaTargetService(
                Targets, Reports, authorization, user, directory, new FixedClock(),
                Audit);
        }
    }

    private static StubCurrentUser Admin(string sub = "admin-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["admin"],
        Permissions = RolePermissions.Resolve(["admin"]),
    };

    private static StubCurrentUser Viewer(string sub = "viewer-1") => new()
    {
        IsAuthenticated = true,
        ExternalIdentityId = sub,
        Roles = ["viewer"],
        Permissions = RolePermissions.Resolve(["viewer"]),
    };

    private static UpsertSlaTargetsCommand Valid() => new(ProjectA, 1, 7, 14, 30);

    // ---------- validation / upsert ----------

    [Fact]
    public async Task Upsert_Creates_WithConfiguredAudit()
    {
        var h = new Harness(Admin());
        var dto = await h.Service.UpsertAsync(Valid(), CancellationToken.None);

        Assert.Equal(ProjectA, dto.ProjectId);
        Assert.Equal((1, 7, 14, 30), (dto.CriticalDays, dto.HighDays, dto.MediumDays, dto.LowDays));
        Assert.Single(h.Targets.Rows);
        Assert.Contains(h.Audit.Actions, a => a == "sla.target_configured");
    }

    [Fact]
    public async Task Upsert_UpdatesSameRow_Idempotently()
    {
        var h = new Harness(Admin());
        await h.Service.UpsertAsync(Valid(), CancellationToken.None);
        var updated = await h.Service.UpsertAsync(
            new UpsertSlaTargetsCommand(ProjectA, 2, 7, 14, 30), CancellationToken.None);

        Assert.Single(h.Targets.Rows);
        Assert.Equal(2, updated.CriticalDays);
        Assert.Contains(h.Audit.Actions, a => a == "sla.target_updated");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(366)]
    public async Task Upsert_RejectsOutOfRangeThresholds(int days)
    {
        var h = new Harness(Admin());
        var ex = await Assert.ThrowsAsync<ValidationException>(() => h.Service.UpsertAsync(
            new UpsertSlaTargetsCommand(ProjectA, days, 7, 14, 30), CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "criticalDays");
        Assert.Empty(h.Targets.Rows);
        Assert.Empty(h.Audit.Actions);
    }

    [Fact]
    public async Task Upsert_RejectsMissingThresholds_WithoutPersisting()
    {
        var h = new Harness(Admin());
        await h.Service.UpsertAsync(Valid(), CancellationToken.None);
        await Assert.ThrowsAsync<ValidationException>(() => h.Service.UpsertAsync(
            new UpsertSlaTargetsCommand(ProjectA, 2, null, 14, 30), CancellationToken.None));

        Assert.Single(h.Targets.Rows);
        Assert.Equal(1, h.Targets.Rows[0].CriticalDays);
        Assert.DoesNotContain(h.Audit.Actions, a => a == "sla.target_updated");
    }

    [Fact]
    public async Task Upsert_AcceptsBoundaryValues_1_And_365()
    {
        var h = new Harness(Admin());
        var dto = await h.Service.UpsertAsync(
            new UpsertSlaTargetsCommand(ProjectA, 1, 365, 365, 1), CancellationToken.None);
        Assert.Equal((1, 365, 365, 1), (dto.CriticalDays, dto.HighDays, dto.MediumDays, dto.LowDays));
    }

    // ---------- reads ----------

    [Fact]
    public async Task Get_Unconfigured_ReturnsNulls()
    {
        var h = new Harness(Viewer());
        var response = await h.Service.GetAsync(ProjectA, CancellationToken.None);
        Assert.Null(response.Targets);
        Assert.Null(response.Compliance);
    }

    [Fact]
    public async Task Get_EvaluatesCompliance_WithDenominator()
    {
        var h = new Harness(Viewer());
        var admin = new Harness(Admin());
        await admin.Service.UpsertAsync(Valid(), CancellationToken.None);
        // Seed through the shared store: the viewer harness reads the admin's row.
        h.Targets.Rows.AddRange(admin.Targets.Rows);
        h.Reports.SlaSlices.AddRange(new[]
        {
            new SlaSeverityCount("Critical", 1, Eligible: 2, Breached: 1),
            new SlaSeverityCount("High", 7, Eligible: 1, Breached: 0),
            new SlaSeverityCount("Medium", 14, Eligible: 0, Breached: 0),
            new SlaSeverityCount("Low", 30, Eligible: 1, Breached: 1),
        });

        var response = await h.Service.GetAsync(ProjectA, CancellationToken.None);

        Assert.NotNull(response.Targets);
        Assert.NotNull(response.Compliance);
        var compliance = response.Compliance!;
        Assert.Equal(4, compliance.Eligible);
        Assert.Equal(2, compliance.Breached);
        Assert.Equal(2, compliance.Compliant);
        Assert.Equal(50.0, compliance.CompliancePercent);
        Assert.Equal(0, compliance.UnconfiguredSeverityCount);
        Assert.Equal(Now, compliance.EvaluatedAt);
        Assert.Equal(4, compliance.BySeverity.Count);
    }

    [Fact]
    public async Task Get_MissingSeveritySlice_ExcludedFromDenominator()
    {
        var h = new Harness(Viewer());
        var admin = new Harness(Admin());
        await admin.Service.UpsertAsync(Valid(), CancellationToken.None);
        h.Targets.Rows.AddRange(admin.Targets.Rows);
        h.Reports.SlaSlices.Add(new SlaSeverityCount("Critical", 1, Eligible: 1, Breached: 1));

        var fetched = await h.Service.GetAsync(ProjectA, CancellationToken.None);
        Assert.NotNull(fetched.Compliance);
        var compliance = fetched.Compliance!;

        Assert.Equal(1, compliance.Eligible);
        Assert.Equal(0.0, compliance.CompliancePercent);
        Assert.Equal(3, compliance.UnconfiguredSeverityCount);
        Assert.Single(compliance.BySeverity);
    }

    [Fact]
    public async Task Get_ZeroEligible_ReturnsNullPercent()
    {
        var h = new Harness(Viewer());
        var admin = new Harness(Admin());
        await admin.Service.UpsertAsync(Valid(), CancellationToken.None);
        h.Targets.Rows.AddRange(admin.Targets.Rows);

        var fetched = await h.Service.GetAsync(ProjectA, CancellationToken.None);
        Assert.NotNull(fetched.Compliance);
        var compliance = fetched.Compliance!;

        Assert.Equal(0, compliance.Eligible);
        Assert.Null(compliance.CompliancePercent);
    }

    // ---------- authorization ----------

    [Fact]
    public async Task Upsert_ViewerForbidden_ReadAllowed()
    {
        var h = new Harness(Viewer());
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.UpsertAsync(Valid(), CancellationToken.None));
        Assert.Empty(h.Targets.Rows);

        var response = await h.Service.GetAsync(ProjectA, CancellationToken.None);
        Assert.Null(response.Targets);
    }

    [Fact]
    public async Task Outsider_Denied()
    {
        var h = new Harness(Viewer(), member: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.GetAsync(ProjectA, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Service.UpsertAsync(Valid(), CancellationToken.None));
    }
}
