using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstallLayoutTests
{
    private static readonly InstallLayout Layout = new(@"C:\root", @"C:\Program Files\CC Director", @"C:\pd");

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
    public void PathFor_GatewayAndCockpit_AreUnderProgramFiles()
    {
        Assert.Equal(
            Path.Combine(@"C:\Program Files\CC Director", "gateway", "cc-director-gateway.exe"),
            Layout.PathFor(ComponentRegistry.Gateway));
        Assert.Equal(
            Path.Combine(@"C:\Program Files\CC Director", "cockpit", "cc-director-cockpit.exe"),
            Layout.PathFor(ComponentRegistry.Cockpit));
    }

    [Fact]
    public void ServiceDataDirs_AreUnderProgramData()
    {
        Assert.Equal(Path.Combine(@"C:\pd", "config"), Layout.ServiceConfigDir);
        Assert.Equal(Path.Combine(@"C:\pd", "state"), Layout.ServiceStateDir);
        Assert.Equal(Path.Combine(@"C:\pd", "logs"), Layout.ServiceLogsDir);
    }

    [Fact]
    public void Constructor_RejectsEmptyRoots()
    {
        Assert.Throws<ArgumentException>(() => new InstallLayout("", @"C:\pf", @"C:\pd"));
        Assert.Throws<ArgumentException>(() => new InstallLayout(@"C:\root", "", @"C:\pd"));
        Assert.Throws<ArgumentException>(() => new InstallLayout(@"C:\root", @"C:\pf", ""));
    }
}
