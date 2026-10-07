#pragma warning disable CA1416 // Rebuild is macOS-only in production; here launchctl is faked, so every branch runs on every operating system.
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The launch agent rebuild the installer and the Director run (#3411): one at a time across processes, the
/// replacement staged before launchd is touched, whatever launchd held booted out and confirmed gone by
/// launchd's own "could not find service", the definition written, bootstrapped and kickstarted, and any
/// failure past the bootout rolled back. launchctl is faked, so what is pinned here is the ORDER of what is
/// asked of launchd, what lands on disk, and the roll back.
/// </summary>
public class LauncherLaunchdRebuildTests : IDisposable
{
    private readonly string _dir;
    private readonly string _plist;
    private readonly string _logDir;

    public LauncherLaunchdRebuildTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-launchd-rebuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _plist = Path.Combine(_dir, "LaunchAgents", LauncherLaunchdAutostart.Label + ".plist");
        _logDir = Path.Combine(_dir, "logs", "launcher");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private const string Refused = "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n";
    private const string RunningAfter = "state = running\npid = 777\nruns = 7\n";
    private const string NotFound = "Could not find service \"com.devthrottle.cc-launcher\" in domain for user gui: 501";

    /// <summary>
    /// A launchd that behaves: print answers while the job is held, bootout releases it, a bootstrap of a job
    /// it already holds is refused (as the real one refuses), and after the kickstart it reports a process.
    /// The knobs make it misbehave in exactly one way per test.
    /// </summary>
    private sealed class FakeLaunchd(bool loaded)
    {
        public readonly List<string> Calls = [];
        public bool Held = loaded;
        public bool Started;
        public int BootoutExit;
        public bool BootoutReleases = true;
        public bool ConfirmationTimesOut;
        public int BootstrapExit;
        public bool BootstrapFailsOnce;
        public bool ThrowOnBootstrapOnce;
        public int KickstartExit;
        public TimeSpan BootstrapDelay = TimeSpan.Zero;
        private bool _bootstrapFailed;
        private bool _threw;
        private readonly object _gate = new();

        public (int Exit, string Output) Run(string exe, string args)
        {
            if (exe == "/bin/launchctl" && args.StartsWith("bootstrap ", StringComparison.Ordinal) && BootstrapDelay > TimeSpan.Zero)
                Thread.Sleep(BootstrapDelay); // outside the gate: the window in which two rebuilds could interleave
            lock (_gate)
            {
                Calls.Add($"{exe} {args}");
                if (exe == "/usr/bin/id") return (0, "501\n");
                if (exe != "/bin/launchctl") return (0, "");
                if (args.StartsWith("print ", StringComparison.Ordinal))
                {
                    var afterBootout = Calls.Any(c => c.Contains(" bootout ", StringComparison.Ordinal));
                    var afterBootstrap = Calls.Any(c => c.Contains(" bootstrap ", StringComparison.Ordinal));
                    if (ConfirmationTimesOut && afterBootout && !afterBootstrap) return (ProcessRunner.TimeoutExitCode, "TIMEOUT: '/bin/launchctl' exceeded 60s and was killed.");
                    return Held ? (0, Started ? RunningAfter : Refused) : (113, NotFound);
                }
                if (args.StartsWith("bootout ", StringComparison.Ordinal))
                {
                    if (BootoutReleases) { Held = false; Started = false; }
                    return (BootoutExit, BootoutExit == 0 ? "" : "Boot-out failed: 36: Operation now in progress");
                }
                if (args.StartsWith("bootstrap ", StringComparison.Ordinal))
                {
                    if (ThrowOnBootstrapOnce && !_threw) { _threw = true; throw new IOException("the runner broke"); }
                    if (BootstrapFailsOnce && !_bootstrapFailed) { _bootstrapFailed = true; return (5, "Bootstrap failed: 5: Input/output error"); }
                    if (BootstrapExit != 0) return (BootstrapExit, "Bootstrap failed: 5: Input/output error");
                    if (Held) return (37, "Bootstrap failed: 37: Operation already in progress");
                    Held = true;
                    return (0, "");
                }
                if (args.StartsWith("kickstart ", StringComparison.Ordinal))
                {
                    if (KickstartExit == 0) Started = true;
                    return (KickstartExit, KickstartExit == 0 ? "" : "Could not kickstart service: 125");
                }
                return (0, "");
            }
        }

        public List<string> Launchctl()
            => Calls.Where(c => c.StartsWith("/bin/launchctl", StringComparison.Ordinal)).Select(c => c.Split(' ')[1]).ToList();
    }

    private string[] StagingFiles() => Directory.Exists(Path.GetDirectoryName(_plist)!)
        ? Directory.GetFiles(Path.GetDirectoryName(_plist)!, "*.new")
        : [];

    [Fact]
    public void Rebuild_WithARefusedJobLoaded_BootsOutConfirmsWritesBootstrapsKickstartsAndLooksAgain()
    {
        var launchd = new FakeLaunchd(loaded: true);

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", "--managed", launchd.Run, _plist, _logDir);

        Assert.True(result.PreviousLoaded);
        Assert.Contains("78: EX_CONFIG", result.PreviousPrint);
        Assert.Equal(["print", "bootout", "print", "bootstrap", "kickstart", "print"], launchd.Launchctl());
        Assert.Contains($"/bin/launchctl bootout gui/501/{LauncherLaunchdAutostart.Label}", launchd.Calls);
        Assert.Contains($"/bin/launchctl kickstart -k gui/501/{LauncherLaunchdAutostart.Label}", launchd.Calls);
        Assert.True(File.Exists(_plist));
        Assert.Empty(StagingFiles());
        Assert.Equal(LauncherLaunchdAutostart.PlistContent("/tmp/x/cc-launcher", "--managed", _logDir), File.ReadAllText(_plist));
        Assert.True(Directory.Exists(_logDir));
        Assert.Contains(result.Steps, s => s.Contains("already held the job", StringComparison.Ordinal) && s.Contains("spawn failed", StringComparison.Ordinal));
        Assert.Contains("pid = 777", result.AfterPrint);
        Assert.Contains(result.Steps, s => s.StartsWith("launchd now reports:", StringComparison.Ordinal));
        Assert.True(launchd.Held && launchd.Started);
    }

    [Fact]
    public void Rebuild_WithNoJobLoaded_SkipsBootout()
    {
        var launchd = new FakeLaunchd(loaded: false);

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir);

        Assert.False(result.PreviousLoaded);
        Assert.Null(result.PreviousPrint);
        Assert.Equal(["print", "bootstrap", "kickstart", "print"], launchd.Launchctl());
    }

    [Fact]
    public void Rebuild_StagesTheNewDefinitionBeforeLaunchdIsTouched()
    {
        var sawStagedFileAtFirstLaunchctlCall = false;
        var launchctlCalls = 0;
        LauncherLaunchdAutostart.CommandRunner run = (exe, args) =>
        {
            if (exe == "/bin/launchctl" && launchctlCalls++ == 0) sawStagedFileAtFirstLaunchctlCall = StagingFiles().Length == 1;
            if (exe == "/usr/bin/id") return (0, "501");
            if (args.StartsWith("print ", StringComparison.Ordinal)) return (113, NotFound);
            return (0, "");
        };

        LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, run, _plist, _logDir);

        Assert.True(sawStagedFileAtFirstLaunchctlCall, "the replacement must be on disk before launchd is asked anything");
        Assert.Empty(StagingFiles());
    }

    [Fact]
    public void Rebuild_WhenBootoutLeavesTheJobHeld_ThrowsAndChangesNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
        var launchd = new FakeLaunchd(loaded: true) { BootoutExit = 36, BootoutReleases = false };

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir));

        Assert.Contains("launchd still holds the job", ex.Message);
        Assert.Contains("nothing was changed", ex.Message);
        Assert.Equal(["print", "bootout", "print"], launchd.Launchctl());
        Assert.Equal("<old/>", File.ReadAllText(_plist));
        Assert.Empty(StagingFiles());
        Assert.True(launchd.Held);
    }

    [Fact]
    public void Rebuild_WhenBootoutAnswersNonZeroButLaunchdNoLongerKnowsTheJob_Continues()
    {
        var launchd = new FakeLaunchd(loaded: true) { BootoutExit = 36 };

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir);

        Assert.Contains(result.Steps, s => s.Contains("launchd no longer holds the job", StringComparison.Ordinal));
        Assert.Equal(["print", "bootout", "print", "bootstrap", "kickstart", "print"], launchd.Launchctl());
    }

    [Fact]
    public void Rebuild_WhenTheConfirmationPrintTimesOut_ReloadsThePreviousAgentAndStops()
    {
        // A timeout is not "could not find service": the old job may or may not be gone, so the old
        // definition is bootstrapped again and nothing on disk changes.
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
        var launchd = new FakeLaunchd(loaded: true) { ConfirmationTimesOut = true };

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir));

        Assert.Contains("could not confirm the old job was booted out", ex.Message);
        Assert.Contains("bootstrapped again (exit 0", ex.Message);
        Assert.Equal(["print", "bootout", "print", "bootstrap"], launchd.Launchctl());
        Assert.Equal("<old/>", File.ReadAllText(_plist));
        Assert.Empty(StagingFiles());
        Assert.True(launchd.Held, "the previous job is loaded again");
    }

    [Fact]
    public void Rebuild_WhenBootstrapFails_RollsBackAndReloadsThePreviousAgent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
        var launchd = new FakeLaunchd(loaded: true) { BootstrapFailsOnce = true }; // the new definition is refused, the old one accepted

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir));

        Assert.Contains("bootstrap failed (exit 5)", ex.Message);
        Assert.Contains("Rolled back: the previous launch agent is back on disk and loaded again.", ex.Message);
        Assert.Equal("<old/>", File.ReadAllText(_plist));
        Assert.Empty(StagingFiles());
        // print, bootout, print (gone), bootstrap (refused), bootstrap again (the roll back of the old file)
        Assert.Equal(["print", "bootout", "print", "bootstrap", "bootstrap"], launchd.Launchctl());
        Assert.True(launchd.Held, "launchd holds the previous job again");
        Assert.Contains("78: EX_CONFIG", ex.PreviousPrint);
        Assert.Contains(ex.Steps, s => s == "booted the old job out");
    }

    [Fact]
    public void Rebuild_WhenBootstrapFailsAndThereWasNoAgentBefore_RemovesTheNewFile()
    {
        var launchd = new FakeLaunchd(loaded: false) { BootstrapExit = 5 };

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir));

        Assert.Contains("there was none before", ex.Message);
        Assert.False(File.Exists(_plist));
        Assert.Empty(StagingFiles());
        Assert.Equal(["print", "bootstrap"], launchd.Launchctl());
    }

    [Fact]
    public void Rebuild_WhenTheRunnerThrowsAfterTheBootout_RollsBackAndKeepsTheSteps()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
        var launchd = new FakeLaunchd(loaded: true) { ThrowOnBootstrapOnce = true };

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir));

        Assert.Contains("the runner broke", ex.Message);
        Assert.Contains("Rolled back", ex.Message);
        Assert.Equal("<old/>", File.ReadAllText(_plist));
        Assert.Empty(StagingFiles());
        Assert.True(launchd.Held, "the previous job is loaded again");
        Assert.Contains(ex.Steps, s => s == "booted the old job out");
        Assert.Contains(ex.Steps, s => s == "wrote the launch agent");
    }

    [Fact]
    public void Rebuild_WhenKickstartAnswersNonZero_RecordsItAndStillAsksLaunchdWhatItHolds()
    {
        // On a real Mac a kickstart of a program macOS refuses hangs until it is killed; the exit code says
        // nothing reliable, so it is recorded and launchd's answer afterwards is what the callers read.
        var launchd = new FakeLaunchd(loaded: false) { KickstartExit = 125 };

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir);

        Assert.Contains(result.Steps, s => s.Contains("kickstart answered exit 125", StringComparison.Ordinal));
        Assert.Equal(["print", "bootstrap", "kickstart", "print"], launchd.Launchctl());
        Assert.DoesNotContain("pid =", result.AfterPrint ?? "");
    }

    [Fact]
    public void Rebuild_InsideTheLaunchersOwnJob_RefusesBeforeTouchingAnything()
    {
        var before = Environment.GetEnvironmentVariable("XPC_SERVICE_NAME");
        Environment.SetEnvironmentVariable("XPC_SERVICE_NAME", LauncherLaunchdAutostart.Label);
        try
        {
            var launchd = new FakeLaunchd(loaded: true);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, launchd.Run, _plist, _logDir));

            Assert.Contains("#3575", ex.Message);
            Assert.Empty(launchd.Calls);
            Assert.False(File.Exists(_plist));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XPC_SERVICE_NAME", before);
        }
    }

    [Fact]
    public void Rebuild_TwoAtOnce_TakeTurnsAndBothLeaveOneValidLoadedAgent()
    {
        // The setup wizard and a running Director can both rebuild. Without the lock, the second would see the
        // first's job gone, bootstrap its own, and the first's bootstrap would be refused ("already in
        // progress") and rolled back - launchd's answer in the fake for a bootstrap of a job it already holds.
        var launchd = new FakeLaunchd(loaded: true) { BootstrapDelay = TimeSpan.FromMilliseconds(150) };
        using var start = new Barrier(2);
        var failures = new List<Exception>();
        var worker = () =>
        {
            start.SignalAndWait();
            try { LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", "--managed", launchd.Run, _plist, _logDir); }
            catch (Exception ex) { lock (failures) failures.Add(ex); }
        };
        var a = new Thread(() => worker());
        var b = new Thread(() => worker());
        a.Start(); b.Start();
        a.Join(); b.Join();

        Assert.Empty(failures);
        Assert.Equal(LauncherLaunchdAutostart.PlistContent("/tmp/x/cc-launcher", "--managed", _logDir), File.ReadAllText(_plist));
        Assert.Empty(StagingFiles());
        Assert.True(launchd.Held && launchd.Started);
        Assert.Equal(2, launchd.Launchctl().Count(v => v == "bootout"));
        Assert.Equal(2, launchd.Launchctl().Count(v => v == "bootstrap"));
    }

    [Fact]
    public void ServiceNotFound_IsOnlyLaunchdsOwnAnswer()
    {
        Assert.True(LauncherLaunchdAutostart.ServiceNotFound(113, ""));
        Assert.True(LauncherLaunchdAutostart.ServiceNotFound(1, NotFound));
        Assert.False(LauncherLaunchdAutostart.ServiceNotFound(ProcessRunner.TimeoutExitCode, "TIMEOUT"));
        Assert.False(LauncherLaunchdAutostart.ServiceNotFound(1, "Could not print domain: 1: Operation not permitted"));
        Assert.False(LauncherLaunchdAutostart.ServiceNotFound(0, Refused));
    }

    [Fact]
    public void AnswersVersionFlag_OnlyFromTheBuildThatHasIt()
    {
        Assert.True(LauncherMacInstaller.AnswersVersionFlag("2.17.0"));
        Assert.True(LauncherMacInstaller.AnswersVersionFlag("2.17.0+abc123"));
        Assert.True(LauncherMacInstaller.AnswersVersionFlag("3.0.0"));
        Assert.False(LauncherMacInstaller.AnswersVersionFlag("2.16.0"));
        Assert.False(LauncherMacInstaller.AnswersVersionFlag("2.15.0+16d267109"));
        Assert.False(LauncherMacInstaller.AnswersVersionFlag(null));
        Assert.False(LauncherMacInstaller.AnswersVersionFlag("unknown"));
    }
}
