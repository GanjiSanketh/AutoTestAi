using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Phase 4 Slice 7: freshness comparison — deterministic field
/// equality, ignored metadata, normalizer-version stamp behavior, and
/// defensive handling. No Jira calls, no AI calls, no persistence.</summary>
public sealed class JiraChangeCheckTests
{
    private static readonly Guid ProjectA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static JiraStorySnapshot Snapshot(
        string title = "Guest checkout",
        string? description = "Allow guests to check out.",
        string[]? criteria = null,
        string type = "Story") => new(
            title, description, criteria ?? new[] { "Guest can place an order", "Receipt shown" }, type);

    // ---------- comparison ----------

    [Fact]
    public void IdenticalSnapshots_AreCurrent()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(Snapshot(), Snapshot());
        Assert.Equal(JiraStorySnapshotComparer.StatusCurrent, status);
        Assert.Empty(changed);
    }

    [Fact]
    public void TitleChanged_ReportsTitle()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(Snapshot(), Snapshot(title: "Express checkout"));
        Assert.Equal(JiraStorySnapshotComparer.StatusChanged, status);
        Assert.Equal(new[] { "title" }, changed);
    }

    [Fact]
    public void DescriptionChanged_ReportsDescription()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(Snapshot(), Snapshot(description: "Members only."));
        Assert.Equal(JiraStorySnapshotComparer.StatusChanged, status);
        Assert.Equal(new[] { "description" }, changed);
    }

    [Fact]
    public void CriteriaChanged_ReportsAcceptanceCriteria()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(
            Snapshot(), Snapshot(criteria: new[] { "Guest can place an order", "Gift wrap offered" }));
        Assert.Equal(JiraStorySnapshotComparer.StatusChanged, status);
        Assert.Equal(new[] { "acceptanceCriteria" }, changed);
    }

    [Fact]
    public void CriteriaReordered_ReportsAcceptanceCriteria()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(
            Snapshot(criteria: new[] { "A", "B" }), Snapshot(criteria: new[] { "B", "A" }));
        Assert.Equal(JiraStorySnapshotComparer.StatusChanged, status);
        Assert.Equal(new[] { "acceptanceCriteria" }, changed);
    }

    [Fact]
    public void IssueTypeChanged_ReportsIssueType()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(Snapshot(), Snapshot(type: "Task"));
        Assert.Equal(JiraStorySnapshotComparer.StatusChanged, status);
        Assert.Equal(new[] { "issueType" }, changed);
    }

    [Fact]
    public void MultipleChanges_ReportedInDeterministicOrder()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(
            Snapshot(),
            Snapshot(title: "X", description: "Y", criteria: new[] { "Z" }, type: "Bug"));
        Assert.Equal(JiraStorySnapshotComparer.StatusChanged, status);
        Assert.Equal(new[] { "title", "description", "acceptanceCriteria", "issueType" }, changed);
    }

    [Fact]
    public void ChangedFieldOrdering_IsDeterministic()
    {
        var first = JiraStorySnapshotComparer.Compare(
            Snapshot(), Snapshot(type: "Bug", title: "X")).ChangedFields;
        var second = JiraStorySnapshotComparer.Compare(
            Snapshot(), Snapshot(title: "X", type: "Bug")).ChangedFields;
        Assert.Equal(first, second);
        Assert.Equal(new[] { "title", "issueType" }, first);
    }

    [Fact]
    public void EmptyArrays_CompareCorrectly()
    {
        var (status, changed) = JiraStorySnapshotComparer.Compare(
            Snapshot(criteria: Array.Empty<string>()), Snapshot(criteria: Array.Empty<string>()));
        Assert.Equal(JiraStorySnapshotComparer.StatusCurrent, status);
        Assert.Empty(changed);
    }

    [Fact]
    public void NullDescription_MatchesEmptyDescription()
    {
        var (status, _) = JiraStorySnapshotComparer.Compare(
            Snapshot(description: null), Snapshot(description: string.Empty));
        Assert.Equal(JiraStorySnapshotComparer.StatusCurrent, status);
    }

    [Fact]
    public void ComparisonResult_ContainsNoContent()
    {
        var (_, changed) = JiraStorySnapshotComparer.Compare(Snapshot(), Snapshot(title: "New title here"));
        var serialized = JsonSerializer.Serialize(changed);
        Assert.DoesNotContain("New title here", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Guest checkout", serialized, StringComparison.Ordinal);
    }

    // ---------- stored-snapshot gate ----------

    private static JsonDocument StoredProvenance(
        string? origin = "jira-import",
        string? key = "PROJ-123",
        string? title = "Guest checkout",
        string? description = "Allow guests to check out.",
        string[]? criteria = null,
        string? type = "Story") => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            storyTitle = title,
            storyDescription = description,
            acceptanceCriteria = criteria ?? new[] { "Guest can place an order" },
            promptVersion = "story-to-tests-v1",
            source = "story-ai",
            origin,
            jiraIssueKey = key,
            jiraIssueType = type,
            jiraBaseUrlHost = "company.atlassian.net",
            jiraFetchedAt = "2026-10-06T00:00:00Z",
        }));

    [Fact]
    public void StoredSnapshot_BuildsFromValidProvenance()
    {
        var snapshot = JiraStorySnapshotComparer.TryReadStored(StoredProvenance());
        Assert.NotNull(snapshot);
        Assert.Equal("Guest checkout", snapshot!.Title);
        Assert.Equal("Story", snapshot.IssueType);
    }

    [Theory]
    [InlineData("other")]
    [InlineData(null)]
    public void StoredSnapshot_RejectsNonJiraOrigin(string? origin)
        => Assert.Null(JiraStorySnapshotComparer.TryReadStored(StoredProvenance(origin: origin)));

    [Theory]
    [InlineData("nope")]
    [InlineData(null)]
    public void StoredSnapshot_RejectsMissingOrInvalidKey(string? key)
        => Assert.Null(JiraStorySnapshotComparer.TryReadStored(StoredProvenance(key: key)));

    [Fact]
    public void StoredSnapshot_MatchesFreshNormalizedCounterpart()
    {
        // Fresh normalization joins the paragraph block and the list block;
        // the stored baseline must carry that exact normalized shape.
        var stored = JiraStorySnapshotComparer.TryReadStored(StoredProvenance(
            title: "Guest checkout",
            description: "Allow guests to check out.\n\nGuest can place an order",
            criteria: new[] { "Guest can place an order" },
            type: "Story"));
        var fresh = JiraStorySnapshotComparer.FromNormalized(
            JiraStoryNormalizer.Normalize(
                "Guest checkout",
                "{\"version\":1,\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Allow guests to check out.\"}]},{\"type\":\"bulletList\",\"content\":[{\"type\":\"listItem\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Guest can place an order\"}]}]}]}]}",
                "Story"));
        var (status, changed) = JiraStorySnapshotComparer.Compare(stored!, fresh);
        Assert.Equal(JiraStorySnapshotComparer.StatusCurrent, status);
        Assert.Empty(changed);
    }

    // ---------- normalizer version stamp ----------

    private sealed class FakeProvider : IAiProvider
    {
        public string Name => "stub";
        public Task<AiGenerationResult> GenerateTestAsync(AiGenerationRequest request, CancellationToken ct)
            => Task.FromResult(new AiGenerationResult(
                Provider: "stub", Model: "stub-1.0", LatencyMs: 5,
                Steps: new[] { new AiGeneratedStep("1", "navigate", "Page loads") },
                SourceCode: "import { test } from '@playwright/test';\ntest('x', async () => {});",
                Title: "Proposal", Framework: "playwright", Platform: "web",
                StructuredSteps: new[] { new AiStructuredStep(1, "navigate", "https://example.test", null) },
                Assumptions: Array.Empty<string>(), Warnings: Array.Empty<string>(),
                PromptVersion: AiPromptVersions.StoryToTestsV1));
        public Task<AiAnalysisResult> AnalyzeFailureAsync(AiFailureAnalysisRequest request, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AiHealingResult> SuggestHealingCandidatesAsync(AiHealingRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        public IAiProvider Provider { get; set; } = null!;
        public IAiProvider Resolve() => Provider;
        public AiProviderStatus GetStatus() => new("stub", "stub-1.0", true, null, AiPromptVersions.StoryToTestsV1);
    }

    private sealed class FakeAuthorization : IAuthorizationService
    {
        public bool HasPermission(string permission) => true;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(true);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeAudit : IAuditService
    {
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private static StoryTestGenerationService CreateGenerationService() => new(
        new FakeResolver { Provider = new FakeProvider() },
        new AiGenerationValidator(),
        new AiGenerationRateLimiter(
            Options.Create(new AiOptions { MaxGenerationsPerMinutePerProject = 1000 }),
            new FixedClock(DateTimeOffset.UtcNow)),
        new FakeAuthorization(),
        new FakeAudit());

    [Fact]
    public async Task NewJiraProvenance_ContainsNormalizerVersion()
    {
        var service = CreateGenerationService();
        var result = await service.GenerateStoryProposalsAsync(new GenerateStoryTestsCommand(
            ProjectA, "Guest checkout", "Flow.", new[] { "Pay" },
            null, "playwright", "web", null, null, null, 1,
            new JiraImportMetadata("PROJ-123", "Story", "company.atlassian.net", "2026-10-06T00:00:00Z",
                JiraStoryNormalizer.NormalizerVersion)),
            CancellationToken.None);
        var raw = result.Proposals.Single(p => p.Status == "Succeeded").Provenance!.Value.GetRawText();
        Assert.Contains("normalizerVersion", raw, StringComparison.Ordinal);
        Assert.Contains(JiraStoryNormalizer.NormalizerVersion, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualGeneration_DoesNotGainNormalizerMetadata()
    {
        var service = CreateGenerationService();
        var result = await service.GenerateStoryProposalsAsync(new GenerateStoryTestsCommand(
            ProjectA, "Guest checkout", "Flow.", new[] { "Pay" },
            null, "playwright", "web", null, null, null, 1),
            CancellationToken.None);
        var raw = result.Proposals.Single(p => p.Status == "Succeeded").Provenance!.Value.GetRawText();
        Assert.DoesNotContain("normalizerVersion", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyJiraProvenance_WithoutVersion_RemainsReadable()
    {
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            storyTitle = "Guest checkout",
            storyDescription = "Flow.",
            acceptanceCriteria = new[] { "Pay" },
            promptVersion = "story-to-tests-v1",
            source = "story-ai",
            origin = "jira-import",
            jiraIssueKey = "PROJ-123",
            jiraIssueType = "Story",
            jiraBaseUrlHost = "company.atlassian.net",
            jiraFetchedAt = "2026-10-06T00:00:00Z",
        }));
        Assert.NotNull(JiraStorySnapshotComparer.TryReadStored(legacy));
        Assert.NotNull(JiraProvenanceReader.TryRead(legacy));
    }

    // ---------- defensive behavior ----------

    [Fact]
    public void MalformedStoredProvenance_SafelyFailsBaseline()
    {
        using var array = JsonDocument.Parse("[1,2]");
        Assert.Null(JiraStorySnapshotComparer.TryReadStored(array));
        Assert.Null(JiraStorySnapshotComparer.TryReadStored(null));
    }

    [Fact]
    public void MalformedFreshData_HandledByNormalizerSafely()
    {
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("Title", "not-json", "Story"));
        Assert.Equal(JiraNormalizationFailure.Malformed, ex.Reason);
        Assert.DoesNotContain("not-json", ex.Message, StringComparison.Ordinal);
    }
}
