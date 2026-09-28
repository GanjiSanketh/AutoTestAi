using AutoTestAi.Application.AI;
using AutoTestAi.Application.Common;

namespace AutoTestAi.UnitTests;

public sealed class StubAiProviderTests
{
    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private static StubAiProvider CreateProvider()
        => new(new FixedClock(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task GenerateTest_ReturnsSteps_AndPlaywrightSource()
    {
        var provider = CreateProvider();
        var result = await provider.GenerateTestAsync(
            new AiGenerationRequest(
                "User login with valid credentials",
                "Verify login.",
                "https://example.test",
                "playwright-typescript",
                "web",
                ["Validate successful login", "Validate dashboard navigation"]),
            CancellationToken.None);

        Assert.Equal("stub", result.Provider);
        Assert.Equal(2, result.Steps.Count);
        Assert.Contains("@playwright/test", result.SourceCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateTest_RejectsEmptyTitle()
    {
        var provider = CreateProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateTestAsync(
            new AiGenerationRequest("", null, null, "playwright-typescript", "web", []),
            CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeFailure_ReturnsUnknownClassificationPlaceholder()
    {
        var provider = CreateProvider();
        var result = await provider.AnalyzeFailureAsync(
            new AiFailureAnalysisRequest(Guid.NewGuid(), "TimeoutError", "waiting for selector", "Login"),
            CancellationToken.None);

        Assert.Equal("stub", result.Provider);
        Assert.Equal("Unknown", result.Classification);
    }
}
