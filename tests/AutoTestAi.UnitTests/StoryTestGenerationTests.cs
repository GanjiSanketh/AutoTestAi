using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestGeneration;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Phase 4 Slice 3: manual user-story to test proposals — validation,
/// story-to-tests-v1 prompt, sequential fan-out, partial failure, rate
/// limiting, redaction, proposal identity, and safe audit metadata.</summary>
public sealed class StoryTestGenerationTests
{
    private static readonly Guid ProjectA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ---------- fakes ----------

    private sealed class FakeProvider : IAiProvider
    {
        public string Name { get; init; } = "stub";
        public AiGenerationResult? Result { get; set; }
        public Func<int, AiGenerationResult?>? ResultForCall { get; set; }
        public Func<int, Exception?>? FailureForCall { get; set; }
        public readonly List<AiGenerationRequest> SeenRequests = new();
        public int Calls { get; private set; }

        public Task<AiGenerationResult> GenerateTestAsync(AiGenerationRequest request, CancellationToken ct)
        {
            Calls++;
            SeenRequests.Add(request);
            ct.ThrowIfCancellationRequested();
            var failure = FailureForCall?.Invoke(Calls);
            if (failure is not null) throw failure;
            return Task.FromResult(ResultForCall?.Invoke(Calls) ?? Result!);
        }

