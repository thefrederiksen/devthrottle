using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class ToolUpdaterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public ToolUpdaterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-toolupd-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Write a release dir: one asset per tool, content "<id>@<version>", with a matching manifest.</summary>
    private ResolvedRelease BuildRelease(string version, params string[] toolIds)
    {
        var assets = new Dictionary<string, object>();
        foreach (var id in toolIds)
        {
            var assetName = $"{id}-win-x64.exe";
            var path = Path.Combine(_releaseDir, assetName);
            File.WriteAllText(path, $"{id}@{version}");
            assets[assetName] = new { version, sha256 = Hashing.Sha256OfFile(path), platform = "windows", size = new FileInfo(path).Length };
        }
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"),
            JsonSerializer.Serialize(new { version, assets }));
        return ReleaseSource.LoadLocalReleaseDir(_releaseDir);
    }

    /// <summary>Mark a tool as installed at a version: place its bin exe and record it in installed.json.</summary>
    private void InstallTool(string id, string version)
    {
        Directory.CreateDirectory(_layout.BinDir);
        File.WriteAllText(Path.Combine(_layout.BinDir, $"{id}.exe"), $"{id}@{version}");
        var m = InstalledManifest.Load(_layout);
        m.Set(id, version);
        m.Save(_layout);
    }

    private string ToolContent(string id) => File.ReadAllText(Path.Combine(_layout.BinDir, $"{id}.exe"));

    [Fact]
    public async Task Refresh_UpdatesInstalledToolThatIsBehind()
    {
        var release = BuildRelease("2.0.0", "cc-pdf", "cc-html");
        InstallTool("cc-pdf", "1.0.0");   // behind
        InstallTool("cc-html", "2.0.0");  // current

        var result = await new ToolUpdater(_layout).RefreshAsync(release, new ReleaseSource());

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Failed);
        Assert.Equal("cc-pdf@2.0.0", ToolContent("cc-pdf"));   // swapped to new build
        Assert.Equal("cc-html@2.0.0", ToolContent("cc-html")); // untouched
        Assert.Equal("2.0.0", InstalledManifest.Load(_layout).Get("cc-pdf")); // recorded
    }

    [Fact]
    public async Task Refresh_DoesNotInstallAToolTheUserDoesNotHave()
    {
        var release = BuildRelease("2.0.0", "cc-pdf", "cc-word");
        InstallTool("cc-pdf", "1.0.0");   // installed + behind
        // cc-word is in the release but NOT installed -> must stay absent.

        var result = await new ToolUpdater(_layout).RefreshAsync(release, new ReleaseSource());

        Assert.Equal(1, result.Updated);
        Assert.Equal("cc-pdf@2.0.0", ToolContent("cc-pdf"));
        Assert.False(File.Exists(Path.Combine(_layout.BinDir, "cc-word.exe"))); // not added
    }

    [Fact]
    public async Task Refresh_RespectsRollbackPin()
    {
        var release = BuildRelease("2.0.0", "cc-pdf");
        InstallTool("cc-pdf", "1.0.0");
        var pins = new UpdatePins();
        pins.Pin("cc-pdf", "2.0.0");      // the user rolled back away from 2.0.0
        PinStore.Save(_layout, pins);

        var result = await new ToolUpdater(_layout).RefreshAsync(release, new ReleaseSource());

        Assert.Equal(0, result.Updated);
        Assert.Equal("cc-pdf@1.0.0", ToolContent("cc-pdf")); // not re-staged
    }

    [Fact]
    public async Task Refresh_NoOp_WhenEverythingCurrent()
    {
        var release = BuildRelease("2.0.0", "cc-pdf");
        InstallTool("cc-pdf", "2.0.0");

        var result = await new ToolUpdater(_layout).RefreshAsync(release, new ReleaseSource());

        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Failed);
    }
}
