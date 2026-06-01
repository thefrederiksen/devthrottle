using System.IO.Compression;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class CockpitPackageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public CockpitPackageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-cockpit-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "pf"), Path.Combine(_dir, "pd"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Build a real cockpit zip (exe + a dep + wwwroot file) and a manifest in a local release dir.</summary>
    private string BuildReleaseDir(string exeContents)
    {
        var payload = Path.Combine(_dir, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "wwwroot"));
        File.WriteAllText(Path.Combine(payload, "cc-director-cockpit.exe"), exeContents);
        File.WriteAllText(Path.Combine(payload, "CcDirector.Gateway.Contracts.dll"), "dep");
        File.WriteAllText(Path.Combine(payload, "wwwroot", "app.css"), "body{}");

        var zipPath = Path.Combine(_releaseDir, CockpitPackage.AssetName);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(payload, zipPath);
        Directory.Delete(payload, recursive: true);

        var sha = Hashing.Sha256OfFile(zipPath);
        var manifest = new
        {
            version = "0.4.0",
            assets = new Dictionary<string, object>
            {
                [CockpitPackage.AssetName] = new { version = "0.4.0", sha256 = sha, platform = "windows", size = new FileInfo(zipPath).Length },
            },
        };
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), JsonSerializer.Serialize(manifest));
        return _releaseDir;
    }

    [Fact]
    public async Task Extract_PlacesCockpitExeAndAssets()
    {
        var dir = BuildReleaseDir("cockpit-v1");
        var release = ReleaseSource.LoadLocalReleaseDir(dir);

        var exe = await CockpitPackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.Equal(_layout.PathFor(ComponentRegistry.Cockpit), exe);
        Assert.True(File.Exists(exe));
        Assert.Equal("cockpit-v1", File.ReadAllText(exe));
        Assert.True(File.Exists(Path.Combine(_layout.CockpitDir, "CcDirector.Gateway.Contracts.dll")));
        Assert.True(File.Exists(Path.Combine(_layout.CockpitDir, "wwwroot", "app.css")));
    }

    [Fact]
    public async Task Extract_ReplacesPriorContents_NoStaleFiles()
    {
        // Pre-seed a stale file that must NOT survive a re-extract.
        Directory.CreateDirectory(_layout.CockpitDir);
        File.WriteAllText(Path.Combine(_layout.CockpitDir, "stale-old.dll"), "stale");

        var release = ReleaseSource.LoadLocalReleaseDir(BuildReleaseDir("cockpit-v2"));
        await CockpitPackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.False(File.Exists(Path.Combine(_layout.CockpitDir, "stale-old.dll")));
        Assert.Equal("cockpit-v2", File.ReadAllText(_layout.PathFor(ComponentRegistry.Cockpit)));
    }

    [Fact]
    public async Task Extract_ShaMismatch_Throws_AndDoesNotPopulate()
    {
        var dir = BuildReleaseDir("cockpit-v1");
        // Corrupt the manifest's sha so it no longer matches the zip.
        var manifestPath = Path.Combine(dir, "release-manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"sha256\":\"", "\"sha256\":\"00"));
        var release = ReleaseSource.LoadLocalReleaseDir(dir);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CockpitPackage.ExtractAsync(_layout, release, new ReleaseSource()));
    }

    [Fact]
    public async Task Extract_MissingAsset_Throws()
    {
        // A manifest with no cockpit asset.
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"),
            "{\"version\":\"0.4.0\",\"assets\":{}}");
        var release = ReleaseSource.LoadLocalReleaseDir(_releaseDir);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CockpitPackage.ExtractAsync(_layout, release, new ReleaseSource()));
    }
}
