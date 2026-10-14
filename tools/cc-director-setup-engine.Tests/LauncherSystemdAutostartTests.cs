using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for <see cref="LauncherSystemdAutostart"/> - the pure systemd --user unit generation for the
/// CC Launcher on Linux. The real systemctl is never called (it would mutate the user's systemd session);
/// the command runner is injected, so these pin the unit format and the systemctl sequence that the
/// installer, the uninstaller and the launcher's own startup registration all rely on - on every host,
/// because the platform is never read from the machine.
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
    public void UnitContent_StartsWithTheDesktopSessionAndEndsWithIt()
    {
        // WantedBy is the whole point: the unit is pulled in at login with no person present - the systemd
        // analogue of the Run key and RunAtLoad. Without it the launcher exists on disk and never runs, which is
        // the defect this class was written to end (#3091).
        //
        // graphical-session.target, NOT default.target: the launcher starts the Director, a windowed app that
        // inherits the launcher's environment. default.target is reached before the desktop has handed DISPLAY to
        // the user manager, so a Director started from there has no display and is rolled back as a bad build.
        var unit = LauncherSystemdAutostart.UnitContent(Exe, null);

        Assert.Contains("[Install]", unit);
        Assert.Contains("WantedBy=graphical-session.target", unit);
        Assert.Contains("After=graphical-session.target", unit);
        Assert.Contains("PartOf=graphical-session.target", unit);
        Assert.DoesNotContain("default.target", unit);
    }

    [Fact]
    public void UnitContent_StopsOnlyTheLauncher_NeverTheDirectorItStarted()
    {
        // The launcher starts the Director as its child, so the Director is in the launcher unit's control group.
        // systemd's default KillMode (control-group) would kill the Director and every session in it each time the
        // launcher restarts for its own update. Only the launcher may be stopped.
        var unit = LauncherSystemdAutostart.UnitContent(Exe, "--managed");

        Assert.Contains("KillMode=process", unit);
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

    [Fact]
    public void Arguments_AreTheSameOnesTheLauncherRegistersForItself()
    {
        // One argument format for every writer. If the installer wrote one thing and the running launcher
        // another, each start would rewrite the unit the other had written.
        Assert.Equal("--managed", LauncherSystemdAutostart.Arguments);
        Assert.Equal(LauncherTrayInstaller.InstalledArguments, LauncherSystemdAutostart.Arguments);
    }

    [Fact]
    public void EnsureRegistered_WritesTheUnit_ReloadsAndEnables_ButNeverStartsIt()
    {
        // The running launcher calls this on every start. "enable --now" from a launcher systemd did not start
        // would start a SECOND launcher beside it, so registering must only enable; starting is the installer's.
        var dir = TempDir();
        var unitPath = Path.Combine(dir, LauncherSystemdAutostart.UnitName);
        var calls = new List<string>();

        var changed = LauncherSystemdAutostart.EnsureRegistered(Exe, "--managed",
            (exe, args) => { calls.Add($"{exe} {args}"); return args.Contains("is-enabled") ? (1, "disabled") : (0, ""); },
            unitPath);

        Assert.True(changed);
        Assert.Equal(LauncherSystemdAutostart.UnitContent(Exe, "--managed"), File.ReadAllText(unitPath));
        Assert.Contains("systemctl --user daemon-reload", calls);
        Assert.Contains($"systemctl --user enable {LauncherSystemdAutostart.UnitName}", calls);
        Assert.DoesNotContain(calls, c => c.Contains("--now") || c.Contains(" start ") || c.Contains(" restart "));
        Assert.Equal($"\"{Exe}\" --managed", LauncherSystemdAutostart.Registered(unitPath));
    }

    [Fact]
    public void EnsureRegistered_AlreadyCorrectAndEnabled_WritesNothing()
    {
        var dir = TempDir();
        var unitPath = Path.Combine(dir, LauncherSystemdAutostart.UnitName);
        File.WriteAllText(unitPath, LauncherSystemdAutostart.UnitContent(Exe, "--managed"));
        var calls = new List<string>();

        var changed = LauncherSystemdAutostart.EnsureRegistered(Exe, "--managed",
            (exe, args) => { calls.Add(args); return (0, "enabled"); }, unitPath);

        Assert.False(changed);
        Assert.Equal([$"--user is-enabled {LauncherSystemdAutostart.UnitName}"], calls);
    }

    [Fact]
    public void EnsureRegistered_EnableRefused_Throws()
    {
        // A refused enable is a launcher that will not start at login. It must not read as success.
        var dir = TempDir();
        var unitPath = Path.Combine(dir, LauncherSystemdAutostart.UnitName);

        Assert.Throws<InvalidOperationException>(() => LauncherSystemdAutostart.EnsureRegistered(Exe, "--managed",
            (_, args) => args.Contains(" enable ") || args.Contains("is-enabled") ? (1, "Failed to connect to bus") : (0, ""),
            unitPath));
    }

    [Fact]
    public void UnregisterVerified_DisablesStopsAndRemovesTheUnit()
    {
        var dir = TempDir();
        var unitPath = Path.Combine(dir, LauncherSystemdAutostart.UnitName);
        File.WriteAllText(unitPath, LauncherSystemdAutostart.UnitContent(Exe, "--managed"));
        var calls = new List<string>();

        var ok = LauncherSystemdAutostart.UnregisterVerified((_, args) => { calls.Add(args); return (0, ""); }, unitPath, out var failure);

        Assert.True(ok);
        Assert.Null(failure);
        Assert.False(File.Exists(unitPath));
        Assert.Contains($"--user disable --now {LauncherSystemdAutostart.UnitName}", calls);
    }

    [Fact]
    public void UnregisterVerified_StillEnabledAfterDisable_ReportsTheFailure()
    {
        var dir = TempDir();
        var unitPath = Path.Combine(dir, LauncherSystemdAutostart.UnitName);
        File.WriteAllText(unitPath, LauncherSystemdAutostart.UnitContent(Exe, "--managed"));

        var ok = LauncherSystemdAutostart.UnregisterVerified(
            (_, args) => args.Contains("disable") ? (1, "Access denied") : args.Contains("is-enabled") ? (0, "enabled") : (0, ""),
            unitPath, out var failure);

        Assert.False(ok);
        Assert.Contains("Access denied", failure);
    }

    [Theory]
    [InlineData("4242\n", 4242)]
    [InlineData("MainPID=4242", 4242)]
    [InlineData("0\n", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("garbage", 0)]
    public void ParseMainPid_ReadsTheNumberOrZero(string? output, int expected)
    {
        // 0 is how systemd says "no process", and it must stay "no process" - never certify a launcher that
        // is not running.
        Assert.Equal(expected, LauncherSystemdAutostart.ParseMainPid(output));
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-launcher-systemd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
