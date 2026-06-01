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
}
