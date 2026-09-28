using AutoTestAi.Domain.Projects;

namespace AutoTestAi.UnitTests;

public sealed class ProjectKeyTests
{
    [Theory]
    [InlineData("AB", "AB")]
    [InlineData("customer-portal", "CUSTOMER-PORTAL")]
    [InlineData("  Shop_2026  ", "SHOP_2026")]
    [InlineData("A1_-B2", "A1_-B2")]
    public void Normalize_TrimsAndUpperCases(string input, string expected)
        => Assert.Equal(expected, ProjectKey.Normalize(input));

    [Theory]
    [InlineData("AB")]
    [InlineData("CUSTOMER-PORTAL")]
    [InlineData("SHOP_2026")]
    [InlineData("A1")]
    public void ValidFormats_Accepted(string key)
        => Assert.True(ProjectKey.IsValidFormat(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]            // too short
    [InlineData("1ABC")]         // must start with a letter
    [InlineData("-ABC")]         // must start with a letter
    [InlineData("AB CD")]        // no spaces
    [InlineData("AB.CD")]        // no dots
    [InlineData("AB/CD")]        // no slashes
    [InlineData("lower")]        // unnormalized input rejected
    public void InvalidFormats_Rejected(string? key)
        => Assert.False(ProjectKey.IsValidFormat(key));

    [Fact]
    public void TooLong_Rejected()
        => Assert.False(ProjectKey.IsValidFormat(new string('A', ProjectKey.MaxLength + 1)));
}
