using System.IO;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The launcher's <c>--version</c> answer (#3411): the macOS installer runs the placed binary this way when
/// launchd will not start it, to tell a refused job from a refused program. The answer is the version and exit
/// 0; a build with no version metadata is a broken build and fails with exit 1, never "unknown" and success.
/// </summary>
public sealed class VersionFlagTests
{
    [Fact]
    public void AnswerVersion_WithMetadata_PrintsItAndSucceeds()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = Program.AnswerVersion("2.17.0+abc123", stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal("2.17.0+abc123", stdout.ToString().Trim());
        Assert.Equal("", stderr.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnswerVersion_WithoutMetadata_FailsLoudly(string? missing)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = Program.AnswerVersion(missing, stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("no version metadata", stderr.ToString());
        Assert.DoesNotContain("unknown", stdout.ToString());
    }
}
