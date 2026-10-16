using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Xunit;

namespace MergeTests;

public class VersionRuleTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("*", true)]
    [InlineData(">=1.0.0", false)]
    public void IsAny_means_any_version(string rule, bool expected)
    {
        Assert.Equal(expected, VersionRule.IsAny(rule));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.1", false)]
    [InlineData("1.2", "1.2.0", true)]
    [InlineData("=1.2.0", "1.2.0", true)]
    [InlineData(">=1.0.0", "1.0.0", true)]
    [InlineData(">=1.0.0", "1.4.2", true)]
    [InlineData(">=1.0.0", "0.9.0", false)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData(">1.0.0", "1.0.1", true)]
    [InlineData("<2.0.0", "1.9.9", true)]
    [InlineData("<2.0.0", "2.0.0", false)]
    [InlineData("<=2.0.0", "2.0.0", true)]
    [InlineData("<=2.0.0", "2.0.1", false)]
    [InlineData(">= 1.10.0", "1.9.0", false)]
    [InlineData(">= 1.10.0", "1.10.0", true)]
    public void Allows_compares_each_number(string rule, string installed, bool expected)
    {
        Assert.True(VersionRule.TryParse(rule, out VersionRule? parsed));
        Assert.True(VersionRule.TryParseVersion(installed, out int[]? have));
        Assert.Equal(expected, parsed.Allows(have));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData(">=")]
    [InlineData("1.0.0-beta")]
    [InlineData("~1.0.0")]
    [InlineData("1.0.0.0.0")]
    public void Unreadable_rules_are_rejected(string rule)
    {
        Assert.False(VersionRule.TryParse(rule, out _));
    }
}
