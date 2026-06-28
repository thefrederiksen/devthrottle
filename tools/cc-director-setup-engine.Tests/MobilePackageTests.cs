using System.IO.Compression;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #809: the mobile app (issue #806) ships as a side-car zip the setup engine unpacks into
/// wwwroot/m beside the Gateway exe, so /m serves on a clean install / self-update / redeploy with no
/// manual copy. These tests prove the extract contract (lands index.html + assets, cleans stale
/// files, verifies the SHA, tolerates a release without the asset).
/// </summary>
public class MobilePackageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public MobilePackageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-mobile-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Build a real mobile zip (index.html + a hashed asset) and a manifest in a local release dir.</summary>
    private string BuildReleaseDir(string indexHtml)
    {
        var zipPath = BuildMobileZip(Path.Combine(_releaseDir, MobilePackage.AssetName), indexHtml);
        var sha = Hashing.Sha256OfFile(zipPath);
        var manifest = new
        {
            version = "0.4.0",
            assets = new Dictionary<string, object>
            {
                [MobilePackage.AssetName] = new { version = "0.4.0", sha256 = sha, platform = "windows", size = new FileInfo(zipPath).Length },
            },
        };
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), JsonSerializer.Serialize(manifest));
        return _releaseDir;
    }

    /// <summary>The zip's root is the CONTENTS of wwwroot/m (index.html + hashed assets/), matching release.yml.</summary>
    private string BuildMobileZip(string zipPath, string indexHtml)
    {
        var payload = Path.Combine(_dir, "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(payload, "assets"));
        File.WriteAllText(Path.Combine(payload, "index.html"), indexHtml);
        File.WriteAllText(Path.Combine(payload, "assets", "index-abc123.js"), "console.log('roster')");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(payload, zipPath);
        Directory.Delete(payload, recursive: true);
        return zipPath;
    }

    [Fact]
    public async Task ExtractAsync_PlacesIndexAndAssets_BesideTheExe()
    {
        var release = ReleaseSource.LoadLocalReleaseDir(BuildReleaseDir("<html>__GATEWAY_TOKEN__</html>"));

        var dir = await MobilePackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.NotNull(dir);
        Assert.Equal(_layout.GatewayMobileDir, dir);
        Assert.True(File.Exists(Path.Combine(_layout.GatewayMobileDir, "index.html")));
        Assert.True(File.Exists(Path.Combine(_layout.GatewayMobileDir, "assets", "index-abc123.js")));
        // The mobile dir lands under the Gateway dir (beside where the exe is placed).
        Assert.StartsWith(_layout.GatewayDir, _layout.GatewayMobileDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAsync_ReplacesPriorContents_NoStaleFiles()
    {
        Directory.CreateDirectory(_layout.GatewayMobileDir);
        File.WriteAllText(Path.Combine(_layout.GatewayMobileDir, "stale-old.js"), "stale");

        var release = ReleaseSource.LoadLocalReleaseDir(BuildReleaseDir("<html></html>"));
        await MobilePackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.False(File.Exists(Path.Combine(_layout.GatewayMobileDir, "stale-old.js")));
        Assert.True(File.Exists(Path.Combine(_layout.GatewayMobileDir, "index.html")));
    }

    [Fact]
    public async Task ExtractAsync_NoMobileAsset_ReturnsNull()
    {
        // A manifest with no mobile asset (a release that predates #806).
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"),
            "{\"version\":\"0.4.0\",\"assets\":{}}");
        var release = ReleaseSource.LoadLocalReleaseDir(_releaseDir);

        var dir = await MobilePackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.Null(dir);
        Assert.False(Directory.Exists(_layout.GatewayMobileDir));
    }

    [Fact]
    public async Task ExtractAsync_ShaMismatch_Throws()
    {
        var dir = BuildReleaseDir("<html></html>");
        // Corrupt the manifest's sha so it no longer matches the zip.
        var manifestPath = Path.Combine(dir, "release-manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"sha256\":\"", "\"sha256\":\"00"));
        var release = ReleaseSource.LoadLocalReleaseDir(dir);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MobilePackage.ExtractAsync(_layout, release, new ReleaseSource()));
    }

    [Fact]
    public void ExtractStagedZip_AppliesStagedZip_IntoWwwrootM()
    {
        // The self-update path: an already-verified zip sitting at the staged path.
        var staged = BuildMobileZip(Path.Combine(_dir, "staged.zip"), "<html>roster</html>");

        var dir = MobilePackage.ExtractStagedZip(_layout, staged);

        Assert.NotNull(dir);
        Assert.Equal(_layout.GatewayMobileDir, dir);
        Assert.True(File.Exists(Path.Combine(_layout.GatewayMobileDir, "index.html")));
    }

    [Fact]
    public void ExtractStagedZip_NoStagedZip_ReturnsNull_LeavesWwwrootMUntouched()
    {
        // Pre-existing wwwroot/m must survive when there is no staged zip (a release without the asset).
        Directory.CreateDirectory(_layout.GatewayMobileDir);
        File.WriteAllText(Path.Combine(_layout.GatewayMobileDir, "index.html"), "prior");

        var dir = MobilePackage.ExtractStagedZip(_layout, Path.Combine(_dir, "does-not-exist.zip"));

        Assert.Null(dir);
        Assert.Equal("prior", File.ReadAllText(Path.Combine(_layout.GatewayMobileDir, "index.html")));
    }
}
