using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #3503: an install left only a Start Menu shortcut, so a person who closed the wizard had no
/// icon to find their way back by. A first install now puts one on the desktop, and uninstall takes it
/// away. The shortcut is written to a temp folder here - never to the real desktop.
/// </summary>
public sealed class DesktopShortcutTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public DesktopShortcutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-desktop-lnk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void CreateDesktopShortcut_DirectorInstalled_WritesTheShortcut()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exe = _layout.PathFor(ComponentRegistry.Director);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "stand-in for the Director");
        var lnk = Path.Combine(_dir, "Desktop", "DevThrottle.lnk");

        Assert.True(InstallFinalizer.CreateDesktopShortcut(_layout, lnk));
        Assert.True(File.Exists(lnk));
    }

    [Fact]
    public void CreateDesktopShortcut_NoDirector_WritesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var lnk = Path.Combine(_dir, "Desktop", "DevThrottle.lnk");

        Assert.False(InstallFinalizer.CreateDesktopShortcut(_layout, lnk));
        Assert.False(File.Exists(lnk));
    }

    [Fact]
    public void DesktopShortcutPath_IsOnTheDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.Equal(Path.Combine(desktop, "DevThrottle.lnk"), InstallFinalizer.DesktopShortcutPath());
    }

    // Whatever the install puts on the desktop, uninstall must plan to take away.
    [Fact]
    public void UninstallPlan_Windows_IncludesTheDesktopShortcut()
    {
        if (!OperatingSystem.IsWindows()) return;

        var plan = new Uninstaller(_layout).Plan(InstallRole.Workstation);

        Assert.Contains(plan, t => t.Kind == UninstallKind.Shortcut && t.Path == InstallFinalizer.DesktopShortcutPath());
    }
}
