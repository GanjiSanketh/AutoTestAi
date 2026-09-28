using System.Text.Json;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.TestCases;

namespace AutoTestAi.UnitTests;

public sealed class TestStepValidationTests
{
    private static JsonElement? Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ValidSteps_Pass()
    {
        var problems = TestStep.Validate(Parse(
            """[{"order":1,"action":"navigate","target":"https://example.test"},{"order":2,"action":"click","target":"#login","value":"x"}]"""));
        Assert.Empty(problems);
    }

    [Fact]
    public void NonArray_Rejected()
    {
        Assert.NotEmpty(TestStep.Validate(Parse("""{"order":1}""")));
        Assert.NotEmpty(TestStep.Validate(null));
    }

    [Fact]
    public void MissingAction_AndBadOrder_Rejected()
    {
        var problems = TestStep.Validate(Parse(
            """[{"order":0,"target":"#x"},{"action":"  "}]"""));
        Assert.Contains(problems, p => p.Contains("'order'"));
        Assert.Contains(problems, p => p.Contains("'action'"));
    }

    [Fact]
    public void TooManySteps_Rejected()
    {
        var items = string.Join(",", Enumerable.Range(1, TestStep.MaxSteps + 1)
            .Select(i => $"{{\"order\":{i},\"action\":\"click\"}}"));
        Assert.Contains(TestStep.Validate(Parse($"[{items}]")), p => p.Contains("must not exceed"));
    }

    [Fact]
    public void ContentEquals_IgnoresFormattingAndKeyOrder()
    {
        var a = Parse("""[{"order":1,"action":"click","target":"#x"}]""");
        var b = Parse("""[ { "target" : "#x" , "action" : "click" , "order" : 1 } ]""");
        var c = Parse("""[{"order":1,"action":"click","target":"#y"}]""");
        Assert.True(TestStep.ContentEquals(a, b));
        Assert.False(TestStep.ContentEquals(a, c));
        Assert.True(TestStep.ContentEquals(null, null));
        Assert.False(TestStep.ContentEquals(a, null));
    }

    [Fact]
    public void Parse_OrdersByOrder()
    {
        var steps = TestStep.Parse(Parse(
            """[{"order":2,"action":"b"},{"order":1,"action":"a"}]"""));
        Assert.Equal(["a", "b"], steps.Select(s => s.Action).ToList());
    }
}

public sealed class ReviewStatusTransitionsTests
{
    [Theory]
    [InlineData(ReviewStatus.Pending, ReviewStatus.Approved)]
    [InlineData(ReviewStatus.Pending, ReviewStatus.ChangesRequested)]
    [InlineData(ReviewStatus.Pending, ReviewStatus.Rejected)]
    [InlineData(ReviewStatus.ChangesRequested, ReviewStatus.Approved)]
    [InlineData(ReviewStatus.Approved, ReviewStatus.ChangesRequested)]
    [InlineData(ReviewStatus.Approved, ReviewStatus.Rejected)]
    [InlineData(ReviewStatus.Rejected, ReviewStatus.Pending)]
    [InlineData(ReviewStatus.Rejected, ReviewStatus.ChangesRequested)]
    [InlineData(ReviewStatus.Approved, ReviewStatus.Approved)]
    public void AllowedTransitions(ReviewStatus from, ReviewStatus to)
        => Assert.True(ReviewStatusTransitions.IsValidTransition(from, to));

    [Theory]
    [InlineData(ReviewStatus.Approved, ReviewStatus.Pending)]
    [InlineData(ReviewStatus.Rejected, ReviewStatus.Approved)]
    public void DisallowedTransitions(ReviewStatus from, ReviewStatus to)
        => Assert.False(ReviewStatusTransitions.IsValidTransition(from, to));
}

public sealed class TestCaseSourceTypesTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("AI")]
    [InlineData("Imported")]
    public void SupportedValues(string value)
        => Assert.True(TestCaseSourceTypes.IsSupported(value));

    [Theory]
    [InlineData("generated")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedValues(string? value)
        => Assert.False(TestCaseSourceTypes.IsSupported(value));

    [Fact]
    public void Normalize_LowerCasesAndDefaults()
    {
        Assert.Equal("ai", TestCaseSourceTypes.Normalize("AI"));
        Assert.Equal("manual", TestCaseSourceTypes.Normalize(null));
    }
}
