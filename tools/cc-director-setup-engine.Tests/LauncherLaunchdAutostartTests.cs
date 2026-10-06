using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for <see cref="LauncherLaunchdAutostart"/> - the pure property-list generation.
/// The launchctl interaction is not exercised here (it would mutate the real launchd
/// gui domain); these tests pin the plist format the agent, installer, and uninstaller
/// all rely on.
/// </summary>
public sealed class LauncherLaunchdAutostartTests
{
    private const string Exe = "/Users/tester/Library/Application Support/cc-director/launcher/cc-launcher";
    private const string LogDir = "/Users/tester/Library/Application Support/cc-director/logs/launcher";

    [Fact]
    public void PlistContent_ContainsLabelAndProgramArguments()
    {
        var plist = LauncherLaunchdAutostart.PlistContent(Exe, "--managed", LogDir);

        Assert.Contains("<string>com.devthrottle.cc-launcher</string>", plist);
        Assert.Contains($"<string>{Exe}</string>", plist);
        Assert.Contains("<string>--managed</string>", plist);
    }

    [Fact]
    public void PlistContent_RunsAtLoad()
    {
        var plist = LauncherLaunchdAutostart.PlistContent(Exe, null, LogDir);

        Assert.Contains("<key>RunAtLoad</key>", plist);
    }

    [Fact]
    public void PlistContent_KeepAliveOnlyAfterUnsuccessfulExit()
    {
        // KeepAlive must be conditional on SuccessfulExit=false: a clean exit (Quit, or the
        // self-update helper's /shutdown) must STAY exited, or launchd would relaunch the old
        // binary mid-swap; only a crash may be resurrected.
        var plist = LauncherLaunchdAutostart.PlistContent(Exe, "--managed", LogDir);

        Assert.Contains("<key>KeepAlive</key>", plist);
        Assert.Contains("<key>SuccessfulExit</key>", plist);
        var keepAliveIndex = plist.IndexOf("<key>KeepAlive</key>", StringComparison.Ordinal);
        var successfulExitIndex = plist.IndexOf("<key>SuccessfulExit</key>", StringComparison.Ordinal);
        Assert.True(successfulExitIndex > keepAliveIndex, "SuccessfulExit must live inside the KeepAlive dictionary");
    }

    [Fact]
    public void PlistContent_NoArguments_OmitsExtraStrings()
    {
        var plist = LauncherLaunchdAutostart.PlistContent(Exe, null, LogDir);

        Assert.DoesNotContain("--managed", plist);
    }

    [Fact]
    public void PlistContent_EscapesXmlCharacters()
    {
        var plist = LauncherLaunchdAutostart.PlistContent("/tmp/a&b/cc-launcher", null, "/tmp/a&b/logs");

        Assert.Contains("/tmp/a&amp;b/cc-launcher", plist);
        Assert.DoesNotContain("<string>/tmp/a&b/cc-launcher</string>", plist);
    }

    [Fact]
    public void PlistContent_WritesLogPathsUnderLogDir()
    {
        var plist = LauncherLaunchdAutostart.PlistContent(Exe, "--managed", LogDir);

        Assert.Contains("launchd-stdout.log", plist);
        Assert.Contains("launchd-stderr.log", plist);
    }

    [Fact]
    public void PlistContent_EmptyExe_Throws()
    {
        Assert.Throws<ArgumentException>(() => LauncherLaunchdAutostart.PlistContent("", "--managed", LogDir));
    }

    [Fact]
    public void PlistPath_IsUserLaunchAgentWithLabelFileName()
    {
        Assert.EndsWith(
            Path.Combine("Library", "LaunchAgents", "com.devthrottle.cc-launcher.plist"),
            LauncherLaunchdAutostart.PlistPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MayReload_InsideItsOwnJob_IsFalse()
    {
        // launchd sets XPC_SERVICE_NAME to the job label in the job's processes. A bootout from
        // there kills the caller before it can bootstrap, and the job is gone until the next
        // login (product #3575).
        Assert.False(LauncherLaunchdAutostart.MayReload(LauncherLaunchdAutostart.Label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application.com.apple.Terminal.12345")]
    public void MayReload_OutsideTheJob_IsTrue(string? xpcServiceName)
    {
        Assert.True(LauncherLaunchdAutostart.MayReload(xpcServiceName));
    }

    [Fact]
    public void MayReload_InsideTheOtherAgentsJob_IsTrue()
    {
        // Only its OWN job is the trap: a process in the other agent's job is not killed by this bootout.
        Assert.True(LauncherLaunchdAutostart.MayReload(GatewayLaunchdAutostart.Label));
    }
}
