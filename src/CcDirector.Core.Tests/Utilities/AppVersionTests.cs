using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

// =====================================================================================
// AppVersion - parsing of the SDK-stamped informational version ("semver+sha") that
// every UI surface displays. Single version source: Directory.Build.props.
// =====================================================================================
public sealed class AppVersionTests
{
    [Fact]
    public void Parse_SemverWithSha_SplitsAndShortensSha()
    {
        var full = AppVersion.Parse("0.6.3+1cc1abd9c2f0aa31764fe1f5d8f3f1b2f9e0d4c7", out var semver, out var sha);

        Assert.Equal("0.6.3+1cc1abd9c2f0aa31764fe1f5d8f3f1b2f9e0d4c7", full);
        Assert.Equal("0.6.3", semver);
        Assert.Equal("1cc1abd", sha);
    }

    [Fact]
    public void Parse_PrereleaseWithSha_KeepsPrereleaseTag()
    {
        AppVersion.Parse("0.6.0-rc1+abcdef0123456789", out var semver, out var sha);

        Assert.Equal("0.6.0-rc1", semver);
        Assert.Equal("abcdef0", sha);
    }

    [Fact]
    public void Parse_NoSha_ReturnsEmptySha()
    {
        AppVersion.Parse("0.6.3", out var semver, out var sha);

        Assert.Equal("0.6.3", semver);
        Assert.Equal("", sha);
    }

    [Fact]
    public void Parse_ShortShaSuffix_NotTruncated()
    {
        AppVersion.Parse("1.2.3+ab12", out var semver, out var sha);

        Assert.Equal("1.2.3", semver);
        Assert.Equal("ab12", sha);
    }

    [Fact]
    public void StaticProperties_AreConsistent()
    {
        // In the test host the entry assembly is the test runner; we only assert shape,
        // not a specific version.
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Semver));
        Assert.StartsWith($"v{AppVersion.Semver}", AppVersion.Display);
        Assert.StartsWith(AppVersion.Semver, AppVersion.Full);
        if (AppVersion.ShortSha.Length > 0)
        {
            Assert.True(AppVersion.ShortSha.Length <= 7);
            Assert.Contains($"({AppVersion.ShortSha})", AppVersion.Display);
        }
    }
}
