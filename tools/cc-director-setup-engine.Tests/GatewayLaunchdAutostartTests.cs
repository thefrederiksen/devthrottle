using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for <see cref="GatewayLaunchdAutostart"/> - the pure property-list generation.
/// The launchctl interaction is not exercised here (it would mutate the real launchd gui
/// domain); these tests pin the plist format the Gateway, installer, and uninstaller all
/// rely on. That the agent actually brings the Gateway up after a reboot is proven by a
/// RUN on the Mac mini, recorded in the QA document - a passing test is not that evidence.
/// </summary>
public sealed class GatewayLaunchdAutostartTests
{
    private const string Exe = "/Users/tester/Library/Application Support/cc-director/gateway/CcDirector.Gateway";
    private const string LogDir = "/Users/tester/Library/Application Support/cc-director/logs/gateway";

    [Fact]
    public void PlistContent_ContainsLabelAndProgramArguments()
    {
        var plist = GatewayLaunchdAutostart.PlistContent(Exe, "--port 7878", LogDir);

        Assert.Contains("<string>com.devthrottle.cc-director-gateway</string>", plist);
        Assert.Contains($"<string>{Exe}</string>", plist);
        Assert.Contains("<string>--port</string>", plist);
        Assert.Contains("<string>7878</string>", plist);
    }

    [Fact]
    public void PlistContent_RunsAtLoad()
    {
        // RunAtLoad is the whole point of this class: start at login, with no person present.
        var plist = GatewayLaunchdAutostart.PlistContent(Exe, null, LogDir);

        Assert.Contains("<key>RunAtLoad</key>", plist);
    }

    [Fact]
    public void PlistContent_KeepAliveOnlyAfterUnsuccessfulExit()
    {
        // KeepAlive must be conditional on SuccessfulExit=false: a clean exit (POST /shutdown)
        // must STAY exited, or launchd would relaunch a Gateway that was deliberately stopped;
        // only a crash may be resurrected.
        var plist = GatewayLaunchdAutostart.PlistContent(Exe, "--port 7878", LogDir);

        Assert.Contains("<key>KeepAlive</key>", plist);
        Assert.Contains("<key>SuccessfulExit</key>", plist);
        var keepAliveIndex = plist.IndexOf("<key>KeepAlive</key>", StringComparison.Ordinal);
        var successfulExitIndex = plist.IndexOf("<key>SuccessfulExit</key>", StringComparison.Ordinal);
        Assert.True(successfulExitIndex > keepAliveIndex, "SuccessfulExit must live inside the KeepAlive dictionary");
    }

    [Fact]
    public void PlistContent_NoArguments_OmitsExtraStrings()
    {
        var plist = GatewayLaunchdAutostart.PlistContent(Exe, null, LogDir);

        Assert.DoesNotContain("--port", plist);
    }

    [Fact]
    public void PlistContent_KeepsQuotedArgumentWithSpacesAsOneToken()
    {
        // A value containing spaces (a path under "Application Support") must survive as ONE
        // ProgramArguments token; splitting it at the space would hand the Gateway a wrong path.
        var plist = GatewayLaunchdAutostart.PlistContent(
            Exe, "--config \"/Users/me/Application Support/gateway.json\"", LogDir);

        Assert.Contains("<string>--config</string>", plist);
        Assert.Contains("<string>/Users/me/Application Support/gateway.json</string>", plist);
        // not torn at the embedded space
        Assert.DoesNotContain("<string>Support/gateway.json</string>", plist);
        Assert.DoesNotContain("<string>Application</string>", plist);
    }

    [Fact]
    public void PlistContent_EscapesXmlCharacters()
    {
        var plist = GatewayLaunchdAutostart.PlistContent("/tmp/a&b/CcDirector.Gateway", null, "/tmp/a&b/logs");

        Assert.Contains("/tmp/a&amp;b/CcDirector.Gateway", plist);
        Assert.DoesNotContain("<string>/tmp/a&b/CcDirector.Gateway</string>", plist);
    }

    [Fact]
    public void PlistContent_WritesLogPathsUnderLogDir()
    {
        var plist = GatewayLaunchdAutostart.PlistContent(Exe, "--port 7878", LogDir);

        Assert.Contains("launchd-stdout.log", plist);
        Assert.Contains("launchd-stderr.log", plist);
    }

    [Fact]
    public void PlistContent_EmptyExe_Throws()
    {
        Assert.Throws<ArgumentException>(() => GatewayLaunchdAutostart.PlistContent("", "--port 7878", LogDir));
    }

    [Fact]
    public void PlistPath_IsUserLaunchAgentWithLabelFileName()
    {
        Assert.EndsWith(
            Path.Combine("Library", "LaunchAgents", "com.devthrottle.cc-director-gateway.plist"),
            GatewayLaunchdAutostart.PlistPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Label_DoesNotCollideWithTheLauncherAgent()
    {
        // Two separate agents live in the same gui domain. One label, one plist file name each -
        // a collision would have each bootout the other.
        Assert.NotEqual(LauncherLaunchdAutostart.Label, GatewayLaunchdAutostart.Label);
        Assert.NotEqual(LauncherLaunchdAutostart.PlistPath, GatewayLaunchdAutostart.PlistPath);
    }

    [Fact]
    public void MayReload_InsideItsOwnJob_IsFalse()
    {
        // launchd sets XPC_SERVICE_NAME to the job label in the job's processes. A bootout from
        // there kills the caller before it can bootstrap, and the job is gone until the next
        // login (product #3575).
        Assert.False(GatewayLaunchdAutostart.MayReload(GatewayLaunchdAutostart.Label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application.com.apple.Terminal.12345")]
    public void MayReload_OutsideTheJob_IsTrue(string? xpcServiceName)
    {
        Assert.True(GatewayLaunchdAutostart.MayReload(xpcServiceName));
    }

    [Fact]
    public void MayReload_InsideTheOtherAgentsJob_IsTrue()
    {
        // Only its OWN job is the trap: a process in the other agent's job is not killed by this bootout.
        Assert.True(GatewayLaunchdAutostart.MayReload(LauncherLaunchdAutostart.Label));
    }
}
