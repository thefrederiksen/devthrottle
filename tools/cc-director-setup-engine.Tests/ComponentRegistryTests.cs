using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class ComponentRegistryTests
{
    [Fact]
    public void Build_ProducesAppsPlusTools()
    {
        var all = ComponentRegistry.Build(["cc-pdf", "cc-html"]);
        Assert.Equal(5, all.Count); // 3 apps + 2 tools
        Assert.Contains(all, c => c.Id == "director");
        Assert.Contains(all, c => c.Id == "gateway");
        Assert.Contains(all, c => c.Id == "cockpit");
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
    public void Workstation_ExcludesGatewayAndCockpit()
    {
        var all = ComponentRegistry.Build(["cc-pdf"]);
        var ws = ComponentRegistry.ForRole(all, InstallRole.Workstation);

        Assert.Contains(ws, c => c.Id == "director");
        Assert.Contains(ws, c => c.Id == "cc-pdf");
        Assert.DoesNotContain(ws, c => c.Id == "gateway");
        Assert.DoesNotContain(ws, c => c.Id == "cockpit");
    }

    [Fact]
    public void Gateway_IsSupersetOfWorkstation()
    {
        var all = ComponentRegistry.Build(["cc-pdf"]);
        var ws = ComponentRegistry.ForRole(all, InstallRole.Workstation).Select(c => c.Id).ToHashSet();
        var gw = ComponentRegistry.ForRole(all, InstallRole.Gateway).Select(c => c.Id).ToHashSet();

        // Gateway contains everything the workstation has...
        Assert.True(ws.IsSubsetOf(gw));
        // ...plus the gateway + cockpit.
        Assert.Contains("gateway", gw);
        Assert.Contains("cockpit", gw);
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
                "cc-director-gateway-win-x64.exe": { "version": "0.4.0", "sha256": "b", "platform": "windows" },
                "cc-director-cockpit-win-x64.zip": { "version": "0.4.0", "sha256": "c", "platform": "windows" },
                "cc-director-setup-win-x64.exe": { "version": "0.4.0", "sha256": "d", "platform": "windows" },
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
}
