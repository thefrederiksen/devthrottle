using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstallLayoutTests
{
    private static readonly InstallLayout Layout = new(@"C:\root");

    [Fact]
    public void PathFor_Director_IsInAppDir()
    {
        var path = Layout.PathFor(ComponentRegistry.Director);
        Assert.Equal(Path.Combine(@"C:\root", "app", "cc-director.exe"), path);
    }

    [Fact]
    public void PathFor_Tool_IsInBinDir()
    {
        var path = Layout.PathFor(ComponentRegistry.ToolComponent("cc-pdf"));
        Assert.Equal(Path.Combine(@"C:\root", "bin", "cc-pdf.exe"), path);
    }

    [Fact]
    public void PathFor_GatewayAndCockpit_AreUnderTheUserRoot()
    {
        // The Gateway is a per-user tray app: everything lives under the one user root
        // (docs/plans/gateway-tray-app.md) so install/update/uninstall never elevate.
        Assert.Equal(
            Path.Combine(@"C:\root", "gateway", "cc-director-gateway.exe"),
            Layout.PathFor(ComponentRegistry.Gateway));
        Assert.Equal(
            Path.Combine(@"C:\root", "cockpit", "cc-director-cockpit.exe"),
            Layout.PathFor(ComponentRegistry.Cockpit));
    }

    [Fact]
    public void StateAndLogsDirs_AreUnderTheUserRoot()
    {
        Assert.Equal(Path.Combine(@"C:\root", "state"), Layout.StateDir);
        Assert.Equal(Path.Combine(@"C:\root", "logs"), Layout.LogsDir);
    }

    [Fact]
    public void Constructor_RejectsEmptyRoot()
    {
        Assert.Throws<ArgumentException>(() => new InstallLayout(""));
        Assert.Throws<ArgumentException>(() => new InstallLayout("  "));
    }
}
