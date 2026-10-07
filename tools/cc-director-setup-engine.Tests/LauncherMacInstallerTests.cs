#pragma warning disable CA1416 // The class under test is macOS-only in production; here every external effect is injected, so these run on every operating system.
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the macOS launcher install step. Every external effect is injected (the command
/// runner, the process starter, the registration file path, the launch agent property list path),
/// so these tests exercise the real decision flow with no launchd and no binary.
/// The class under test is macOS-only in production; with every effect injected these run on every operating system.
/// </summary>
public class LauncherMacInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _plistPath;
    private readonly string _registrationPath;

    public LauncherMacInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-launcher-mac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        _plistPath = Path.Combine(_dir, "LaunchAgents", LauncherLaunchdAutostart.Label + ".plist");
        _registrationPath = Path.Combine(_dir, "launcher.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_plistPath)!);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task InstallAsync_BinaryMissing_Fails()
    {
        var installer = new LauncherMacInstaller(_layout,
            runCommand: (_, _) => (0, ""),
            startProcess: (_, _, _) => 1234,
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1),
            registrationPath: _registrationPath);

        var result = await installer.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("not present", result.Message);
    }

    [Fact]
    public async Task InstallAsync_LaunchdReportsNoProcess_SaysWhyAndCarriesTheDiagnostics()
    {
        // A registered agent whose launcher dies at start-up: launchd answers, but with no pid - the state
        // a user hit on 22 Sep 2026 and was told only "launchd did not report which process is running".
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "");
        File.WriteAllText(_plistPath, "<plist/>");
        var logDir = Path.Combine(_layout.LogsDir, "launcher");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "launchd-stderr.log"), "Unhandled exception. System.Exception: boom\n");

        const string crashed = "state = not running\nruns = 2\nlast exit code = 134: Abort trap\njob state = exited\n";
        // launchd holds the job until the rebuild boots it out, and again once the new definition is bootstrapped;
        // a print in between answers "Could not find service", as the real launchd does.
        var held = true;
        var installer = new LauncherMacInstaller(_layout,
            runCommand: (exe, args) =>
            {
                if (exe == "/bin/launchctl" && args.StartsWith("bootout", StringComparison.Ordinal)) held = false;
                if (exe == "/bin/launchctl" && args.StartsWith("bootstrap", StringComparison.Ordinal)) held = true;
                return exe switch
                {
                    "/usr/bin/id" => (0, "501"),
                    // Every install file belongs to the user: the ownership check must answer, not be skipped.
                    "/usr/bin/stat" => (0, string.Join('\n', Enumerable.Repeat("501", args.Count(c => c == '"') / 2))),
                    "/usr/bin/find" => (0, ""),
                    "/bin/launchctl" when args.StartsWith("print", StringComparison.Ordinal) => held ? (0, crashed) : (113, "Could not find service"),
                    _ => (0, ""),
                };
            },
            startProcess: (_, _, _) => 1234,
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1),
            registrationPath: _registrationPath,
            launchdPidWait: TimeSpan.FromSeconds(1));

        var result = await installer.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("exited with code 134: Abort trap", result.Message);
        Assert.Contains("Go to Folder", result.Message);
        Assert.NotNull(result.Diagnostics);
        Assert.Contains("last exit code = 134: Abort trap", result.Diagnostics);
        Assert.Contains("Unhandled exception. System.Exception: boom", result.Diagnostics);
    }

    // RETIRED, deliberately: two tests here pinned the behaviour that broke a Mac.
    //
    // InstallAsync_FirstInstall_StartsDirectlyAndVerifiesPlist asserted that a first install starts the
    // launcher DIRECTLY and relies on it registering its own launch agent afterwards. That is precisely
    // how a launcher launchd does not own comes into existence, and nothing could then stop it: the
    // uninstall only asked launchd, which had never heard of that process. The first install now
    // registers the agent and lets launchd start it, so the test asserted the defect.
    //
    // The kickstart-failure test required a direct-start fallback for the same reason, and that fallback
    // is gone: a registered agent that will not start is now a reported failure, not an unmanaged
    // process started behind the user's back.
    //
    // They also could not have caught the change: the production first-install path calls the STATIC
    // LauncherLaunchdAutostart.EnsureRegistered, not the injected _runCommand or _startProcess seams, so
    // on a Mac they would have written the developer's own real launch agent pointing at a temporary
    // file. On Windows every one of them returned early rather than being skipped, so they counted as
    // green while covering nothing.
    //
    // What replaces them: LauncherStopperTests covers stopping by process and scope, and
    // LaunchdPidParseTests covers reading the process id back from launchd. Proving that a first install
    // on a real Mac yields a launchd-managed launcher needs a real Mac, and is recorded as such in
    // docs/MISSION-installer-both-platforms-2026-07-29.md.
}