        public Task<AiAnalysisResult> AnalyzeFailureAsync(AiFailureAnalysisRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<AiHealingResult> SuggestHealingCandidatesAsync(AiHealingRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeResolver : IAiProviderResolver
    {
        public IAiProvider Provider { get; set; } = null!;
        public int Calls { get; private set; }

        public IAiProvider Resolve()
        {
            Calls++;
            return Provider;
        }

        public AiProviderStatus GetStatus()
            => new(Provider?.Name ?? "stub", "stub-1.0", true, null, AiPromptVersions.StoryToTestsV1);
    }

    private sealed class FakeAuthorization : IAuthorizationService
    {
        public bool Allowed { get; set; } = true;
        public Guid? SeenProject { get; private set; }
        public string? SeenPermission { get; private set; }
        public bool HasPermission(string permission) => Allowed;
        public bool IsAdmin() => false;
        public Task<bool> CanAccessProjectAsync(Guid projectId, CancellationToken ct) => Task.FromResult(Allowed);
        public Task RequireProjectAccessAsync(Guid projectId, string? permission, CancellationToken ct)
        {
            SeenProject = projectId;
            SeenPermission = permission;
            if (!Allowed) throw new ForbiddenException("The caller has no access to this project.");
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEvent(string Action, string? MetadataJson);

    private sealed class FakeAudit : IAuditService
    {
        public readonly List<AuditEvent> Events = new();
        public Task RecordAsync(string action, string entityType, string? entityId, Guid? projectId, string? metadataJson, CancellationToken ct)
        {
            Events.Add(new AuditEvent(action, metadataJson));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    // ---------- builders ----------

    private static GenerateStoryTestsCommand ValidCommand(int maxProposals = 2) => new(
        ProjectA, "Guest checkout", "Allow guests to check out without an account.",
        new[] { "Guest can place an order", "Order confirmation is shown" },
        "https://example.test/checkout", "playwright", "web", "Checkout", "High", null, maxProposals);

    private static AiGenerationResult ValidResult(string provider = "stub") => new(
        Provider: provider,
        Model: "stub-1.0",
        LatencyMs: 5,
        Steps: new[] { new AiGeneratedStep("1", "navigate", "Page loads") },
        SourceCode: "import { test } from '@playwright/test';\ntest('checkout', async () => {});",
        Title: "Guest checkout proposal",
        Framework: "playwright",
        Platform: "web",
        StructuredSteps: new[] { new AiStructuredStep(1, "navigate", "https://example.test/checkout", null) },
        Assumptions: new[] { "Checkout page exists." },
        Warnings: Array.Empty<string>(),
        PromptVersion: AiPromptVersions.StoryToTestsV1);

    private static (StoryTestGenerationService Service, FakeProvider Provider, FakeAuthorization Auth, FakeAudit Audit) Create(
        int rateLimit = 1000)
    {
        var provider = new FakeProvider { Result = ValidResult() };
        var resolver = new FakeResolver { Provider = provider };
        var auth = new FakeAuthorization();
        var audit = new FakeAudit();
        var service = new StoryTestGenerationService(
            resolver,
            new AiGenerationValidator(),
            new AiGenerationRateLimiter(
                Options.Create(new AiOptions { MaxGenerationsPerMinutePerProject = rateLimit }),
                new FixedClock(DateTimeOffset.UtcNow)),
            auth, audit);
        return (service, provider, auth, audit);
    }

    // ---------- validation ----------

    [Fact]
    public async Task MissingTitle_Throws400()
    {
        var (service, _, _, _) = Create();
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            service.GenerateStoryProposalsAsync(ValidCommand() with { StoryTitle = "  " }, CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "storyTitle");
    }

    [Fact]
    public async Task EmptyAcceptanceCriteria_Throws400()
    {
        var (service, _, _, _) = Create();
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            service.GenerateStoryProposalsAsync(
                ValidCommand() with { AcceptanceCriteria = Array.Empty<string>() }, CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "acceptanceCriteria");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(100)]
    public async Task MaxProposals_OutOfRange_Throws400(int maxProposals)
    {
        var (service, _, _, _) = Create();
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            service.GenerateStoryProposalsAsync(ValidCommand(maxProposals), CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "maxProposals");
    }

    [Fact]
    public async Task OversizedCriterion_Throws400()
    {
        var (service, _, _, _) = Create();
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            service.GenerateStoryProposalsAsync(
                ValidCommand() with { AcceptanceCriteria = new[] { new string('x', 2001) } },
                CancellationToken.None));
        Assert.Contains(ex.Errors, e => e.Field == "acceptanceCriteria");
    }

    [Fact]
    public async Task Requires_TestCasesManage_And_ProjectScope()
    {
        var (service, _, auth, _) = Create();
        auth.Allowed = false;
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GenerateStoryProposalsAsync(ValidCommand(), CancellationToken.None));
        Assert.Equal(ProjectA, auth.SeenProject);
        Assert.Equal(Permissions.TestCasesManage, auth.SeenPermission);
    }

    // ---------- prompt contract ----------

    [Fact]
    public void StoryPrompt_HasVersion_And_StoryContent_And_SingleTestRules()
    {
        var builder = new AiStoryTestPromptBuilder();
        Assert.Equal(AiPromptVersions.StoryToTestsV1, builder.PromptVersion);
        var prompt = builder.Build(new AiGenerationRequest(
            "Guest checkout", "Desc", null, "playwright", "web",
            new[] { "Guest can place an order" }, null, null, null,
            new AiStoryContext("Guest checkout", "Desc",
                new[] { "Guest can place an order" }, 0, "Guest can place an order",
                Array.Empty<int>())));
        Assert.Equal(AiPromptVersions.StoryToTestsV1, prompt.PromptVersion);
        Assert.Contains("Guest checkout", prompt.UserPrompt);
        Assert.Contains("Guest can place an order", prompt.UserPrompt);
        Assert.Contains("ONE test case", prompt.SystemPrompt);
        Assert.Contains("single JSON object", prompt.SystemPrompt);
        Assert.Contains("Jira", prompt.SystemPrompt);
        Assert.Contains("never claim", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericV1Prompt_Unchanged_WithoutStoryContext()
    {
        var builder = new AiTestGenerationPromptBuilder();
        var request = new AiGenerationRequest(
            "Login", "Desc", "https://example.test/login", "playwright", "web",
            new[] { "Validate login" }, "Auth", "High", "ctx");
        var prompt = builder.Build(request);
        Assert.Equal(AiPromptVersions.TestGenerationV1, prompt.PromptVersion);
        Assert.Contains("Generate a complete automated test from the following requirements.", prompt.UserPrompt);
        Assert.DoesNotContain("user story", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jira", prompt.SystemPrompt);
    }

    [Fact]
    public void GenericV1Builder_DelegatesStoryRequests_ToStoryContract()
    {
        var builder = new AiTestGenerationPromptBuilder();
        var prompt = builder.Build(new AiGenerationRequest(
            "S", null, null, "playwright", "web", new[] { "AC" }, null, null, null,
            new AiStoryContext("S", null, new[] { "AC" }, 0, "AC", Array.Empty<int>())));
        Assert.Equal(AiPromptVersions.StoryToTestsV1, prompt.PromptVersion);
    }

    // ---------- fan-out and proposals ----------

    [Fact]
    public async Task Generates_OneProposalPerCall_WithRotatingFocus_And_StableIds()
    {
        var (service, provider, _, _) = Create();
        var result = await service.GenerateStoryProposalsAsync(ValidCommand(3), CancellationToken.None);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(3, result.ProposalCount);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(AiPromptVersions.StoryToTestsV1, result.PromptVersion);
        var proposals = result.Proposals.ToList();
        Assert.Equal(new[] { 1, 2, 3 }, proposals.Select(p => p.Index).ToArray());
        Assert.Equal(new[] { 0, 1, 0 }, proposals.Select(p => p.FocusCriterionIndex).ToArray());
        Assert.Equal("Guest can place an order", proposals[0].FocusCriterion);
        Assert.Equal("Order confirmation is shown", proposals[1].FocusCriterion);
        var ids = proposals.Select(p => p.ProposalId).ToList();
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(proposals, p => Assert.Equal("Succeeded", p.Status));
        Assert.All(proposals, p => Assert.NotNull(p.Provenance));
        // Story context reaches the provider seam on every call.
        Assert.All(provider.SeenRequests, r => Assert.NotNull(r.Story));
        Assert.Equal("Guest checkout", provider.SeenRequests[0].Story!.StoryTitle);
    }

    [Fact]
    public async Task ProviderFailure_YieldsPartialSuccess_WithSafeErrorCode()
    {
        var (service, provider, _, _) = Create();
        provider.FailureForCall = callNo => callNo == 2
            ? AiProviderException.Unavailable("stub", "boom")
            : null;
        var result = await service.GenerateStoryProposalsAsync(ValidCommand(3), CancellationToken.None);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        var failed = result.Proposals.Single(p => p.Status == "Failed");
        Assert.Equal(2, failed.Index);
        Assert.Equal("PROVIDER_UNAVAILABLE", failed.ErrorCode);
        Assert.DoesNotContain("sk-", failed.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedProviderOutput_RejectedPerProposal()
    {
        var (service, provider, _, _) = Create();
        provider.ResultForCall = callNo => callNo == 1
            ? ValidResult() with { StructuredSteps = Array.Empty<AiStructuredStep>(), Steps = Array.Empty<AiGeneratedStep>() }
            : ValidResult();
        var result = await service.GenerateStoryProposalsAsync(ValidCommand(2), CancellationToken.None);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal("PROVIDER_RESPONSE_ERROR", result.Proposals[0].ErrorCode);
        Assert.Equal("Succeeded", result.Proposals[1].Status);
    }

    [Fact]
    public async Task OverlongGeneratedAction_RejectedPerProposal()
    {
        var (service, provider, _, _) = Create();
        provider.ResultForCall = _ => ValidResult() with
        {
            StructuredSteps = new[] { new AiStructuredStep(1, new string('a', 201), null, null) }
        };
        var result = await service.GenerateStoryProposalsAsync(ValidCommand(1), CancellationToken.None);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal("PROVIDER_RESPONSE_ERROR", result.Proposals[0].ErrorCode);
    }

    [Fact]
    public async Task RateLimit_StopsFanOut_And_MarksRemainder()
    {
        var (service, provider, _, _) = Create(rateLimit: 2);
        var result = await service.GenerateStoryProposalsAsync(ValidCommand(4), CancellationToken.None);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(2, result.FailureCount);
        Assert.All(result.Proposals.Skip(2), p =>
        {
            Assert.Equal("Failed", p.Status);
            Assert.Equal("RATE_LIMITED", p.ErrorCode);
        });
    }

    [Fact]
    public async Task Cancellation_Aborts_WithAudit_And_Rethrows()
    {
        var (service, _, _, audit) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateStoryProposalsAsync(ValidCommand(2), cts.Token));
        Assert.Contains(audit.Events, e =>
            e.Action == "story-generation.failed" && (e.MetadataJson ?? string.Empty).Contains("cancelled"));
    }

    // ---------- redaction and audit ----------

    [Fact]
    public async Task Secrets_Redacted_InProvenance_And_Audit()
    {
        var (service, _, _, audit) = Create();
        var result = await service.GenerateStoryProposalsAsync(
            ValidCommand(1) with
            {
                StoryDescription = "Login with api-key=sk-live-123",
                AcceptanceCriteria = new[] { "Guest can place an order" },
            },
            CancellationToken.None);
        var proposal = result.Proposals.Single();
        Assert.Equal("Succeeded", proposal.Status);
        var provenance = proposal.Provenance!.Value.GetRawText();
        Assert.DoesNotContain("sk-live-123", provenance, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", provenance, StringComparison.Ordinal);
        var completed = audit.Events.Single(e => e.Action == "story-generation.completed");
        Assert.DoesNotContain("sk-live-123", completed.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("story-to-tests-v1", completed.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        var requested = audit.Events.Single(e => e.Action == "story-generation.requested");
        Assert.DoesNotContain("Guest checkout", requested.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_UsesCounts_NotStoryText()
    {
        var (service, _, _, audit) = Create();
        await service.GenerateStoryProposalsAsync(ValidCommand(2), CancellationToken.None);
        Assert.Equal(
            new[] { "story-generation.requested", "story-generation.completed" },
            audit.Events.Select(e => e.Action).ToArray());
    }
}
