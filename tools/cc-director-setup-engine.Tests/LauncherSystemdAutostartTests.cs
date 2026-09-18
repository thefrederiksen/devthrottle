using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for <see cref="LauncherSystemdAutostart"/> - the pure systemd --user unit generation for the
/// CC Launcher on Linux. The systemctl interaction is not exercised here (it would mutate the real user
/// systemd session); these tests pin the unit format that the installer, the uninstaller, and the
/// launcher's own startup registration all rely on.
///
/// WHAT A PASSING TEST HERE DOES NOT PROVE. That a launcher actually starts at login on a Linux machine,
/// and that an idle Linux Director then installs a staged update unattended, is proven by a RUN on a real
/// Linux box - see the pull request. These tests prove the unit is written the way it is meant to be;
/// they cannot prove systemd does anything with it.
/// </summary>
public sealed class LauncherSystemdAutostartTests
{
    private const string Exe = "/home/tester/.local/share/cc-director/launcher/cc-launcher";

    [Fact]
    public void UnitContent_ContainsExecStartWithExeAndArguments()
    {
        var unit = LauncherSystemdAutostart.UnitContent(Exe, "--managed");

        Assert.Contains($"ExecStart=\"{Exe}\" --managed", unit);
        Assert.Contains("Description=DevThrottle Launcher", unit);
    }

    [Fact]
    public void UnitContent_StartsAtLoginViaDefaultTarget()
    {
        // WantedBy=default.target is the whole point: the unit is pulled in when the user session starts,
        // with no person present - the systemd analogue of the Run key and RunAtLoad. Without it the
        // launcher exists on disk and never runs, which is the defect this class was written to end.
        var unit = LauncherSystemdAutostart.UnitContent(Exe, null);

        Assert.Contains("[Install]", unit);
        Assert.Contains("WantedBy=default.target", unit);
    }

    [Fact]
    public void UnitContent_RestartsOnFailureOnly()
    {
        // Restart=on-failure resurrects the launcher after a crash but leaves a CLEAN exit exited - the
        // systemd analogue of launchd's KeepAlive SuccessfulExit=false. Restart=always would relaunch a
        // launcher that a self-update had deliberately stopped, and race the swap.
        var unit = LauncherSystemdAutostart.UnitContent(Exe, "--managed");

        Assert.Contains("Restart=on-failure", unit);
        Assert.DoesNotContain("Restart=always", unit);
    }

    [Fact]
    public void UnitContent_NoArguments_QuotesExeAlone()
    {
        var unit = LauncherSystemdAutostart.UnitContent(Exe, null);

        Assert.Contains($"ExecStart=\"{Exe}\"", unit);
        Assert.DoesNotContain("--managed", unit);
    }

    [Fact]
    public void UnitContent_QuotesExeSoASpacedPathStaysOneToken()
    {
        // A path with a space must stay one ExecStart token, or systemd would hand the launcher a torn path.
        var spaced = "/home/tester/My Apps/cc-launcher";
        var unit = LauncherSystemdAutostart.UnitContent(spaced, "--managed");

        Assert.Contains($"ExecStart=\"{spaced}\" --managed", unit);
    }

    [Fact]
    public void UnitContent_NamesNoDisplayBecauseTheLauncherIsHeadlessOnLinux()
    {
        // The Linux launcher runs headless by design, so its unit must not depend on a graphical session.
        // A unit that waited for one would not start on a server, and would make the launcher's startup
        // conditional on a desktop it does not need.
        var unit = LauncherSystemdAutostart.UnitContent(Exe, "--managed");

        Assert.DoesNotContain("graphical-session", unit);
        Assert.DoesNotContain("DISPLAY", unit);
    }

    [Fact]
    public void UnitContent_EmptyExe_Throws()
    {
        Assert.Throws<ArgumentException>(() => LauncherSystemdAutostart.UnitContent("", "--managed"));
    }

    [Fact]
    public void UnitPath_IsUserSystemdUnitWithTheUnitName()
    {
        Assert.EndsWith(
            Path.Combine(".config", "systemd", "user", "devthrottle-launcher.service"),
            LauncherSystemdAutostart.UnitPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnitName_DoesNotCollideWithTheGatewayUnit()
    {
        // Both are per-user units in the same directory. One name for one thing: a collision would mean
        // installing one silently replaced the other.
        Assert.NotEqual(GatewaySystemdAutostart.UnitName, LauncherSystemdAutostart.UnitName);
    }

    [Fact]
    public void Registered_NoUnitFile_IsNull()
    {
        // Reading the registered command line must not invent one when nothing is registered. The whole
        // Linux defect was a "not registered" state that read as normal; the answer has to be honest.
        if (File.Exists(LauncherSystemdAutostart.UnitPath)) return;   // a real unit on this machine; nothing to assert

        Assert.Null(LauncherSystemdAutostart.Registered());
        Assert.False(LauncherSystemdAutostart.IsRegistered());
    }
}
