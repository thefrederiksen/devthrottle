using System.IO.Compression;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class CockpitUpdaterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public CockpitUpdaterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-ckupd-" + Guid.NewGuid().ToString("N"));
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
        var payload = Path.Combine(_dir, "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "devthrottle-cockpit.exe"), $"cockpit@{version}");
        var zip = Path.Combine(_releaseDir, CockpitPackage.AssetName);
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(payload, zip);

        var manifest = new
        {
            version,
            assets = new Dictionary<string, object>
            {
                [CockpitPackage.AssetName] = new { version, sha256 = Hashing.Sha256OfFile(zip), platform = "windows", size = new FileInfo(zip).Length },
            },
        };
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), JsonSerializer.Serialize(manifest));
        return ReleaseSource.LoadLocalReleaseDir(_releaseDir);
    }

    private void InstallCockpit(string version)
    {
        Directory.CreateDirectory(_layout.CockpitDir);
        File.WriteAllText(_layout.PathFor(ComponentRegistry.Cockpit), $"cockpit@{version}");
        var m = InstalledManifest.Load(_layout);
        m.Set(ComponentRegistry.Cockpit.Id, version);
        m.Save(_layout);
    }

    private string CockpitContent() => File.ReadAllText(_layout.PathFor(ComponentRegistry.Cockpit));

    [Fact]
    public async Task Apply_UpdatesWhenNewer()
    {
        InstallCockpit("0.3.6");
        var release = BuildRelease("0.3.7");
        var updater = new CockpitUpdater(_layout);

        Assert.True(updater.IsUpdateAvailable(release));
        var v = await updater.ApplyAsync(release, new ReleaseSource());

        Assert.Equal("0.3.7", v);
        Assert.Equal("cockpit@0.3.7", CockpitContent());
        Assert.Equal("0.3.7", InstalledManifest.Load(_layout).Get(ComponentRegistry.Cockpit.Id));
    }

    [Fact]
    public async Task Apply_NoOp_WhenCurrent()
    {
        InstallCockpit("0.3.7");
        var release = BuildRelease("0.3.7");
        var updater = new CockpitUpdater(_layout);

        Assert.False(updater.IsUpdateAvailable(release));
        Assert.Null(await updater.ApplyAsync(release, new ReleaseSource()));
        Assert.Equal("cockpit@0.3.7", CockpitContent());
    }

    [Fact]
    public void IsUpdateAvailable_False_WhenNotInstalled()
    {
        // No InstallCockpit -> the Cockpit is absent; refresh-only must not offer to install it.
        var release = BuildRelease("0.3.7");
        Assert.False(new CockpitUpdater(_layout).IsUpdateAvailable(release));
    }

    [Fact]
    public void IsUpdateAvailable_False_WhenPinned()
    {
        InstallCockpit("0.3.6");
        var release = BuildRelease("0.3.7");
        var pins = new UpdatePins();
        pins.Pin(ComponentRegistry.Cockpit.Id, "0.3.7");
        PinStore.Save(_layout, pins);

        Assert.False(new CockpitUpdater(_layout).IsUpdateAvailable(release));
    }
}
