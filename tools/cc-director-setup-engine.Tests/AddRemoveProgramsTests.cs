using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Microsoft.Win32;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Add/Remove Programs registration (issue #257). Uses a throwaway HKCU subkey name so the test
/// never touches the real "CcDirector" registration; Windows-only.
/// </summary>
[SupportedOSPlatform("windows")]
public class AddRemoveProgramsTests : IDisposable
{
    private readonly string _key = "CcDirector_Test_" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AddRemovePrograms.Unregister(_key); } catch { /* best effort */ }
    }

    [Fact]
    public void Register_ThenIsRegistered_ThenUnregister_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.False(AddRemovePrograms.IsRegistered(_key));

        Assert.True(AddRemovePrograms.Register(
            version: "0.7.0",
            uninstallCommand: @"C:\fake\cc-director-setup.exe uninstall",
            installLocation: @"C:\fake\cc-director",
            keyName: _key));

        Assert.True(AddRemovePrograms.IsRegistered(_key));

        Assert.True(AddRemovePrograms.Unregister(_key));
        Assert.False(AddRemovePrograms.IsRegistered(_key));
    }

    [Fact]
    public void Unregister_WhenAbsent_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(AddRemovePrograms.Unregister(_key));
    }

    [Fact]
    public void Register_WritesArpValues()
    {
        if (!OperatingSystem.IsWindows()) return;

        AddRemovePrograms.Register("1.2.3", @"C:\fake\setup.exe uninstall", @"C:\fake\loc", keyName: _key);

        using var k = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{_key}");
        Assert.NotNull(k);
        Assert.Equal("CC Director", k!.GetValue("DisplayName"));
        Assert.Equal("1.2.3", k.GetValue("DisplayVersion"));
        Assert.Equal(@"C:\fake\setup.exe uninstall", k.GetValue("UninstallString"));
        Assert.Equal(1, k.GetValue("NoModify"));
    }
}
