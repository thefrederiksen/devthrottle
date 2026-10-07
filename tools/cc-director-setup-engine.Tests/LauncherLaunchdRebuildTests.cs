#pragma warning disable CA1416 // Rebuild is macOS-only in production; here launchctl is faked, so every branch runs on every operating system.
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The launch agent rebuild the installer and the Director run (#3411): the replacement is staged before
/// launchd is touched, whatever launchd held is booted out and confirmed gone, the definition is written,
/// bootstrapped and kickstarted, and a refusal at any step leaves the Mac as it was. launchctl is faked, so
/// what is pinned here is the ORDER of what is asked of launchd, what lands on disk, and the roll back.
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

    /// <summary>
    /// A launchd that behaves: print answers while the job is held, bootout releases it, bootstrap takes
    /// it back, and after the kickstart it reports a process.
    /// </summary>
    private static LauncherLaunchdAutostart.CommandRunner Fake(List<string> calls, bool loaded,
        int bootoutExit = 0, int bootstrapExit = 0, int kickstartExit = 0, bool bootoutReleases = true)
    {
        var held = loaded;
        var started = false;
        return (exe, args) =>
        {
            calls.Add($"{exe} {args}");
            if (exe == "/usr/bin/id") return (0, "501\n");
            if (exe != "/bin/launchctl") return (0, "");
            if (args.StartsWith("print ", StringComparison.Ordinal))
                return held ? (0, started ? RunningAfter : Refused) : (113, "Could not find service");
            if (args.StartsWith("bootout ", StringComparison.Ordinal))
            {
                if (bootoutReleases) held = false;
                return (bootoutExit, bootoutExit == 0 ? "" : "Boot-out failed: 36: Operation now in progress");
            }
            if (args.StartsWith("bootstrap ", StringComparison.Ordinal))
            {
                if (bootstrapExit == 0) held = true;
                return (bootstrapExit, bootstrapExit == 0 ? "" : "Bootstrap failed: 5: Input/output error");
            }
            if (args.StartsWith("kickstart ", StringComparison.Ordinal))
            {
                if (kickstartExit == 0) started = true;
                return (kickstartExit, kickstartExit == 0 ? "" : "Could not kickstart service: 125");
            }
            return (0, "");
        };
    }

    private static List<string> Launchctl(List<string> calls)
        => calls.Where(c => c.StartsWith("/bin/launchctl", StringComparison.Ordinal)).Select(c => c.Split(' ')[1]).ToList();

    [Fact]
    public void Rebuild_WithARefusedJobLoaded_BootsOutConfirmsWritesBootstrapsKickstartsAndLooksAgain()
    {
        var calls = new List<string>();

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", "--managed", Fake(calls, loaded: true), _plist, _logDir);

        Assert.True(result.PreviousLoaded);
        Assert.Contains("78: EX_CONFIG", result.PreviousPrint);
        Assert.Equal(["print", "bootout", "print", "bootstrap", "kickstart", "print"], Launchctl(calls));
        Assert.Contains($"/bin/launchctl bootout gui/501/{LauncherLaunchdAutostart.Label}", calls);
        Assert.Contains($"/bin/launchctl kickstart -k gui/501/{LauncherLaunchdAutostart.Label}", calls);
        Assert.True(File.Exists(_plist));
        Assert.False(File.Exists(_plist + ".new"), "the staged copy is gone once it has taken the old file's place");
        Assert.Equal(LauncherLaunchdAutostart.PlistContent("/tmp/x/cc-launcher", "--managed", _logDir), File.ReadAllText(_plist));
        Assert.True(Directory.Exists(_logDir));
        Assert.Contains(result.Steps, s => s.Contains("already held the job", StringComparison.Ordinal) && s.Contains("spawn failed", StringComparison.Ordinal));
        Assert.Contains("pid = 777", result.AfterPrint);
        Assert.Contains(result.Steps, s => s.StartsWith("launchd now reports:", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebuild_WithNoJobLoaded_SkipsBootout()
    {
        var calls = new List<string>();

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: false), _plist, _logDir);

        Assert.False(result.PreviousLoaded);
        Assert.Null(result.PreviousPrint);
        Assert.Equal(["print", "bootstrap", "kickstart", "print"], Launchctl(calls));
    }

    [Fact]
    public void Rebuild_StagesTheNewDefinitionBeforeLaunchdIsTouched()
    {
        var calls = new List<string>();
        var sawStagedFileAtFirstLaunchctlCall = false;
        LauncherLaunchdAutostart.CommandRunner run = (exe, args) =>
        {
            if (exe == "/bin/launchctl" && calls.Count == 0) sawStagedFileAtFirstLaunchctlCall = File.Exists(_plist + ".new");
            if (exe == "/bin/launchctl") calls.Add(args);
            if (exe == "/usr/bin/id") return (0, "501");
            if (args.StartsWith("print ", StringComparison.Ordinal)) return (113, "");
            return (0, "");
        };

        LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, run, _plist, _logDir);

        Assert.True(sawStagedFileAtFirstLaunchctlCall, "the replacement must be on disk before launchd is asked anything");
    }

    [Fact]
    public void Rebuild_WhenBootoutLeavesTheJobHeld_ThrowsAndChangesNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
        var calls = new List<string>();

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: true, bootoutExit: 36, bootoutReleases: false), _plist, _logDir));

        Assert.Contains("launchd still holds the job", ex.Message);
        Assert.Contains("nothing was changed", ex.Message);
        Assert.Equal(["print", "bootout", "print"], Launchctl(calls));
        Assert.Equal("<old/>", File.ReadAllText(_plist));
        Assert.False(File.Exists(_plist + ".new"));
    }

    [Fact]
    public void Rebuild_WhenBootoutAnswersNonZeroButTheJobIsGone_Continues()
    {
        var calls = new List<string>();

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: true, bootoutExit: 36), _plist, _logDir);

        Assert.Contains(result.Steps, s => s.Contains("launchd no longer holds the job", StringComparison.Ordinal));
        Assert.Equal(["print", "bootout", "print", "bootstrap", "kickstart", "print"], Launchctl(calls));
    }

    [Fact]
    public void Rebuild_WhenBootstrapFails_ThrowsAndRollsBackToThePreviousLoadedAgent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
        var calls = new List<string>();

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: true, bootstrapExit: 5), _plist, _logDir));

        Assert.Contains("bootstrap failed (exit 5)", ex.Message);
        Assert.Contains("Rolled back", ex.Message);
        Assert.Equal("<old/>", File.ReadAllText(_plist));
        Assert.False(File.Exists(_plist + ".new"));
        // print, bootout, print (gone), bootstrap (refused), bootstrap again (the roll back of the old file)
        Assert.Equal(["print", "bootout", "print", "bootstrap", "bootstrap"], Launchctl(calls));
    }

    [Fact]
    public void Rebuild_WhenBootstrapFailsAndThereWasNoAgentBefore_RemovesTheNewFile()
    {
        var calls = new List<string>();

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: false, bootstrapExit: 5), _plist, _logDir));

        Assert.Contains("there was none before", ex.Message);
        Assert.False(File.Exists(_plist));
        Assert.Equal(["print", "bootstrap"], Launchctl(calls));
    }

    [Fact]
    public void Rebuild_WhenKickstartAnswersNonZero_RecordsItAndStillAsksLaunchdWhatItHolds()
    {
        // On a real Mac a kickstart of a program macOS refuses hangs until it is killed; the exit code says
        // nothing reliable, so it is recorded and launchd's answer afterwards is what the callers read.
        var calls = new List<string>();

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: false, kickstartExit: 125), _plist, _logDir);

        Assert.Contains(result.Steps, s => s.Contains("kickstart answered exit 125", StringComparison.Ordinal));
        Assert.Equal(["print", "bootstrap", "kickstart", "print"], Launchctl(calls));
        Assert.DoesNotContain("pid =", result.AfterPrint ?? "");
    }

    [Fact]
    public void Rebuild_WhenItFails_TheExceptionCarriesTheStepsAndThePreviousAnswer()
    {
        var calls = new List<string>();

        var ex = Assert.Throws<LauncherLaunchdAutostart.RebuildException>(() =>
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: true, bootstrapExit: 5), _plist, _logDir));

        Assert.Contains("78: EX_CONFIG", ex.PreviousPrint);
        Assert.Contains(ex.Steps, s => s.Contains("already held the job", StringComparison.Ordinal));
        Assert.Contains(ex.Steps, s => s == "booted the old job out");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void Rebuild_InsideTheLaunchersOwnJob_RefusesBeforeTouchingAnything()
    {
        var before = Environment.GetEnvironmentVariable("XPC_SERVICE_NAME");
        Environment.SetEnvironmentVariable("XPC_SERVICE_NAME", LauncherLaunchdAutostart.Label);
        try
        {
            var calls = new List<string>();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: true), _plist, _logDir));

            Assert.Contains("#3575", ex.Message);
            Assert.Empty(calls);
            Assert.False(File.Exists(_plist));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XPC_SERVICE_NAME", before);
        }
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
