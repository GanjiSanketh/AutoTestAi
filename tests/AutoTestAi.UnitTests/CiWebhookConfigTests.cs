using AutoTestAi.Application.Common;
using AutoTestAi.Application.Webhooks;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3B: CI config validation, variable mapping, idempotency keys, rate limiting.</summary>
public sealed class CiWebhookConfigTests
{
    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public DateTimeOffset UtcNow => _now;
    }

    [Fact]
    public void ProviderNames_AllowlistIsExact()
    {
        Assert.True(CiProviderNames.IsSupported("github"));
        Assert.True(CiProviderNames.IsSupported("GitHub"));
        Assert.False(CiProviderNames.IsSupported("jira"));
        Assert.False(CiProviderNames.IsSupported(""));
        Assert.False(CiProviderNames.IsSupported(null));
        Assert.Equal("cicd", CiProviderNames.IntegrationType);
    }

    [Fact]
    public void Config_RejectsInvalidVariableMapping()
    {
        var errors = new List<FieldError>();
        CiIntegrationConfig.ValidateFields(null, null, null, null, null,
            new Dictionary<string, string> { ["lowercase"] = "BRANCH" }, null, null, errors);
        Assert.Contains(errors, e => e.Field.StartsWith("variableMapping.", StringComparison.Ordinal));

        errors.Clear();
        CiIntegrationConfig.ValidateFields(null, null, null, null, null,
            new Dictionary<string, string> { ["BRANCH"] = "ENTIRE_PAYLOAD" }, null, null, errors);
        Assert.Contains(errors, e => e.Field.StartsWith("variableMapping.", StringComparison.Ordinal));

        errors.Clear();
        CiIntegrationConfig.ValidateFields(null, null, null, null, null,
            new Dictionary<string, string> { ["BRANCH"] = "BRANCH" }, null,
            new Dictionary<string, string> { ["API_KEY"] = "raw-secret-value" }, errors);
        Assert.Contains(errors, e => e.Field.StartsWith("secretMapping.", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_RoundTripsThroughJson()
    {
        var config = new CiIntegrationConfig(
            Guid.NewGuid(), Guid.NewGuid(),
            new[] { "push" }, new[] { "main" }, new[] { "octo/repo" },
            new Dictionary<string, string> { ["BRANCH"] = "BRANCH" },
            "hookuser",
            new Dictionary<string, string> { ["API_KEY"] = "env_secret:11111111-1111-1111-1111-111111111111" });
        var restored = CiIntegrationConfig.FromJson(System.Text.Json.JsonDocument.Parse(config.ToJson()));
        Assert.Equal(config.DefaultSuiteId, restored.DefaultSuiteId);
        Assert.Equal(config.EventAllowlist, restored.EventAllowlist);
        Assert.Equal("hookuser", restored.Username);
        Assert.Equal("BRANCH", restored.VariableMapping["BRANCH"]);
        Assert.DoesNotContain("raw", restored.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VariableMapping_BuildsOnlyConfiguredOverrides()
    {
        var normalized = new NormalizedCiEvent("github", Guid.NewGuid(), Guid.NewGuid(),
            "d", "push", "main", "abc123", "octo/repo", null, null, "run-9", "octocat",
            DateTimeOffset.UtcNow, "hash");
        var overrides = CiVariableMapping.BuildOverrides(
            new Dictionary<string, string> { ["BRANCH"] = "BRANCH", ["SKIPPED"] = "BUILD_NUMBER", ["REPO"] = "REPOSITORY" },
            normalized);
        Assert.Equal("main", overrides["BRANCH"]);
        Assert.Equal("octo/repo", overrides["REPO"]);
        Assert.DoesNotContain("SKIPPED", overrides.Keys);
    }

    [Fact]
    public void VariableMapping_RejectsOverlongValues()
    {
        var normalized = new NormalizedCiEvent("github", Guid.NewGuid(), Guid.NewGuid(),
            "d", "push", new string('x', 501), null, null, null, null, null, null,
            DateTimeOffset.UtcNow, "hash");
        Assert.Throws<ValidationException>(() => CiVariableMapping.BuildOverrides(
            new Dictionary<string, string> { ["BRANCH"] = "BRANCH" }, normalized));
    }

    [Fact]
    public void IdempotencyKey_IsDeterministicAndBounded()
    {
        var integrationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var first = WebhookProcessingService.IdempotencyKeyFor(integrationId, "delivery-1", 0);
        var second = WebhookProcessingService.IdempotencyKeyFor(integrationId, "delivery-1", 0);
        Assert.Equal(first, second);
        Assert.StartsWith("wh:11111111", first, StringComparison.Ordinal);
        Assert.NotEqual(first, WebhookProcessingService.IdempotencyKeyFor(integrationId, "delivery-1", 1));

        var longKey = WebhookProcessingService.IdempotencyKeyFor(integrationId, new string('d', 200), 3);
        Assert.True(longKey.Length <= 100);
        Assert.Equal(longKey, WebhookProcessingService.IdempotencyKeyFor(integrationId, new string('d', 200), 3));
    }

    [Fact]
    public void WebhookOptions_DefaultsMatchSliceContract()
    {
        var options = new WebhookOptions();
        Assert.Equal(1024 * 1024, options.MaxBodyBytes);
        Assert.Equal(100, options.MaxDeliveriesPerMinutePerProject);
        Assert.Equal(100, options.MaxSuiteMembers);
        Assert.Equal(90, options.RetentionDays);
        Assert.True(options.BackgroundProcessingEnabled);
    }

    [Fact]
    public void RateLimiter_AllowsBudgetThenThrows()
    {
        var options = Options.Create(new WebhookOptions { MaxDeliveriesPerMinutePerProject = 2 });
        var limiter = new WebhookRateLimiter(options, new FixedClock(DateTimeOffset.UtcNow));
        var project = Guid.NewGuid();
        limiter.CheckOrThrow(project);
        limiter.CheckOrThrow(project);
        Assert.Throws<WebhookRateLimitedException>(() => limiter.CheckOrThrow(project));
        // Other projects are unaffected.
        limiter.CheckOrThrow(Guid.NewGuid());
    }
}
