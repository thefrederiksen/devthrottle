using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class LauncherSelfUpdateTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _target;
    private readonly string _staged;

    public LauncherSelfUpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-lnsu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        var installedDir = Path.Combine(_dir, "installed");
        Directory.CreateDirectory(installedDir);
        _target = Path.Combine(installedDir, "cc-launcher.exe");
        File.WriteAllText(_target, "launcher-OLD");
        var stagedDir = Path.Combine(_dir, "staged");
        Directory.CreateDirectory(stagedDir);
        _staged = Path.Combine(stagedDir, "cc-launcher.exe");
        File.WriteAllText(_staged, "launcher-NEW");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Apply_HealthyNewBuild_Swaps_RecordsVersion_NoPin()
    {
        var stops = 0; var starts = 0;
        var su = new LauncherSelfUpdate(_layout, unlockTimeout: TimeSpan.FromSeconds(1));

        var result = await su.ApplyAsync(
            _target, _staged, "0.4.0",
            order: LauncherSwapOrder.StopThenPlaceThenStart,
            stopLauncher: () => { stops++; return true; },
            startLauncher: () => { starts++; return true; },
            isHealthy: _ => Task.FromResult(true),
            healthTimeout: TimeSpan.FromSeconds(2));

        Assert.Equal(SelfUpdateOutcome.Updated, result.Outcome);
        Assert.Equal("launcher-NEW", File.ReadAllText(_target));            // swapped in
        Assert.Equal("launcher-OLD", File.ReadAllText(_target + ".old"));   // backup kept
        Assert.Equal("0.4.0", InstalledManifest.Load(_layout).Get(ComponentRegistry.Launcher.Id));
        Assert.False(PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, "0.4.0")); // not pinned
        Assert.Equal(1, stops);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Apply_UnhealthyNewBuild_RollsBack_AndPins()
    {
        var su = new LauncherSelfUpdate(_layout, unlockTimeout: TimeSpan.FromSeconds(1));

        var result = await su.ApplyAsync(
            _target, _staged, "0.4.0",
            order: LauncherSwapOrder.StopThenPlaceThenStart,
            stopLauncher: () => true,
            startLauncher: () => true,
            isHealthy: _ => Task.FromResult(false),    // new build never comes up
            healthTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(SelfUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));   // restored from .old
        Assert.True(PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, "0.4.0")); // pinned away from the bad version
    }

    [Fact]
    public async Task PlaceThenRestart_NeverStopsOrStarts_AndAsksTheSupervisorAfterTheFileIsReplaced()
    {
        // The macOS order. Nothing is stopped - launchd would answer a stop by starting the OLD build
        // back up before the swap lands - and nothing is started alongside the supervisor, which would
        // leave two launchers. One ask, after the file is already the new one.
        var stops = 0; var starts = 0;
        string? contentsWhenAsked = null;
        var su = new LauncherSelfUpdate(_layout, unlockTimeout: TimeSpan.FromSeconds(1));

        var result = await su.ApplyAsync(
            _target, _staged, "0.4.0",
            order: LauncherSwapOrder.PlaceThenRestart,
            stopLauncher: () => { stops++; return true; },
            startLauncher: () => { starts++; return true; },
            isHealthy: _ => Task.FromResult(true),
            healthTimeout: TimeSpan.FromSeconds(2),
            restartLauncher: () => { contentsWhenAsked = File.ReadAllText(_target); return true; });

        Assert.Equal(SelfUpdateOutcome.Updated, result.Outcome);
        Assert.Equal("launcher-NEW", contentsWhenAsked);
        Assert.Equal(0, stops);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task AStopThatDidNotClearTheMachine_AbortsWithoutStartingASECONDLauncher()
    {
        // The launcher did not go. The old build is therefore still serving the machine, and starting
        // one here would hand it a second launcher - two processes, two registrations, and a witness
        // that cannot say which one it is reading. Nothing was replaced, so there is nothing to
        // recover from either: abort and touch nothing.
        var starts = 0;
        var su = new LauncherSelfUpdate(_layout, unlockTimeout: TimeSpan.FromMilliseconds(200));

        // Hold the target open so Windows reports it locked; off Windows the stop reports unclean.
        using var hold = File.Open(_target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await su.ApplyAsync(
            _target, _staged, "0.4.0",
            order: LauncherSwapOrder.StopThenPlaceThenStart,
            stopLauncher: () => false,                       // the stop did not clear the machine
            startLauncher: () => { starts++; return true; },
            isHealthy: _ => Task.FromResult(true),
            healthTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(SelfUpdateOutcome.Failed, result.Outcome);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task PlaceThenRestart_WhenTheSwapItselfFails_DoesNotStartALauncherAlongsideTheSupervisor()
    {
        // The recovery for a failed swap is "put the machine back to a running launcher" - correct when
        // this code is what stopped it, and wrong here, where nothing was stopped and launchd still owns
        // the process that is running. Starting one would leave two.
        var starts = 0;
        var su = new LauncherSelfUpdate(_layout, unlockTimeout: TimeSpan.FromMilliseconds(200));

        var result = await su.ApplyAsync(
            _target, Path.Combine(_dir, "no-such-staged-build"), "0.4.0",
            order: LauncherSwapOrder.PlaceThenRestart,
            stopLauncher: () => true,
            startLauncher: () => { starts++; return true; },
            isHealthy: _ => Task.FromResult(true),
            healthTimeout: TimeSpan.FromMilliseconds(200),
            restartLauncher: () => true);

        Assert.Equal(SelfUpdateOutcome.Failed, result.Outcome);
        Assert.Equal(0, starts);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));
    }

    [Fact]
    public async Task PlaceThenRestart_WithNoSupervisorToAsk_IsRefusedRatherThanRunTheWindowsOrder()
    {
        // A default here would be the Windows order silently applied on a platform where it reinstates
        // the old build and reports success. The argument is required, and so is the thing it needs.
        var su = new LauncherSelfUpdate(_layout, unlockTimeout: TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ArgumentNullException>(() => su.ApplyAsync(
            _target, _staged, "0.4.0",
            order: LauncherSwapOrder.PlaceThenRestart,
            stopLauncher: () => true,
            startLauncher: () => true,
            isHealthy: _ => Task.FromResult(true),
            healthTimeout: TimeSpan.FromSeconds(1)));
    }
}

public class LauncherUpdaterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public LauncherUpdaterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-lnupd-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private ResolvedRelease BuildRelease(string version)
    {
        // THIS MACHINE'S asset, not Windows's. Hard-coded to WindowsAsset, this helper built a release
        // containing only the Windows launcher - so on a Mac or in Linux CI the updater would correctly
        // find no asset for its platform and every test here would pass by doing nothing.
        var name = ComponentRegistry.Launcher.AssetFor(HostPlatform.Current)!;
        var path = Path.Combine(_releaseDir, name);
        File.WriteAllText(path, $"launcher@{version}");
        var manifest = new
        {
            version,
            assets = new Dictionary<string, object>
            {
                [name] = new { version, sha256 = Hashing.Sha256OfFile(path), platform = HostPlatform.Current.ToString().ToLowerInvariant(), size = new FileInfo(path).Length },
            },
        };
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), JsonSerializer.Serialize(manifest));
        return ReleaseSource.LoadLocalReleaseDir(_releaseDir);
    }

    private void InstallLauncher(string version)
    {
        Directory.CreateDirectory(_layout.LauncherDir);
        var p = _layout.PathFor(ComponentRegistry.Launcher);
        File.WriteAllText(p, $"launcher@{version}");
        var m = InstalledManifest.Load(_layout);
        m.Set(ComponentRegistry.Launcher.Id, version);
        m.Save(_layout);
    }

    [Fact]
    public void IsUpdateAvailable_TrueWhenNewer_FalseWhenCurrentOrAbsentOrPinned()
    {
        var release = BuildRelease("0.4.0");
        var updater = new LauncherUpdater(_layout);

        Assert.False(updater.IsUpdateAvailable(release));   // not installed -> refresh-only

        InstallLauncher("0.3.6");
        Assert.True(updater.IsUpdateAvailable(release));     // behind

        InstallLauncher("0.4.0");
        Assert.False(updater.IsUpdateAvailable(release));    // current

        InstallLauncher("0.3.6");
        var pins = new UpdatePins();
        pins.Pin(ComponentRegistry.Launcher.Id, "0.4.0");
        PinStore.Save(_layout, pins);
        Assert.False(updater.IsUpdateAvailable(release));    // pinned
    }

    [Fact]
    public async Task Stage_DownloadsVerifiedExe_ToStagingPath()
    {
        InstallLauncher("0.3.6");
        var release = BuildRelease("0.4.0");

        var staged = await new LauncherUpdater(_layout).StageAsync(release, new ReleaseSource());

        Assert.NotNull(staged);
        Assert.Equal("0.4.0", staged.Value.Version);
        Assert.True(File.Exists(staged.Value.StagedPath));
        Assert.Equal("launcher@0.4.0", File.ReadAllText(staged.Value.StagedPath));
    }

    [Fact]
    public async Task Stage_RecordsTheVersionWhereTheINSTALLERLooksForIt()
    {
        // THE SEAM BETWEEN THE TWO HALVES, and the one place this change can come apart without
        // anything failing to compile: the launcher stages, and a DIFFERENT process - the Director -
        // installs. If the stager did not write the sidecar, or wrote it somewhere else, the Director
        // would find a staged build that "has no recorded version" and refuse it for ever, on every
        // machine, silently. So this asserts the handover itself rather than each side separately.
        InstallLauncher("0.3.6");
        var release = BuildRelease("0.4.0");

        var staged = await new LauncherUpdater(_layout).StageAsync(release, new ReleaseSource());

        Assert.NotNull(staged);
        Assert.Equal("0.4.0", StagedBuildVersion.Read(staged!.Value.StagedPath));

        var owner = new LauncherUpdateOwner(_layout.LocalRoot, () => true);
        Assert.Equal(staged.Value.StagedPath, owner.StagedBuildPath);
        Assert.Equal("0.4.0", owner.ReadVersionOnDisk(owner.StagedBuildPath));
    }

    [Fact]
    public async Task Stage_TheStagedNameIsThisPlatformsName_NeverAWindowsExeOnAMac()
    {
        InstallLauncher("0.3.6");
        var release = BuildRelease("0.4.0");

        var staged = await new LauncherUpdater(_layout).StageAsync(release, new ReleaseSource());

        Assert.NotNull(staged);
        var expected = OperatingSystem.IsWindows() ? "cc-launcher.exe" : "cc-launcher";
        Assert.Equal(expected, Path.GetFileName(staged!.Value.StagedPath));
    }
}
