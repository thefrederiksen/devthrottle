using CcDirector.Setup.Cli;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

/// <summary>
/// Only commands that write the per-user install are refused as root and checked for root-owned files
/// (#3411). A read-only command must still answer on a damaged install - that is when it is needed.
/// The read-only list is closed: plan and --dry-run write the release cache, so they are guarded, and so
/// is any command nobody has shown to be read-only.
/// </summary>
public class WritesTheInstallTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("enroll")]
    [InlineData("uninstall")]
    [InlineData("rollback")]
    [InlineData("enroll --dry-run")]
    [InlineData("rollback director --dry-run")]
    [InlineData("signin")]
    [InlineData("plan")]
    [InlineData("install --dry-run")]
    [InlineData("update --dry-run")]
    [InlineData("uninstall --dry-run")]
    [InlineData("some-future-command")]
    [InlineData("status --log-file /tmp/status.log")]
    [InlineData("autostart on")]
    [InlineData("autostart off")]
    public void WritingCommands_AreChecked(string command) =>
        Assert.True(Program.WritesTheInstall(CliArgs.Parse(command.Split(' '))));

    [Theory]
    [InlineData("status")]
    [InlineData("status --json")]
    [InlineData("components")]
    [InlineData("prereqs")]
    [InlineData("version")]
    [InlineData("help")]
    [InlineData("autostart")]
    [InlineData("autostart status")]
    public void ReadOnlyCommands_AreNot(string command) =>
        Assert.False(Program.WritesTheInstall(CliArgs.Parse(command.Split(' '))));
}
