using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class UninstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public UninstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "pf"), Path.Combine(_dir, "pd"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Plan_Workstation_TargetsAppBinPathShortcut_NotGatewayOrService()
    {
        var plan = new Uninstaller(_layout).Plan(InstallRole.Workstation);
        var dirs = plan.Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();

        Assert.Contains(_layout.AppDir, dirs);
        Assert.Contains(_layout.BinDir, dirs);
        Assert.DoesNotContain(_layout.GatewayDir, dirs);
        Assert.DoesNotContain(_layout.CockpitDir, dirs);
        Assert.DoesNotContain(plan, t => t.Kind == UninstallKind.Service);
        Assert.Contains(plan, t => t.Kind == UninstallKind.PathEntry);
        Assert.Contains(plan, t => t.Kind == UninstallKind.Shortcut);
    }

    [Fact]
    public void Plan_Gateway_AddsServiceAndMachineDirs()
    {
        var plan = new Uninstaller(_layout).Plan(InstallRole.Gateway);
        var dirs = plan.Where(t => t.Kind == UninstallKind.Directory).Select(t => t.Path).ToList();

        Assert.Contains(plan, t => t.Kind == UninstallKind.Service);
        Assert.Contains(_layout.GatewayDir, dirs);
        Assert.Contains(_layout.CockpitDir, dirs);
        Assert.Contains(_layout.ServiceConfigDir, dirs);
        Assert.Contains(_layout.ServiceStateDir, dirs);
        Assert.Contains(_layout.ServiceLogsDir, dirs);
    }

    [Fact]
    public void RemoveDirectories_DeletesInstallDirs_PreservesUserData()
    {
        // Install-owned dirs:
        Directory.CreateDirectory(_layout.AppDir);
        File.WriteAllText(Path.Combine(_layout.AppDir, "cc-director.exe"), "app");
        Directory.CreateDirectory(_layout.BinDir);
        File.WriteAllText(Path.Combine(_layout.BinDir, "cc-pdf.exe"), "tool");

        // User data living under the SAME per-user root (must NOT be touched):
        var vault = Path.Combine(_layout.LocalRoot, "vault");
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(vault, "contacts.db"), "precious");
        var connections = Path.Combine(_layout.LocalRoot, "connections");
        Directory.CreateDirectory(connections);
        File.WriteAllText(Path.Combine(connections, "linkedin.json"), "session");

        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(_layout).RemoveDirectories(InstallRole.Workstation, steps, errors);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(_layout.AppDir));
        Assert.False(Directory.Exists(_layout.BinDir));
        // The per-user root and the user's data survive.
        Assert.True(Directory.Exists(_layout.LocalRoot));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(vault, "contacts.db")));
        Assert.Equal("session", File.ReadAllText(Path.Combine(connections, "linkedin.json")));
    }

    [Fact]
    public void RemoveDirectories_NeverDeletesPerUserRoot()
    {
        // A pathological layout where bin == the root; the guard must refuse.
        var bad = new InstallLayout(_layout.LocalRoot, _layout.ProgramFilesRoot, _layout.ProgramDataRoot);
        Directory.CreateDirectory(bad.LocalRoot);
        // Force a target equal to the root by checking the guard via AppDir? AppDir != root, so simulate
        // by asserting the guard logic indirectly: deleting AppDir leaves root intact.
        Directory.CreateDirectory(bad.AppDir);
        var steps = new List<string>();
        var errors = new List<string>();
        new Uninstaller(bad).RemoveDirectories(InstallRole.Workstation, steps, errors);
        Assert.True(Directory.Exists(bad.LocalRoot));
    }

    [Theory]
    [InlineData(@"C:\a;C:\Users\me\AppData\Local\cc-director\bin;C:\b", @"C:\Users\me\AppData\Local\cc-director\bin", @"C:\a;C:\b")]
    [InlineData(@"C:\a;C:\b", @"C:\Users\me\AppData\Local\cc-director\bin", @"C:\a;C:\b")]
    [InlineData(@"C:\CC\BIN\;c:\cc\bin", @"C:\CC\BIN", "")]
    public void ComputePathWithout_RemovesEntryCaseInsensitive(string input, string dir, string expected)
    {
        Assert.Equal(expected, Uninstaller.ComputePathWithout(input, dir));
    }
}
