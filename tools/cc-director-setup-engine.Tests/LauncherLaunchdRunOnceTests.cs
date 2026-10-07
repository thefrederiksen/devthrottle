#pragma warning disable CA1416 // RunOnce is macOS-only in production; here every effect is injected, so it runs on every operating system.
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Director's repair, end to end with launchd faked (#3411): a refused job is rebuilt and success is
/// claimed only when launchd reports a process, within the wait and not before; a job that never shows one
/// is a FAILED line; a switched-off launcher, or a disabled list that cannot be read, is left alone.
/// </summary>
public class LauncherLaunchdRunOnceTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _plist;

    public LauncherLaunchdRunOnceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-launchd-runonce-" + Guid.NewGuid().ToString("N"));
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "");
        _plist = Path.Combine(_dir, "LaunchAgents", LauncherLaunchdAutostart.Label + ".plist");
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private const string Refused = "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n";
    private const string NotFound = "Could not find service \"com.devthrottle.cc-launcher\" in domain for user gui: 501";
    private const string NothingDisabled = "disabled services = {\n}\n";

    /// <summary>launchd faked: the job is held and refused; after the rebuild's kickstart it shows a process only
    /// after <paramref name="printsUntilProcess"/> more looks, or never when that is negative.</summary>
    private static LauncherLaunchdAutostart.CommandRunner Fake(int printsUntilProcess, string disabledList = NothingDisabled, int disabledExit = 0)
    {
        var held = true;
        var kickstarted = false;
        var looksAfterKickstart = 0;
        return (exe, args) =>
        {
            if (exe == "/usr/bin/id") return (0, "501\n");
            if (exe != "/bin/launchctl") return (0, "");
            if (args.StartsWith("print-disabled", StringComparison.Ordinal)) return (disabledExit, disabledList);
            if (args.StartsWith("print ", StringComparison.Ordinal))
            {
                if (!held) return (113, NotFound);
                if (!kickstarted) return (0, Refused);
                looksAfterKickstart++;
                return printsUntilProcess >= 0 && looksAfterKickstart > printsUntilProcess
                    ? (0, "state = running\npid = 4242\nruns = 7\n")
                    : (0, "state = spawn scheduled\nruns = 7\nlast exit code = 78: EX_CONFIG\n");
            }
            if (args.StartsWith("bootout ", StringComparison.Ordinal)) { held = false; return (0, ""); }
            if (args.StartsWith("bootstrap ", StringComparison.Ordinal)) { held = true; return (0, ""); }
            if (args.StartsWith("kickstart ", StringComparison.Ordinal)) { kickstarted = true; return (0, ""); }
            return (0, "");
        };
    }

    private string Run(LauncherLaunchdAutostart.CommandRunner run, TimeSpan? wait = null)
        => LauncherLaunchdRepair.RunOnce(_layout, run, wait ?? TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(20), () => 0, _plist);

    [Fact]
    public void RunOnce_RefusedJob_RebuildsAndReportsTheProcessLaunchdShowsLater()
    {
        // The process appears on the fourth look after the kickstart - later than the first answer, well
        // within the wait. A repair that believed the first answer would call this a failure.
        var line = Run(Fake(printsUntilProcess: 4));

        Assert.StartsWith("Repair: launchd refused to spawn the launcher", line);
        Assert.Contains("Rebuilt the launch agent", line);
        Assert.Contains("launchd reports the launcher running as process 4242", line);
        Assert.Equal(LauncherLaunchdAutostart.PlistContent(_layout.PathFor(ComponentRegistry.Launcher), LauncherTrayInstaller.InstalledArguments, Path.Combine(_layout.LogsDir, "launcher")), File.ReadAllText(_plist));
    }

    [Fact]
    public void RunOnce_NoProcessWithinTheWait_IsAFailedLineWithLaunchdsAnswer()
    {
        var line = Run(Fake(printsUntilProcess: -1), TimeSpan.FromMilliseconds(300));

        Assert.StartsWith("FAILED to start the launcher after rebuilding the launch agent", line);
        Assert.Contains("78: EX_CONFIG", line);
        Assert.Contains("never ran", line);
    }

    [Fact]
    public void RunOnce_LauncherSwitchedOff_IsLeftAlone()
    {
        var line = Run(Fake(printsUntilProcess: 0, disabledList: "disabled services = {\n\t\"com.devthrottle.cc-launcher\" => disabled\n}\n"));

        Assert.StartsWith("Disabled:", line);
        Assert.Equal("<old/>", File.ReadAllText(_plist));
    }

    [Fact]
    public void RunOnce_DisabledListUnreadable_IsLeftAlone()
    {
        var line = Run(Fake(printsUntilProcess: 0, disabledList: "", disabledExit: 1));

        Assert.StartsWith("Unknown:", line);
        Assert.Equal("<old/>", File.ReadAllText(_plist));
    }

    [Fact]
    public void RunOnce_DisabledListTruncated_IsLeftAlone()
    {
        var line = Run(Fake(printsUntilProcess: 0, disabledList: "disabled services = {\n\t\"com.apple.x\" => disabled\n"));

        Assert.StartsWith("Unknown:", line);
    }

    [Fact]
    public void RunOnce_LauncherProcessAlreadyRunning_DoesNothing()
    {
        var line = LauncherLaunchdRepair.RunOnce(_layout, Fake(printsUntilProcess: 0), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), () => 1, _plist);

        Assert.StartsWith("Running:", line);
        Assert.Equal("<old/>", File.ReadAllText(_plist));
    }
}
