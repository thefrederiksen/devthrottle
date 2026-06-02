using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class GatewaySelfUpdateTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _target;
    private readonly string _staged;

    public GatewaySelfUpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-gwsu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "pf"), Path.Combine(_dir, "pd"));
        var installedDir = Path.Combine(_dir, "installed");
        Directory.CreateDirectory(installedDir);
        _target = Path.Combine(installedDir, "cc-director-gateway.exe");
        File.WriteAllText(_target, "gateway-OLD");
        var stagedDir = Path.Combine(_dir, "staged");
        Directory.CreateDirectory(stagedDir);
        _staged = Path.Combine(stagedDir, "cc-director-gateway.exe");
        File.WriteAllText(_staged, "gateway-NEW");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Apply_HealthyNewBuild_Swaps_RecordsVersion_NoPin()
    {
        var stops = 0; var starts = 0;
        var su = new GatewaySelfUpdate(_layout, unlockTimeout: TimeSpan.FromSeconds(1));

        var result = await su.ApplyAsync(
            _target, _staged, "0.4.0",
            stopService: () => { stops++; return true; },
            startService: () => { starts++; return true; },
            isHealthy: _ => Task.FromResult(true),
            healthTimeout: TimeSpan.FromSeconds(2));

        Assert.Equal(SelfUpdateOutcome.Updated, result.Outcome);
        Assert.Equal("gateway-NEW", File.ReadAllText(_target));            // swapped in
        Assert.Equal("gateway-OLD", File.ReadAllText(_target + ".old"));   // backup kept
        Assert.Equal("0.4.0", InstalledManifest.Load(_layout).Get(ComponentRegistry.Gateway.Id));
        Assert.False(PinStore.Load(_layout).IsPinned(ComponentRegistry.Gateway.Id, "0.4.0")); // not pinned
        Assert.Equal(1, stops);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Apply_UnhealthyNewBuild_RollsBack_AndPins()
    {
        var su = new GatewaySelfUpdate(_layout, unlockTimeout: TimeSpan.FromSeconds(1));

        var result = await su.ApplyAsync(
            _target, _staged, "0.4.0",
            stopService: () => true,
            startService: () => true,
            isHealthy: _ => Task.FromResult(false),    // new build never comes up
            healthTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(SelfUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal("gateway-OLD", File.ReadAllText(_target));   // restored from .old
        Assert.True(PinStore.Load(_layout).IsPinned(ComponentRegistry.Gateway.Id, "0.4.0")); // pinned away from the bad version
    }
}

public class GatewayUpdaterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public GatewayUpdaterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-gwupd-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "pf"), Path.Combine(_dir, "pd"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private ResolvedRelease BuildRelease(string version)
    {
        var name = ComponentRegistry.Gateway.WindowsAsset; // cc-director-gateway-win-x64.exe
        var path = Path.Combine(_releaseDir, name);
        File.WriteAllText(path, $"gateway@{version}");
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

    private void InstallGateway(string version)
    {
        Directory.CreateDirectory(_layout.GatewayDir);
        var p = _layout.PathFor(ComponentRegistry.Gateway);
        File.WriteAllText(p, $"gateway@{version}");
        var m = InstalledManifest.Load(_layout);
        m.Set(ComponentRegistry.Gateway.Id, version);
        m.Save(_layout);
    }

    [Fact]
    public void IsUpdateAvailable_TrueWhenNewer_FalseWhenCurrentOrAbsentOrPinned()
    {
        var release = BuildRelease("0.4.0");
        var updater = new GatewayUpdater(_layout);

        Assert.False(updater.IsUpdateAvailable(release));   // not installed -> refresh-only

        InstallGateway("0.3.6");
        Assert.True(updater.IsUpdateAvailable(release));     // behind

        InstallGateway("0.4.0");
        Assert.False(updater.IsUpdateAvailable(release));    // current

        InstallGateway("0.3.6");
        var pins = new UpdatePins();
        pins.Pin(ComponentRegistry.Gateway.Id, "0.4.0");
        PinStore.Save(_layout, pins);
        Assert.False(updater.IsUpdateAvailable(release));    // pinned
    }

    [Fact]
    public async Task Stage_DownloadsVerifiedExe_ToStagingPath()
    {
        InstallGateway("0.3.6");
        var release = BuildRelease("0.4.0");

        var staged = await new GatewayUpdater(_layout).StageAsync(release, new ReleaseSource());

        Assert.NotNull(staged);
        Assert.Equal("0.4.0", staged.Value.Version);
        Assert.True(File.Exists(staged.Value.StagedPath));
        Assert.Equal("gateway@0.4.0", File.ReadAllText(staged.Value.StagedPath));
    }
}
