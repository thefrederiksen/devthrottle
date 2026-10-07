using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The launch agent rebuild the installer and the Director run (#3411): whatever launchd held is booted out,
/// the current definition is written, the log folder exists, and the job is bootstrapped AND kickstarted.
/// launchctl is faked, so what is pinned here is the ORDER of what is asked of launchd and what lands on disk;
/// the macOS-only guard is the class's own, so these exit early elsewhere.
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

    private static LauncherLaunchdAutostart.CommandRunner Fake(List<string> calls, bool loaded, string print = "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n")
        => (exe, args) =>
        {
            calls.Add($"{exe} {args}");
            if (exe == "/usr/bin/id") return (0, "501\n");
            if (exe == "/bin/launchctl" && args.StartsWith("print ", StringComparison.Ordinal)) return loaded ? (0, print) : (113, "Could not find service");
            return (0, "");
        };

    [Fact]
    public void Rebuild_WithARefusedJobLoaded_BootsOutWritesBootstrapsAndKickstarts()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var calls = new List<string>();

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", "--managed", Fake(calls, loaded: true), _plist, _logDir);

        Assert.True(result.PreviousLoaded);
        Assert.Contains("78: EX_CONFIG", result.PreviousPrint);
        var launchctl = calls.Where(c => c.StartsWith("/bin/launchctl", StringComparison.Ordinal)).Select(c => c.Split(' ')[1]).ToList();
        Assert.Equal(["print", "bootout", "bootstrap", "kickstart"], launchctl);
        Assert.Contains($"/bin/launchctl bootout gui/501/{LauncherLaunchdAutostart.Label}", calls);
        Assert.Contains($"/bin/launchctl kickstart -k gui/501/{LauncherLaunchdAutostart.Label}", calls);
        Assert.True(File.Exists(_plist));
        Assert.Equal(LauncherLaunchdAutostart.PlistContent("/tmp/x/cc-launcher", "--managed", _logDir), File.ReadAllText(_plist));
        Assert.True(Directory.Exists(_logDir));
        Assert.Contains(result.Steps, s => s.Contains("already held the job", StringComparison.Ordinal) && s.Contains("spawn failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebuild_WithNoJobLoaded_SkipsBootout()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var calls = new List<string>();

        var result = LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, Fake(calls, loaded: false), _plist, _logDir);

        Assert.False(result.PreviousLoaded);
        Assert.Null(result.PreviousPrint);
        var launchctl = calls.Where(c => c.StartsWith("/bin/launchctl", StringComparison.Ordinal)).Select(c => c.Split(' ')[1]).ToList();
        Assert.Equal(["print", "bootstrap", "kickstart"], launchctl);
    }

    [Fact]
    public void Rebuild_WhenBootstrapFails_Throws()
    {
        if (!OperatingSystem.IsMacOS()) return;
        LauncherLaunchdAutostart.CommandRunner run = (exe, args) =>
        {
            if (exe == "/usr/bin/id") return (0, "501");
            if (args.StartsWith("print ", StringComparison.Ordinal)) return (113, "");
            if (args.StartsWith("bootstrap ", StringComparison.Ordinal)) return (5, "Bootstrap failed: 5: Input/output error");
            return (0, "");
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("not macOS");
            LauncherLaunchdAutostart.Rebuild("/tmp/x/cc-launcher", null, run, _plist, _logDir);
        });

        Assert.Contains("bootstrap failed (exit 5)", ex.Message);
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
