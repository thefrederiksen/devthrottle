using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the Linux launcher install step. Every external effect is injected - systemctl, the stop of
/// already-running launchers, the unit path, the registration file and process liveness - so the real
/// decision flow runs on EVERY host. Nothing here returns early on a non-Linux machine: a Linux test that
/// skipped itself on Windows would pass by never running (see LinuxDesktopEntryLaunchTests).
///
/// WHAT THESE DO NOT PROVE: that systemd starts the unit at login. That is proven by a run on a real Linux
/// machine (issue #3091).
/// </summary>
public sealed class LauncherLinuxInstallerTests : IDisposable
{
    private const int SystemdPid = 4242;

    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _unitPath;
    private readonly string _registrationPath;
    private readonly List<string> _events = [];

    public LauncherLinuxInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-launcher-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        _unitPath = Path.Combine(_dir, "systemd", "user", LauncherSystemdAutostart.UnitName);
        _registrationPath = Path.Combine(_dir, "launcher.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string PlaceBinary()
    {
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "");
        return binary;
    }

    /// <summary>A fake systemd: enabled once enabled, running as <paramref name="mainPid"/> once restarted,
    /// and the launcher writes its registration naming <paramref name="registeredPid"/> when started.</summary>
    private (int, string) FakeSystemctl(string exe, string args, int mainPid = SystemdPid, int registeredPid = SystemdPid,
        bool restartWorks = true)
    {
        _events.Add($"{exe} {args}");
        if (args.Contains("is-enabled")) return _events.Any(e => e.Contains(" enable ")) ? (0, "enabled") : (1, "disabled");
        if (args.Contains(" restart "))
        {
            if (!restartWorks) return (1, "Job failed");
            File.WriteAllText(_registrationPath, $$"""{"pid":{{registeredPid}},"version":"2.21.0"}""");
            return (0, "");
        }
        if (args.Contains("MainPID")) return (0, _events.Any(e => e.Contains(" restart ")) ? $"{mainPid}\n" : "0\n");
        return (0, "");
    }

    private LauncherLinuxInstaller Installer(LauncherSystemdAutostart.CommandRunner run, bool stopWorks = true) =>
        new(_layout,
            runCommand: run,
            stopRunningLaunchers: () =>
            {
                _events.Add("stop running launchers");
                return new LauncherStopper.Result(stopWorks, [stopWorks ? "no launcher process running" : "pid 99 would not exit"]);
            },
            unitPath: _unitPath,
            registrationPath: _registrationPath,
            healthTimeout: TimeSpan.FromSeconds(2),
            mainPidWait: TimeSpan.FromSeconds(1),
            processIsAlive: _ => true);

    [Fact]
    public async Task BinaryMissing_Fails_AndTouchesNothing()
    {
        var result = await Installer((e, a) => FakeSystemctl(e, a)).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("not present", result.Message);
        Assert.False(File.Exists(_unitPath));
        Assert.DoesNotContain(_events, e => e.Contains(" restart "));
    }

    [Fact]
    public async Task FreshInstall_WritesTheUnit_StopsOldLaunchers_ThenStartsAndCertifiesTheSystemdProcess()
    {
        // The reviewer's defect on the first attempt at #3091: the unit was only ever written by the running
        // launcher, and nothing started the launcher. The installer must write it, enable it, and start it.
        var binary = PlaceBinary();

        var result = await Installer((e, a) => FakeSystemctl(e, a)).InstallAsync();

        Assert.True(result.Success, result.Message + "\n" + string.Join("\n", result.Steps));
        Assert.Equal(LauncherSystemdAutostart.UnitContent(binary, "--managed"), File.ReadAllText(_unitPath));
        var enable = _events.FindIndex(e => e.Contains($"--user enable {LauncherSystemdAutostart.UnitName}"));
        var stop = _events.IndexOf("stop running launchers");
        var restart = _events.FindIndex(e => e.Contains($"--user restart {LauncherSystemdAutostart.UnitName}"));
        Assert.True(enable >= 0 && enable < stop && stop < restart,
            "order must be: enable the unit, stop old launchers, then start: " + string.Join(" | ", _events));
        Assert.Contains(result.Steps, s => s.Contains($"process {SystemdPid}"));
    }

    [Fact]
    public async Task RegistrationNamesAnotherProcess_RefusesToCertify()
    {
        // An orphan launcher answering for the one systemd started is the case that certified a broken Mac.
        PlaceBinary();

        var result = await Installer((e, a) => FakeSystemctl(e, a, registeredPid: 777)).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("777", result.Message);
        Assert.Contains(SystemdPid.ToString(), result.Message);
    }

    [Fact]
    public async Task OldLauncherWillNotStop_FailsWithoutStartingANewOne()
    {
        // The old build holds the single-instance lock; a new one would exit at once and this would certify nothing.
        PlaceBinary();

        var result = await Installer((e, a) => FakeSystemctl(e, a), stopWorks: false).InstallAsync();

        Assert.False(result.Success);
        Assert.DoesNotContain(_events, e => e.Contains(" restart "));
        Assert.True(File.Exists(_unitPath), "the unit is still written, so the next login starts the launcher");
    }

    [Fact]
    public async Task SystemdWillNotStartIt_Fails_AndCarriesTheUnitStatus()
    {
        PlaceBinary();

        var result = await Installer((e, a) => FakeSystemctl(e, a, restartWorks: false)).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("systemd would not start the launcher", result.Message);
        Assert.NotNull(result.Diagnostics);
        Assert.Contains("systemctl --user status", result.Diagnostics);
    }

    [Fact]
    public async Task SystemdReportsNoProcess_Fails()
    {
        PlaceBinary();

        var result = await Installer((e, a) => FakeSystemctl(e, a, mainPid: 0)).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("did not report the launcher running", result.Message);
    }
}
