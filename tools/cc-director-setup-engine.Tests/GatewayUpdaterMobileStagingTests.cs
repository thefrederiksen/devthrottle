using System.IO.Compression;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #809: when the running Gateway stages a newer exe for a self-update, it must ALSO stage the
/// matching mobile app zip next to it so the update helper can lay wwwroot/m beside the swapped exe
/// with no download. These tests prove StageAsync stages both, and that a release without the mobile
/// asset clears any stale staged zip.
/// </summary>
public class GatewayUpdaterMobileStagingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public GatewayUpdaterMobileStagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-gwmob-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A release with a newer Gateway exe (version 0.4.0) and, optionally, the mobile zip.</summary>
    private ResolvedRelease BuildRelease(bool includeMobile)
    {
        var gwAsset = Path.Combine(_releaseDir, ComponentRegistry.Gateway.WindowsAsset);
        File.WriteAllText(gwAsset, "gateway-v2");
        var assets = new Dictionary<string, object>
        {
            [ComponentRegistry.Gateway.WindowsAsset] =
                new { version = "0.4.0", sha256 = Hashing.Sha256OfFile(gwAsset), platform = "windows", size = new FileInfo(gwAsset).Length },
        };

        if (includeMobile)
        {
            var zipPath = BuildMobileZip(Path.Combine(_releaseDir, MobilePackage.AssetName));
            assets[MobilePackage.AssetName] =
                new { version = "0.4.0", sha256 = Hashing.Sha256OfFile(zipPath), platform = "windows", size = new FileInfo(zipPath).Length };
        }

        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"),
            JsonSerializer.Serialize(new { version = "0.4.0", assets }));
        return ReleaseSource.LoadLocalReleaseDir(_releaseDir);
    }

    private string BuildMobileZip(string zipPath)
    {
        var payload = Path.Combine(_dir, "m-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "index.html"), "<html></html>");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(payload, zipPath);
        Directory.Delete(payload, recursive: true);
        return zipPath;
    }

    /// <summary>Mark the Gateway as installed at an OLDER version so an update is available.</summary>
    private void MarkGatewayInstalled(string version)
    {
        var exe = _layout.PathFor(ComponentRegistry.Gateway);
        Directory.CreateDirectory(Path.GetDirectoryName(exe) ?? _layout.GatewayDir);
        File.WriteAllText(exe, "installed-gateway");
        var m = InstalledManifest.Load(_layout);
        m.Set(ComponentRegistry.Gateway.Id, version);
        m.Save(_layout);
    }

    [Fact]
    public async Task StageAsync_StagesMobileZip_AlongsideExe()
    {
        MarkGatewayInstalled("0.3.0");
        var release = BuildRelease(includeMobile: true);
        var updater = new GatewayUpdater(_layout);

        var staged = await updater.StageAsync(release, new ReleaseSource());

        Assert.NotNull(staged);
        Assert.True(File.Exists(updater.StagedExePath), "the new Gateway exe should be staged");
        Assert.True(File.Exists(updater.StagedMobileZipPath), "the matching mobile zip should be staged beside it");
    }

    [Fact]
    public async Task StageAsync_NoMobileAsset_RemovesStaleStagedZip()
    {
        MarkGatewayInstalled("0.3.0");
        var updater = new GatewayUpdater(_layout);
        // A stale staged zip from a prior release must not be applied over a newer exe.
        Directory.CreateDirectory(Path.GetDirectoryName(updater.StagedMobileZipPath) ?? _layout.StateDir);
        File.WriteAllText(updater.StagedMobileZipPath, "stale");

        var staged = await updater.StageAsync(BuildRelease(includeMobile: false), new ReleaseSource());

        Assert.NotNull(staged);
        Assert.True(File.Exists(updater.StagedExePath));
        Assert.False(File.Exists(updater.StagedMobileZipPath), "the stale staged mobile zip should be removed");
    }
}
