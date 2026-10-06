using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTestAi.UnitTests;

/// <summary>Phase 4 Slice 6: read-side Jira traceability — defensive
/// provenance projection, exact normalized matching, and filter validation.
/// No Jira calls, no AI, no persistence behavior under test.</summary>
public sealed class JiraTraceabilityTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    private static JsonDocument JiraProvenance(
        string key = "PROJ-123",
        string? type = "Story",
        string? host = "company.atlassian.net",
        string? fetchedAt = "2026-10-06T00:00:00Z",
        string? extra = null) => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            storyTitle = "Guest checkout",
            storyDescription = "Flow.",
            acceptanceCriteria = new[] { "Pay" },
            focusCriterionIndex = 0,
            promptVersion = "story-to-tests-v1",
            source = "story-ai",
            origin = "jira-import",
            jiraIssueKey = key,
            jiraIssueType = type,
            jiraBaseUrlHost = host,
            jiraFetchedAt = fetchedAt,
            extraField = extra,
        }));

    private static JsonDocument GenericProvenance() => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        title = "Login",
        promptVersion = "test-generation-v1",
        source = "ai",
    }));

    // ---------- projection ----------

    [Fact]
    public void AbsentProvenance_ReturnsNull()
        => Assert.Null(JiraProvenanceReader.TryRead(null));

    [Fact]
    public void NonJiraProvenance_ReturnsNull()
        => Assert.Null(JiraProvenanceReader.TryRead(GenericProvenance()));

    [Fact]
    public void ValidJiraProvenance_ReturnsShapedDto()
    {
        var dto = JiraProvenanceReader.TryRead(JiraProvenance());
        Assert.NotNull(dto);
        Assert.Equal("jira-import", dto!.Origin);
        Assert.Equal("PROJ-123", dto.JiraIssueKey);
        Assert.Equal("Story", dto.JiraIssueType);
        Assert.Equal("company.atlassian.net", dto.JiraBaseUrlHost);
        Assert.Equal("2026-10-06T00:00:00Z", dto.JiraFetchedAt);
    }

    [Theory]
    [InlineData("""{"origin":"jira-import"}""")]
    [InlineData("""{"origin":"jira-import","jiraIssueKey":"nope"}""")]
    [InlineData("""{"origin":"jira-import","jiraIssueKey":""}""")]
    [InlineData("""{"origin":"other","jiraIssueKey":"PROJ-123"}""")]
    [InlineData("""{"jiraIssueKey":"PROJ-123"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"origin":42}""")]
    [InlineData("""{"origin":"jira-import","jiraIssueKey":42}""")]
    public void MalformedOrKeylessProvenance_ReturnsNull(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(JiraProvenanceReader.TryRead(document));
    }

    [Fact]
    public void ExtraFields_AreNotExposed()
    {
        var dto = JiraProvenanceReader.TryRead(JiraProvenance(extra: "ATTACKER-DATA"));
        Assert.NotNull(dto);
        Assert.Equal("PROJ-123", dto!.JiraIssueKey);
        Assert.DoesNotContain("ATTACKER-DATA",
            JsonSerializer.Serialize(dto), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("proj-123", "PROJ-123")]
    [InlineData("  PROJ-123  ", "PROJ-123")]
    [InlineData("Proj-123", "PROJ-123")]
    public void StoredKey_IsNormalized(string stored, string expected)
    {
        var dto = JiraProvenanceReader.TryRead(JiraProvenance(key: stored));
        Assert.NotNull(dto);
        Assert.Equal(expected, dto!.JiraIssueKey);
    }

    [Theory]
    [InlineData("PROJ-123", "PROJ-123", true)]
    [InlineData("PROJ-123", "PROJ-1234", false)]
    [InlineData("PROJ-123", "PROJ-12", false)]
    [InlineData("PROJ-123", "OTHER-123", false)]
    public void Matching_IsExact_NotSubstring(string stored, string filter, bool expected)
        => Assert.Equal(expected, JiraProvenanceReader.Matches(JiraProvenance(key: stored), filter));

    // ---------- filter validation (service level) ----------

    private static TestCaseService CreateService(FakeTestCaseStore store)
    {
        var user = new StubCurrentUser
        {
            IsAuthenticated = true,
            ExternalIdentityId = "user-1",
            Roles = ["tester"],
            Permissions = RolePermissions.Resolve(["tester"]),
        };
        var memberships = new StubMembershipStore();
        memberships.Add("user-1", ProjectA);
        var directory = new FakeUserDirectory();
        var authorization = new AuthorizationService(user, memberships);
        return new TestCaseService(
            store, user, authorization, directory,
            new SystemDateTimeProvider(),
            new AuditService(
                new StubAuditProjectStore(), user, directory,
                NullLogger<AuditService>.Instance));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("PROJ123")]
    [InlineData("PROJ-")]
    [InlineData("https://jira.test/browse/PROJ-123")]
    public async Task InvalidFilterKey_ReturnsValidationError(string key)
    {
        var service = CreateService(new FakeTestCaseStore());
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            service.ListAsync(ProjectA, 1, 25,
                new TestCaseFilters(null, null, null, null, null, null, key), CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "jiraIssueKey");
    }

    [Theory]
    [InlineData(" proj-123 ", "PROJ-123")]
    [InlineData("PROJ-123", "PROJ-123")]
    public async Task ValidFilterKey_IsNormalized_BeforeQuery(string input, string expected)
    {
        var store = new FakeTestCaseStore();
        var testCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "JI-001", Title = "Jira test",
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "ai",
        };
        store.Cases.Add(testCase);
        store.Versions.Add(new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 1,
            ReviewStatus = ReviewStatus.Approved, GenerationRequest = JiraProvenance(),
        });
        var service = CreateService(store);

        var page = await service.ListAsync(ProjectA, 1, 25,
            new TestCaseFilters(null, null, null, null, null, null, input), CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal("JI-001", page.Items[0].TestKey);
    }

    [Fact]
    public async Task HistoricalOnlyVersion_StillMatchesFilter()
    {
        var store = new FakeTestCaseStore();
        var testCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "JI-HIST", Title = "Historical",
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "ai",
        };
        store.Cases.Add(testCase);
        store.Versions.Add(new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 1,
            ReviewStatus = ReviewStatus.Approved, GenerationRequest = JiraProvenance(),
        });
        store.Versions.Add(new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 2,
            ReviewStatus = ReviewStatus.Pending, GenerationRequest = null,
        });
        var service = CreateService(store);

        var page = await service.ListAsync(ProjectA, 1, 25,
            new TestCaseFilters(null, null, null, null, null, null, "PROJ-123"), CancellationToken.None);

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task VersionDto_CarriesProvenance_WhenJira()
    {
        var store = new FakeTestCaseStore();
        var testCase = new TestCase
        {
            ProjectId = ProjectA, TestKey = "JI-002", Title = "Jira test",
            Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "ai",
        };
        store.Cases.Add(testCase);
        var version = new TestCaseVersion
        {
            TestCaseId = testCase.Id, VersionNumber = 1,
            ReviewStatus = ReviewStatus.Approved, GenerationRequest = JiraProvenance(),
        };
        store.Versions.Add(version);
        var directory = new FakeUserDirectory();
        var user = new StubCurrentUser
        {
            IsAuthenticated = true,
            ExternalIdentityId = "user-1",
            Roles = ["viewer"],
            Permissions = RolePermissions.Resolve(["viewer"]),
        };
        var memberships = new StubMembershipStore();
        memberships.Add("user-1", ProjectA);
        var service = new TestCaseService(
            store, user, new AuthorizationService(user, memberships), directory,
            new SystemDateTimeProvider(),
            new AuditService(
                new StubAuditProjectStore(), user, directory,
                NullLogger<AuditService>.Instance));

        var versions = await service.ListVersionsAsync(testCase.Id, CancellationToken.None);

        var dto = Assert.Single(versions);
        Assert.NotNull(dto.JiraProvenance);
        Assert.Equal("PROJ-123", dto.JiraProvenance!.JiraIssueKey);
    }
}
