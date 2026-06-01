using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class VersionUtilTests
{
    [Theory]
    [InlineData("v0.3.3", "0.3.3")]
    [InlineData("0.3.3", "0.3.3")]
    [InlineData("0.3.3-rc1", "0.3.3")]
    [InlineData("1.2.0.4", "1.2.0")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("0.4.0+build7", "0.4.0")]
    public void TryParse_NormalizesKnownForms(string input, string expected)
    {
        var parsed = VersionUtil.TryParse(input);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData(null)]
    public void TryParse_ReturnsNullForGarbage(string? input)
    {
        Assert.Null(VersionUtil.TryParse(input));
    }

    [Theory]
    [InlineData("0.4.0", "0.3.9", true)]
    [InlineData("0.3.9", "0.4.0", false)]
    [InlineData("0.4.0", "0.4.0", false)]
    [InlineData("1.0.0", "0.9.99", true)]
    public void IsNewer_ComparesCorrectly(string candidate, string installed, bool expected)
    {
        Assert.Equal(expected, VersionUtil.IsNewer(candidate, installed));
    }

    [Fact]
    public void IsNewer_FalseWhenEitherUnparseable()
    {
        Assert.False(VersionUtil.IsNewer("0.4.0", null));
        Assert.False(VersionUtil.IsNewer(null, "0.3.0"));
        Assert.False(VersionUtil.IsNewer("garbage", "0.3.0"));
    }
}
