using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestGeneration;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Phase 4 Slice 5: transient Jira story import — issue-key rules,
/// ADF allowlist/bounds, criteria extraction, normalization caps, redaction,
/// safe provenance, and Slice-3 regression (no prompt/version change).</summary>
public sealed class JiraStoryImportTests
{
    private static readonly Guid ProjectA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private const string SimpleAdf =
        """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Guest checkout flow."}]}]}""";

    private static string Q(string text)
        => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Para(string text)
        => "{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":" + Q(text) + "}]}";

    private static string Item(params string[] children)
        => "{\"type\":\"listItem\",\"content\":[" + string.Join(",", children) + "]}";

    private static string Bullets(params string[] items)
        => "{\"type\":\"bulletList\",\"content\":[" + string.Join(",", items) + "]}";

    private static string Doc(params string[] blocks)
        => "{\"version\":1,\"type\":\"doc\",\"content\":[" + string.Join(",", blocks) + "]}";

    private static string ListAdf(params string[] items)
    {
        var listItems = string.Join(",", items.Select(i =>
            "{\"type\":\"listItem\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":" +
            Q(i) + "}]}]}"));
        return "{\"version\":1,\"type\":\"doc\",\"content\":[{\"type\":\"bulletList\",\"content\":[" + listItems + "]}]}";
    }

    // ---------- issue key ----------

    [Theory]
    [InlineData("PROJ-123", "PROJ-123")]
    [InlineData("ABC-1", "ABC-1")]
    [InlineData("proj-123", "PROJ-123")]
    [InlineData("  abc-42  ", "ABC-42")]
    public void IssueKey_Valid_Normalizes(string input, string expected)
        => Assert.Equal(expected, JiraIssueKey.NormalizeOrThrow(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PROJ123")]
    [InlineData("PROJ-")]
    [InlineData("-123")]
    [InlineData("PR_OJ-1")]
    [InlineData("PROJ 123")]
    [InlineData("PROJ-12A")]
    [InlineData("1PROJ-123")]
    [InlineData("https://jira.test/browse/PROJ-123")]
    public void IssueKey_Invalid_Rejected(string? input)
    {
        Assert.False(JiraIssueKey.TryNormalize(input, out _));
        Assert.Throws<ValidationException>(() => JiraIssueKey.NormalizeOrThrow(input));
    }

    [Fact]
    public void IssueKey_TooLong_Rejected()
    {
        var key = "ABCDEFGHIJKLMNOPQRSTUVWXY-123456789";
        Assert.True(key.Length > JiraIssueKey.MaxLength);
        Assert.False(JiraIssueKey.TryNormalize(key, out _));
    }

    // ---------- ADF extraction ----------

    [Fact]
    public void Adf_Paragraph_Extracted()
    {
        var story = JiraStoryNormalizer.Normalize("Guest checkout", SimpleAdf, "Story");
        Assert.Equal("Guest checkout", story.StoryTitle);
        Assert.Equal("Guest checkout flow.", story.StoryDescription);
        Assert.Single(story.AcceptanceCriteria);
    }

    [Fact]
    public void Adf_Heading_Extracted()
    {
        const string adf =
            """{"version":1,"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Checkout rules"}]}]}""";
        var story = JiraStoryNormalizer.Normalize("Title", adf, "Task");
        Assert.Equal("Checkout rules", story.StoryDescription);
    }

    [Fact]
    public void Adf_BulletList_BecomesCriteria()
    {
        var story = JiraStoryNormalizer.Normalize("Title", ListAdf("Guest can pay", "Receipt shown"), "Story");
        Assert.Equal(2, story.AcceptanceCriteria.Count);
        Assert.Equal("Guest can pay", story.AcceptanceCriteria[0]);
        Assert.Equal("Receipt shown", story.AcceptanceCriteria[1]);
    }

    [Fact]
    public void Adf_OrderedList_BecomesCriteria()
    {
        const string adf =
            """{"version":1,"type":"doc","content":[{"type":"orderedList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"First"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Second"}]}]}]}]}""";
        var story = JiraStoryNormalizer.Normalize("Title", adf, "Story");
        Assert.Equal(new[] { "First", "Second" }, story.AcceptanceCriteria);
    }

    [Fact]
    public void Adf_NestedLists_FlattenedDeterministically()
    {
        var adf = Doc(Bullets(Item(Para("Outer"), Bullets(Item(Para("Inner"))))));
        var first = JiraStoryNormalizer.Normalize("Title", adf, "Story");
        var second = JiraStoryNormalizer.Normalize("Title", adf, "Story");
        Assert.Equal(first.AcceptanceCriteria, second.AcceptanceCriteria);
        Assert.Single(first.AcceptanceCriteria);
        Assert.Contains("Outer", first.AcceptanceCriteria[0], StringComparison.Ordinal);
        Assert.Contains("Inner", first.AcceptanceCriteria[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Adf_HardBreak_BecomesNewline()
    {
        const string adf =
            """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Line one"},{"type":"hardBreak"},{"type":"text","text":"line two"}]}]}""";
        var story = JiraStoryNormalizer.Normalize("Title", adf, "Story");
        Assert.Contains("Line one\nline two", story.StoryDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Adf_Marks_Ignored_ButTextKept()
    {
        const string adf =
            """{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Ignore ","marks":[{"type":"strong"}]},{"type":"text","text":"https://evil.test/x","marks":[{"type":"link","attrs":{"href":"https://evil.test/x"}}]}]}]}""";
        var story = JiraStoryNormalizer.Normalize("Title", adf, "Story");
        Assert.Contains("Ignore", story.StoryDescription, StringComparison.Ordinal);
        Assert.Contains("https://evil.test/x", story.StoryDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("mediaGroup")]
    [InlineData("inlineCard")]
    [InlineData("codeBlock")]
    [InlineData("table")]
    [InlineData("mention")]
    [InlineData("emoji")]
    [InlineData("expand")]
    [InlineData("panel")]
    [InlineData("mysteryNode")]
    public void Adf_UnsupportedNodes_Dropped(string nodeType)
    {
        var adf =
            "{\"version\":1,\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Kept\"}]},{\"type\":" +
            Q(nodeType) +
            ",\"content\":[{\"type\":\"text\",\"text\":\"SECRET-DROP-ME\"}]}]}";
        var story = JiraStoryNormalizer.Normalize("Title", adf, "Story");
        Assert.Equal("Kept", story.StoryDescription);
        Assert.DoesNotContain("SECRET-DROP-ME", story.StoryDescription ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"paragraph"}""")]
    [InlineData("""{"version":1,"type":"doc"}""")]
    public void Adf_Malformed_ThrowsMalformed(string adf)
    {
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("Title", adf, "Story"));
        Assert.Equal(JiraNormalizationFailure.Malformed, ex.Reason);
        Assert.DoesNotContain(adf, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adf_ExcessiveDepth_ThrowsOversized()
    {
        var inner = Item(Para("level zero"));
        for (var i = 0; i < 25; i++)
            inner = Item(Para("level " + i), Bullets(inner));
        var adf = Doc(Bullets(inner));
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("Title", adf, "Story"));
        Assert.Equal(JiraNormalizationFailure.Oversized, ex.Reason);
    }

    [Fact]
    public void Adf_ExcessiveNodes_ThrowsOversized()
    {
        var blocks = string.Join(",", Enumerable.Range(0, 1100).Select(i =>
            """{"type":"paragraph","content":[{"type":"text","text":"x"}]}"""));
        var adf = """{"version":1,"type":"doc","content":[""" + blocks + """]}""";
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("Title", adf, "Story"));
        Assert.Equal(JiraNormalizationFailure.Oversized, ex.Reason);
    }

    [Fact]
    public void Adf_OversizedText_ThrowsOversized()
    {
        var big = new string('x', 25000);
        var adf = "{\"version\":1,\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":" +
            Q(big) + "}]}]}";
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("Title", adf, "Story"));
        Assert.Equal(JiraNormalizationFailure.Oversized, ex.Reason);
    }

    // ---------- normalization bounds ----------

    [Fact]
    public void Title_Capped_At200()
    {
        var story = JiraStoryNormalizer.Normalize(new string('T', 250), SimpleAdf, "Story");
        Assert.Equal(JiraStoryNormalizer.MaxTitleLength, story.StoryTitle.Length);
    }

    [Fact]
    public void Description_Capped_At4000_WithMarker()
    {
        var big = string.Join(" ", Enumerable.Range(0, 600).Select(i => "word" + i));
        var adf = "{\"version\":1,\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":" +
            Q(big.Replace("\"", "'")) + "}]}]}";
        var story = JiraStoryNormalizer.Normalize("Title", adf, "Story");
        Assert.NotNull(story.StoryDescription);
        Assert.True(story.StoryDescription!.Length <= JiraStoryNormalizer.MaxDescriptionLength);
        Assert.Contains("[truncated]", story.StoryDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Criteria_Capped_At50_And_PerCriterion_At2000()
    {
        var items = Enumerable.Range(0, 60).Select(i => "criterion-" + i).ToArray();
        var story = JiraStoryNormalizer.Normalize("Title", ListAdf(items), "Story");
        Assert.Equal(JiraStoryNormalizer.MaxCriteria, story.AcceptanceCriteria.Count);

        var longItem = new string('c', 2500);
        var single = JiraStoryNormalizer.Normalize("Title", ListAdf(longItem), "Story");
        Assert.True(single.AcceptanceCriteria[0].Length <= JiraStoryNormalizer.MaxCriterionLength);
        Assert.Contains("[truncated]", single.AcceptanceCriteria[0], StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCriteria_Removed_And_Fallback_ToDescription()
    {
        var story = JiraStoryNormalizer.Normalize("Title", SimpleAdf, "Story");
        Assert.Single(story.AcceptanceCriteria);
        Assert.Equal("Guest checkout flow.", story.AcceptanceCriteria[0]);
    }

    [Fact]
    public void EmptyTitle_Rejected()
    {
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("   ", SimpleAdf, "Story"));
        Assert.Equal(JiraNormalizationFailure.Empty, ex.Reason);
    }

    [Fact]
    public void EmptyContent_Rejected()
    {
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("Title", null, "Story"));
        Assert.Equal(JiraNormalizationFailure.Empty, ex.Reason);
    }

    [Fact]
    public void MissingIssueType_Rejected_AsMalformed()
    {
        var ex = Assert.Throws<JiraStoryNormalizationException>(
            () => JiraStoryNormalizer.Normalize("Title", SimpleAdf, "  "));
        Assert.Equal(JiraNormalizationFailure.Malformed, ex.Reason);
    }

    [Fact]
    public void CustomIssueType_Accepted_AsProvenance()
    {
        var story = JiraStoryNormalizer.Normalize("Title", SimpleAdf, "Requirement Ghotic-9");
        Assert.Equal("Requirement Ghotic-9", story.IssueType);
    }

    [Fact]
    public void Secrets_Redacted_InTitle_Description_And_Criteria()
    {
        const string secret = "sk-live-123";
        var story = JiraStoryNormalizer.Normalize(
            "Title",
            ListAdf("Use api-key=" + secret + " here"),
            "Story");
        Assert.DoesNotContain(secret, story.AcceptanceCriteria[0], StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", story.AcceptanceCriteria[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Normalization_IsDeterministic()
    {
        var adf = ListAdf("  spaced   out  ", "", "kept");
        var first = JiraStoryNormalizer.Normalize("  Title  ", adf, "Story");
        var second = JiraStoryNormalizer.Normalize("  Title  ", adf, "Story");
        Assert.Equal(first.StoryTitle, second.StoryTitle);
        Assert.Equal(first.StoryDescription, second.StoryDescription);
        Assert.Equal(first.AcceptanceCriteria, second.AcceptanceCriteria);
        Assert.Equal("Title", first.StoryTitle);
        Assert.DoesNotContain("  ", first.AcceptanceCriteria[0], StringComparison.Ordinal);
    }

    // ---------- provenance + Slice-3 regression ----------

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
        public readonly List<string> Actions = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private static StoryTestGenerationService CreateService(FakeAudit? audit = null)
    {
        var provider = new FakeProvider();
        return new StoryTestGenerationService(
            new FakeResolver { Provider = provider },
            new AiGenerationValidator(),
            new AiGenerationRateLimiter(
                Options.Create(new AiOptions { MaxGenerationsPerMinutePerProject = 1000 }),
                new FixedClock(DateTimeOffset.UtcNow)),
            new FakeAuthorization(),
            audit ?? new FakeAudit());
    }

    [Fact]
    public async Task Provenance_WithJira_ContainsOnlySafeMetadata()
    {
        var service = CreateService();
        var result = await service.GenerateStoryProposalsAsync(new GenerateStoryTestsCommand(
            ProjectA, "Guest checkout", "Flow.", new[] { "Pay" },
            null, "playwright", "web", null, null, null, 1,
            new JiraImportMetadata("PROJ-123", "Story", "company.atlassian.net", "2026-10-06T00:00:00Z")),
            CancellationToken.None);
        var proposal = result.Proposals.Single(p => p.Status == "Succeeded");
        Assert.NotNull(proposal.Provenance);
        var raw = proposal.Provenance!.Value.GetRawText();
        Assert.Contains("PROJ-123", raw, StringComparison.Ordinal);
        Assert.Contains("jira-import", raw, StringComparison.Ordinal);
        Assert.Contains("company.atlassian.net", raw, StringComparison.Ordinal);
        Assert.Contains(AiPromptVersions.StoryToTestsV1, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"version\":1", raw, StringComparison.Ordinal);
        Assert.Equal("story-to-tests-v1", result.PromptVersion);
    }

    [Fact]
    public async Task Provenance_WithoutJira_HasNoJiraFields()
    {
        var service = CreateService();
        var result = await service.GenerateStoryProposalsAsync(new GenerateStoryTestsCommand(
            ProjectA, "Guest checkout", "Flow.", new[] { "Pay" },
            null, "playwright", "web", null, null, null, 1),
            CancellationToken.None);
        var raw = result.Proposals.Single(p => p.Status == "Succeeded").Provenance!.Value.GetRawText();
        Assert.DoesNotContain("jiraIssueKey", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("origin", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("jira-import", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadLimiter_IsSeparate_FromAiBudget()
    {
        var limiter = new JiraStoryImportRateLimiter();
        for (var i = 0; i < JiraStoryImportRateLimiter.MaxReadsPerMinutePerProject; i++)
            limiter.CheckOrThrow(ProjectA);
        Assert.Throws<AutoTestAi.Application.Tickets.JiraProviderException>(() => limiter.CheckOrThrow(ProjectA));
    }
}
