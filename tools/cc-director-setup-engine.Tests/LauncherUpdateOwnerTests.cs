using System.Text.Json;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Director installing the launcher's staged update. Every test here drives the whole pass through
/// fakes for the three things that touch the machine - the process list, the stop, and the start - so
/// the rollback, the refusals and the witness are all exercised without a launcher anywhere.
///
/// The fake START rewrites the registration file, because that is what a real launcher does when it
/// comes up, and it is the only way the witness can tell a build that arrived from one that did not.
/// </summary>
public class LauncherUpdateOwnerTests : IDisposable
{
    private readonly string _root;
    private readonly InstallLayout _layout;
    private readonly string _target;
    private readonly string _staged;
    private readonly string _registration;

    private const string InstalledVersion = "1.9.8";
    private const string StagedVersion = "2.0.4";

    public LauncherUpdateOwnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-luo-" + Guid.NewGuid().ToString("N"));
        _layout = new InstallLayout(_root);
        _target = _layout.PathFor(ComponentRegistry.Launcher);
        _staged = Path.Combine(_root, "state", "staged", "cc-launcher.exe");
        _registration = LauncherWitness.RegistrationPathFor(_root);

        Directory.CreateDirectory(Path.GetDirectoryName(_target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_staged)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_registration)!);

        File.WriteAllText(_target, "launcher-OLD");
        File.WriteAllText(_staged, "launcher-NEW");
        var manifest = InstalledManifest.Load(_layout);
        manifest.Set(ComponentRegistry.Launcher.Id, InstalledVersion);
        manifest.Save(_layout);

        WriteRegistration(1001, InstalledVersion);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private void WriteRegistration(int pid, string version)
        => File.WriteAllText(_registration, JsonSerializer.Serialize(new { pid, version }));

    /// <summary>The launcher this machine is running, as the process list would report it.</summary>
    private LauncherProcess InstalledLauncher(int pid = 1001)
        => new(pid, Path.Combine(_layout.LauncherDir, "cc-launcher.exe") + " --managed");

    /// <summary>
    /// An owner wired to fakes. <paramref name="newBuildIsCommandable"/> decides what the started
    /// launcher turns out to be: a build that can be told things, or one that comes up perfectly and
    /// listens for nothing - which is the failure this whole thing exists to catch.
    /// </summary>
    private LauncherUpdateOwner Owner(
        List<LauncherProcess> running,
        bool newBuildIsCommandable,
        List<int>? stopped = null,
        List<string>? quitRequests = null,
        bool directorSurvives = true,
        bool oldBuildListens = false)
    {
        var startedNewBuild = false;

        var witness = new LauncherWitness(_root)
        {
            RegistrationPath = _registration,
            ProcessIsAlive = _ => running.Count > 0,
            // Before the swap the old launcher answers (or does not); afterwards the new one does,
            // according to what this test says the new build is.
            HasListener = _ => startedNewBuild ? newBuildIsCommandable : oldBuildListens,
        };

        return new LauncherUpdateOwner(
            _root,
            directorStillHoldsItsInstance: () => directorSurvives,
            witness: witness,
            apply: new LauncherSelfUpdate(_layout, unlockTimeout: TimeSpan.FromSeconds(1)),
            witnessTimeout: TimeSpan.FromMilliseconds(400))
        {
            ReadVersionOnDisk = path => path == _staged ? StagedVersion : null,
            ListLauncherProcesses = () => running.ToList(),
            RequestQuit = root => { quitRequests?.Add(root); return false; },
            StopProcess = pid =>
            {
                stopped?.Add(pid);
                running.RemoveAll(p => p.Pid == pid);
                return true;
            },
            StartLauncher = _ =>
            {
                startedNewBuild = true;
                running.Add(InstalledLauncher(2002));
                WriteRegistration(2002, StagedVersion);
                return 2002;
            },
            GracefulStopTimeout = TimeSpan.FromMilliseconds(200),
            StopSettleTimeout = TimeSpan.FromMilliseconds(200),
        };
    }

    [Fact]
    public void StagedBuildPath_IsUnderTheSharedRoot()
    {
        // Resolved through the calling process's own storage instead, this points into a Director's
        // instance home and finds nothing, for ever, on every machine.
        var owner = new LauncherUpdateOwner(_root, () => true);

        Assert.Equal(Path.Combine(_root, "state", "staged", "cc-launcher.exe"), owner.StagedBuildPath);
    }

    [Fact]
    public void FindStagedUpdate_FindsANewerBuild_AndSaysWhyWhenThereIsNone()
    {
        var owner = Owner([InstalledLauncher()], newBuildIsCommandable: true);

        var found = owner.FindStagedUpdate(out var why);
        Assert.NotNull(found);
        Assert.Equal(StagedVersion, found!.Version);
        Assert.Equal(_target, found.InstallTarget);
        Assert.Equal("", why);

        File.Delete(_staged);
        Assert.Null(owner.FindStagedUpdate(out var missing));
        Assert.Contains("no launcher build is staged", missing);
    }

    [Fact]
    public void FindStagedUpdate_RefusesABuildThatIsNotNewer_AndOneThatWillNotSayWhatItIs()
    {
        var older = new LauncherUpdateOwner(_root, () => true) { ReadVersionOnDisk = _ => "1.9.7" };
        Assert.Null(older.FindStagedUpdate(out var why));
        Assert.Contains("not newer", why);

        var silent = new LauncherUpdateOwner(_root, () => true) { ReadVersionOnDisk = _ => null };
        Assert.Null(silent.FindStagedUpdate(out var noVersion));
        Assert.Contains("does not declare a version", noVersion);
    }

    [Fact]
    public void FindStagedUpdate_SkipsABuildThatIsPinned()
    {
        var pins = new UpdatePins();
        pins.Pin(ComponentRegistry.Launcher.Id, StagedVersion);
        PinStore.Save(_layout, pins);

        var owner = Owner([InstalledLauncher()], newBuildIsCommandable: true);

        Assert.Null(owner.FindStagedUpdate(out var why));
        Assert.StartsWith("pinned", why);
    }

    [Fact]
    public async Task NoLauncherRunning_HoldsAndTouchesNothing()
    {
        var owner = Owner([], newBuildIsCommandable: true);

        var result = await owner.RunOnceAsync();

        Assert.Equal(LauncherUpdateDecision.HeldBecauseLauncherNotRunning, result.Decision);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));
    }

    [Fact]
    public async Task MoreThanOneInstalledLauncher_IsUndecidable_AndTouchesNothing()
    {
        var owner = Owner([InstalledLauncher(1001), InstalledLauncher(1002)], newBuildIsCommandable: true);

        var result = await owner.RunOnceAsync();

        Assert.Equal(LauncherUpdateDecision.HeldBecauseUndecidable, result.Decision);
        Assert.Contains("1001, 1002", result.Message);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));
    }

    [Fact]
    public async Task ALauncherFromSomewhereElse_IsNotOurs()
    {
        // A developer's launcher run from a repository checkout. It is not under the install directory,
        // so it is not counted, not stopped, and not replaced.
        var stopped = new List<int>();
        var owner = Owner([new LauncherProcess(9, @"D:\repos\devthrottle\bin\cc-launcher.exe")],
            newBuildIsCommandable: true, stopped: stopped);

        var result = await owner.RunOnceAsync();

        Assert.Equal(LauncherUpdateDecision.HeldBecauseLauncherNotRunning, result.Decision);
        Assert.Empty(stopped);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));
    }

    [Fact]
    public async Task ARunningLauncherThatIsAlreadyTheStagedVersion_InstallsNothing()
    {
        WriteRegistration(1001, StagedVersion);
        var stopped = new List<int>();
        var owner = Owner([InstalledLauncher()], newBuildIsCommandable: true, stopped: stopped);

        var result = await owner.RunOnceAsync();

        Assert.Equal(LauncherUpdateDecision.NothingStaged, result.Decision);
        Assert.Empty(stopped);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));
        // ...and the manifest is corrected, so the record and the running build stop disagreeing.
        Assert.Equal(StagedVersion, InstalledManifest.Load(_layout).Get(ComponentRegistry.Launcher.Id));
    }

    [Fact]
    public async Task ACommandableNewBuild_IsInstalled_AndRecorded()
    {
        var stopped = new List<int>();
        var quits = new List<string>();
        var owner = Owner([InstalledLauncher()], newBuildIsCommandable: true, stopped: stopped, quitRequests: quits);

        var result = await owner.RunOnceAsync();

        Assert.Equal(LauncherUpdateDecision.Applied, result.Decision);
        Assert.Equal("launcher-NEW", File.ReadAllText(_target));
        Assert.Equal(StagedVersion, InstalledManifest.Load(_layout).Get(ComponentRegistry.Launcher.Id));
        Assert.False(PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, StagedVersion));
        // Asked politely first, and only then insisted on the one process id.
        Assert.Equal(new[] { _root }, quits);
        Assert.Equal(new[] { 1001 }, stopped);
        Assert.Contains(result.Steps, s => s.Contains("still holds its instance"));
    }

    [Fact]
    public async Task ANewBuildThatComesUpAndListensForNOTHING_IsRolledBackAndPinned()
    {
        // THE TEST THIS CLASS EXISTS FOR. The started process is alive, registered, and reports the new
        // version - everything a liveness check looks at - and it holds no command surface. That is a
        // failed update, and calling it a success is how a machine becomes uncommandable in silence.
        var owner = Owner([InstalledLauncher()], newBuildIsCommandable: false);

        var result = await owner.RunOnceAsync();

        Assert.Equal(LauncherUpdateDecision.RolledBack, result.Decision);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));
        Assert.True(PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, StagedVersion));
    }

    [Fact]
    public async Task WhenTheDirectorNoLongerHoldsItsInstance_TheResultSaysSo()
    {
        var owner = Owner([InstalledLauncher()], newBuildIsCommandable: true, directorSurvives: false);

        var result = await owner.RunOnceAsync();

        Assert.Equal(LauncherUpdateDecision.Applied, result.Decision);
        Assert.Contains("NO LONGER HOLDS ITS INSTANCE", result.Message);
        Assert.Contains(result.Steps, s => s.Contains("NO LONGER HOLDS ITS INSTANCE"));
    }

    [Fact]
    public void StopInstalledLauncher_NeverStopsThisVeryProcess()
    {
        // Belt and braces on the no-orphan rule: even if the process list somehow named this process,
        // the Director performing the swap is not something the swap may stop.
        var stopped = new List<int>();
        var running = new List<LauncherProcess> { InstalledLauncher(Environment.ProcessId) };
        var owner = Owner(running, newBuildIsCommandable: true, stopped: stopped);

        owner.StopInstalledLauncher();

        Assert.Empty(stopped);
    }

    [Fact]
    public void StopInstalledLauncher_AnUnreadableProcessList_IsNotAnEmptyOne()
    {
        // "Nothing left running" has to be observed. A list that could not be read is not evidence of
        // an empty machine, and reporting it as a clean stop would let the swap run over a live launcher.
        var owner = new LauncherUpdateOwner(_root, () => true)
        {
            ListLauncherProcesses = () => throw new InvalidOperationException("cannot list processes"),
            RequestQuit = _ => false,
            StopProcess = _ => true,
            GracefulStopTimeout = TimeSpan.FromMilliseconds(50),
            StopSettleTimeout = TimeSpan.FromMilliseconds(50),
        };

        Assert.False(owner.StopInstalledLauncher());
    }
}
