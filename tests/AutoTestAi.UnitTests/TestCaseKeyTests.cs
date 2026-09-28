using AutoTestAi.Domain.TestCases;

namespace AutoTestAi.UnitTests;

public sealed class TestCaseKeyTests
{
    [Theory]
    [InlineData("AB", "AB")]
    [InlineData("login-001", "LOGIN-001")]
    [InlineData("  Checkout_Flow-2  ", "CHECKOUT_FLOW-2")]
    public void Normalize_TrimsAndUpperCases(string input, string expected)
        => Assert.Equal(expected, TestCaseKey.Normalize(input));

    [Theory]
    [InlineData("AB")]
    [InlineData("LOGIN-001")]
    [InlineData("A1_-B2")]
    public void ValidFormats_Accepted(string key)
        => Assert.True(TestCaseKey.IsValidFormat(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("1ABC")]
    [InlineData("AB CD")]
    [InlineData("lower")]
    public void InvalidFormats_Rejected(string? key)
        => Assert.False(TestCaseKey.IsValidFormat(key));

    [Fact]
    public void TooLong_Rejected()
        => Assert.False(TestCaseKey.IsValidFormat(new string('A', TestCaseKey.MaxLength + 1)));
}
