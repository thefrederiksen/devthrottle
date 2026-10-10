using CcDirector.Core.ErrorReports;
using CcDirector.Setup.Cli;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

/// <summary>
/// What `install` reports around the install and the next step it prints (issue #3722).
/// </summary>
public class InstallProgressTests
{
    [Fact]
    public void NextStep_Mac_SaysWhereTheAppIsAndSignIn()
    {
        var lines = InstallProgress.NextStep(InstallRole.Workstation, isMac: true, isWindows: false);
        var text = string.Join("\n", lines);
        Assert.Contains("~/Applications", text);
        Assert.Contains("Command-Space", text);
        Assert.Contains("Sign in and connect", text);
    }

    [Fact]
    public void NextStep_Windows_SaysStartMenu()
    {
        var text = string.Join("\n", InstallProgress.NextStep(InstallRole.Workstation, isMac: false, isWindows: true));
        Assert.Contains("Start menu", text);
        Assert.Contains("Sign in and connect", text);
    }

    [Fact]
    public void NextStep_Linux_SaysAppMenu()
    {
        var text = string.Join("\n", InstallProgress.NextStep(InstallRole.Workstation, isMac: false, isWindows: false));
        Assert.Contains("app menu", text);
    }

    [Fact]
    public void NextStep_GatewayInstall_PrintsNothing()
    {
        Assert.Empty(InstallProgress.NextStep(InstallRole.Gateway, isMac: false, isWindows: true));
    }

    [Fact]
    public void Messages_CarryTheTagWhenThereIsOne()
    {
        Assert.EndsWith("[tag k3m9x2qa]", InstallProgress.StartMessage(InstallRole.Workstation, "k3m9x2qa"));
        Assert.EndsWith("[tag k3m9x2qa]", InstallProgress.DoneMessage(InstallRole.Workstation, "k3m9x2qa"));
        Assert.DoesNotContain("[tag", InstallProgress.StartMessage(InstallRole.Workstation, null));
    }

    [Fact]
    public void FailedMessage_SaysTheExitCode()
    {
        Assert.Contains("exit code 1", InstallProgress.FailedMessage(InstallRole.Workstation, 1, null));
    }

    [Theory]
    [InlineData("k3m9x2qa", "k3m9x2qa")]
    [InlineData("abcd", "abcd")]
    [InlineData("abc", null)]
    [InlineData("ABCDEFGH", null)]
    [InlineData("k3m9-x2qa", null)]
    [InlineData("k3m9x2qa k3m9x2qa", null)]
    [InlineData("abcdefghijklmnopq", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void InstallTag_AcceptsOnlyShortLowerCaseLettersAndDigits(string? raw, string? expected)
    {
        Assert.Equal(expected, InstallTag.Parse(raw));
    }
}
