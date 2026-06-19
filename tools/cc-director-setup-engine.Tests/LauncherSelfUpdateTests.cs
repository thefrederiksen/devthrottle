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
            stopLauncher: () => true,
            startLauncher: () => true,
            isHealthy: _ => Task.FromResult(false),    // new build never comes up
            healthTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(SelfUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal("launcher-OLD", File.ReadAllText(_target));   // restored from .old
        Assert.True(PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, "0.4.0")); // pinned away from the bad version
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
        var name = ComponentRegistry.Launcher.WindowsAsset; // cc-launcher-win-x64.exe
        var path = Path.Combine(_releaseDir, name);
        File.WriteAllText(path, $"launcher@{version}");
        var manifest = new
        {
            version,
            assets = new Dictionary<string, object>
            {
                [name] = new { version, sha256 = Hashing.Sha256OfFile(path), platform = "windows", size = new FileInfo(path).Length },
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
}
