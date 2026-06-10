using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstalledRoleDetectorTests
{
    private static readonly InstallLayout Layout = new(@"C:\root");
    private static readonly string GatewayExe = Layout.PathFor(ComponentRegistry.Gateway);

    /// <summary>Build a reader whose file-existence answer we control, with no disk access.</summary>
    private static InstalledStateReader ReaderWith(Func<string, bool> fileExists) =>
        new(Layout, fileExists: fileExists, readVersion: _ => null, installed: InstalledManifest.Empty());

    [Fact]
    public void Detect_GatewayExePresent_IsGateway()
    {
        var role = InstalledRoleDetector.Detect(Layout, ReaderWith(path => path == GatewayExe));
        Assert.Equal(InstallRole.Gateway, role);
    }

    [Fact]
    public void Detect_GatewayExeAbsent_IsWorkstation()
    {
        // Director present, Gateway absent: a plain Workstation install.
        var role = InstalledRoleDetector.Detect(Layout, ReaderWith(path => path != GatewayExe));
        Assert.Equal(InstallRole.Workstation, role);
    }

    [Fact]
    public void Detect_NothingInstalled_IsWorkstation()
    {
        var role = InstalledRoleDetector.Detect(Layout, ReaderWith(_ => false));
        Assert.Equal(InstallRole.Workstation, role);
    }
}
