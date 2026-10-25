using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class ComponentRegistryTests
{
    [Fact]
    public void Build_ProducesAppsPlusTools()
    {
        var all = ComponentRegistry.Build(["cc-pdf", "cc-html"]);
        // 3 apps (director, gateway, launcher) + the shared Python tools bundle + 2 tools
        Assert.Equal(6, all.Count);
        Assert.Contains(all, c => c.Id == PythonToolsInstaller.ComponentId);
        Assert.Contains(all, c => c.Id == "director");
        Assert.Contains(all, c => c.Id == "gateway");
        Assert.Contains(all, c => c.Id == "cc-launcher");
        Assert.Contains(all, c => c.Id == "cc-pdf");
    }

    [Fact]
    public void ToolComponent_UsesReleaseAssetNaming()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        Assert.Equal("cc-pdf-win-x64.exe", pdf.WindowsAsset);
        Assert.Equal(ComponentKind.Tool, pdf.Kind);
    }

    [Fact]
    public void Build_RejectsDuplicateToolIds()
    {
        Assert.Throws<ArgumentException>(() => ComponentRegistry.Build(["cc-pdf", "cc-pdf"]));
    }

    [Fact]
    public void Workstation_ExcludesGateway()
    {
        var all = ComponentRegistry.Build(["cc-pdf"]);
        var ws = ComponentRegistry.ForRole(all, InstallRole.Workstation);

        Assert.Contains(ws, c => c.Id == "director");
        Assert.Contains(ws, c => c.Id == "cc-pdf");
        Assert.DoesNotContain(ws, c => c.Id == "gateway");
    }

    [Fact]
    public void Gateway_IsSupersetOfWorkstation()
    {
        var all = ComponentRegistry.Build(["cc-pdf"]);
        var ws = ComponentRegistry.ForRole(all, InstallRole.Workstation).Select(c => c.Id).ToHashSet();
        var gw = ComponentRegistry.ForRole(all, InstallRole.Gateway).Select(c => c.Id).ToHashSet();

        // Gateway contains everything the workstation has...
        Assert.True(ws.IsSubsetOf(gw));
        // ...plus the gateway itself.
        Assert.Contains("gateway", gw);
    }

    [Fact]
    public void DiscoverToolIds_ReturnsShippedToolsExcludingAppsAndInstaller()
    {
        var manifest = ReleaseManifest.Parse(
            """
            {
              "version": "0.4.0",
              "assets": {
                "cc-director-win-x64.exe": { "version": "0.4.0", "sha256": "a", "platform": "windows" },
                "devthrottle-gateway-win-x64.exe": { "version": "0.4.0", "sha256": "b", "platform": "windows" },
                "devthrottle-cockpit-win-x64.zip": { "version": "0.4.0", "sha256": "c", "platform": "windows" },
                "devthrottle-setup-win-x64.exe": { "version": "0.4.0", "sha256": "d", "platform": "windows" },
                "cc-director-mac-arm64.zip": { "version": "0.4.0", "sha256": "e", "platform": "macos" },
                "cc-pdf-win-x64.exe": { "version": "1.2.0", "sha256": "f", "platform": "windows" },
                "cc-html-win-x64.exe": { "version": "1.1.3", "sha256": "g", "platform": "windows" },
                "cc-word-win-x64.exe": { "version": "1.0.0", "sha256": "h", "platform": "windows" },
                "release-manifest.json": { "version": "0.4.0", "sha256": "i", "platform": "unknown" }
              }
            }
            """);

        var ids = ComponentRegistry.DiscoverToolIds(manifest);

        Assert.Equal(new[] { "cc-html", "cc-pdf", "cc-word" }, ids);
    }

    [Fact]
    public void Launcher_MacAsset_IsTheSingleFileMacBinary()
    {
        // The macOS launcher ships as a self-contained single-file executable; this name is the
        // contract between the release workflow, the release manifest, and the Mac installer.
        Assert.Equal("cc-launcher-mac-arm64", ComponentRegistry.Launcher.MacAsset);
    }

    [Fact]
    public void Director_MacAsset_MatchesTheAppBundleZipThatMacAppPlacerPlaces()
    {
        Assert.Equal(MacAppPlacer.DirectorAsset, ComponentRegistry.Director.MacAsset);
    }

    [Fact]
    public void ToolComponent_HasNoMacAsset()
    {
        // Tools ship to macOS through the shared Python bundle, not as per-tool release assets.
        Assert.Null(ComponentRegistry.ToolComponent("cc-pdf").MacAsset);
    }
}
