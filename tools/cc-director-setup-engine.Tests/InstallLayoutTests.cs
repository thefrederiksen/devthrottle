using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstallLayoutTests
{
    private static readonly InstallLayout Layout = new(@"C:\root", @"C:\cc-tools");

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
    public void PathFor_GatewayAndCockpit_AreInServiceRoot()
    {
        Assert.Equal(
            Path.Combine(@"C:\cc-tools", "cc-director-gateway", "cc-director-gateway.exe"),
            Layout.PathFor(ComponentRegistry.Gateway));
        Assert.Equal(
            Path.Combine(@"C:\cc-tools", "cc-director-cockpit", "cc-director-cockpit.exe"),
            Layout.PathFor(ComponentRegistry.Cockpit));
    }

    [Fact]
    public void Constructor_RejectsEmptyRoots()
    {
        Assert.Throws<ArgumentException>(() => new InstallLayout("", @"C:\cc-tools"));
        Assert.Throws<ArgumentException>(() => new InstallLayout(@"C:\root", ""));
    }
}
