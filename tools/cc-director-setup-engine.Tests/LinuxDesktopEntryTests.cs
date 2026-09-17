using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Linux app menu entry. A Linux install used to place the Director and stop, leaving nothing in the
/// app menu. Every test writes under its own temporary data directory, never the real ~/.local/share.
/// </summary>
public class LinuxDesktopEntryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dataHome;
    private readonly string _exe;

    public LinuxDesktopEntryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-desktop-entry-" + Guid.NewGuid().ToString("N"));
        _dataHome = Path.Combine(_dir, "share");
        _exe = Path.Combine(_dir, "app", "cc-director");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void BuildEntry_NamesTheDirectorItsIconAndItsWindow()
    {
        var text = LinuxDesktopEntry.BuildEntry("/home/someone/.local/share/cc-director/app/cc-director");
        var lines = text.Split('\n');

        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains("Name=DevThrottle", lines);
        Assert.Contains("Exec=/home/someone/.local/share/cc-director/app/cc-director", lines);
        Assert.Contains("Path=/home/someone/.local/share/cc-director/app", lines);
        Assert.Contains("Icon=devthrottle", lines);
        Assert.Contains("Terminal=false", lines);
        // The window class the Director actually reports, so the dock matches the window to the entry.
        Assert.Contains("StartupWMClass=cc-director", lines);
    }

    [Theory]
    [InlineData("/opt/dev throttle/cc-director", "\"/opt/dev throttle/cc-director\"")]
    [InlineData("/opt/100%/cc-director", "/opt/100%%/cc-director")]
    [InlineData("/opt/plain/cc-director", "/opt/plain/cc-director")]
    public void QuoteExec_QuotesOnlyWhatTheSpecificationRequires(string path, string expected)
    {
        Assert.Equal(expected, LinuxDesktopEntry.QuoteExec(path));
    }

    [Fact]
    public void Install_WritesTheEntryAndTheIcon()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");

        var written = LinuxDesktopEntry.Install(_exe, _dataHome);

        Assert.True(written);
        var entry = LinuxDesktopEntry.EntryPath(_dataHome);
        Assert.Equal(Path.Combine(_dataHome, "applications", "devthrottle.desktop"), entry);
        Assert.Equal(LinuxDesktopEntry.BuildEntry(_exe), File.ReadAllText(entry));

        var icon = LinuxDesktopEntry.IconPath(_dataHome);
        Assert.Equal(Path.Combine(_dataHome, "icons", "hicolor", "scalable", "apps", "devthrottle.svg"), icon);
        // The real embedded icon, not an empty file.
        Assert.Contains("<svg", File.ReadAllText(icon));
        Assert.False(File.Exists(entry + ".new"));
    }

    [Fact]
    public void Install_Twice_ReplacesTheEntry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");
        var moved = Path.Combine(_dir, "moved", "cc-director");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.WriteAllText(moved, "a director");

        LinuxDesktopEntry.Install(_exe, _dataHome);
        LinuxDesktopEntry.Install(moved, _dataHome);

        Assert.Equal(LinuxDesktopEntry.BuildEntry(moved), File.ReadAllText(LinuxDesktopEntry.EntryPath(_dataHome)));
    }

    [Fact]
    public void Install_WithNoDirectorOnDisk_WritesNothing()
    {
        // An entry that starts nothing is worse than no entry.
        var written = LinuxDesktopEntry.Install(_exe, _dataHome);

        Assert.False(written);
        Assert.False(LinuxDesktopEntry.IsInstalled(_dataHome));
        Assert.False(File.Exists(LinuxDesktopEntry.IconPath(_dataHome)));
    }

    [Fact]
    public void Remove_DeletesTheEntryAndTheIcon_AndAbsentIsNotAnError()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");
        LinuxDesktopEntry.Install(_exe, _dataHome);

        var first = LinuxDesktopEntry.Remove(_dataHome);
        var second = LinuxDesktopEntry.Remove(_dataHome);

        Assert.False(LinuxDesktopEntry.IsInstalled(_dataHome));
        Assert.False(File.Exists(LinuxDesktopEntry.IconPath(_dataHome)));
        Assert.All(first, s => Assert.StartsWith("removed", s));
        Assert.All(second, s => Assert.EndsWith("not present", s));
    }

    [Fact]
    public void Plan_OnLinux_ListsTheAppMenuEntry_NotAStartMenuShortcut()
    {
        if (!OperatingSystem.IsLinux()) return;

        var plan = new Uninstaller(new InstallLayout(Path.Combine(_dir, "local"))).Plan(InstallRole.Workstation);

        var shortcut = Assert.Single(plan, t => t.Kind == UninstallKind.Shortcut);
        Assert.Equal("App menu entry", shortcut.Description);
        Assert.EndsWith(Path.Combine("applications", "devthrottle.desktop"), shortcut.Path);
    }
}
