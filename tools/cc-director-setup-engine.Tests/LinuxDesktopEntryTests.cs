using CcDirector.Core.Storage;
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
        var text = LinuxDesktopEntry.BuildEntry(_exe);
        var lines = text.Split('\n');

        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains("Name=DevThrottle", lines);
        Assert.Contains($"Exec={LinuxDesktopEntry.QuoteExec(_exe)}", lines);
        Assert.Contains($"Path={LinuxDesktopEntry.EscapeString(Path.GetDirectoryName(_exe)!)}", lines);
        Assert.Contains("Icon=devthrottle", lines);
        Assert.Contains("Terminal=false", lines);
        // The window class the Director actually reports, so the dock matches the window to the entry.
        Assert.Contains("StartupWMClass=cc-director", lines);
    }

    [Theory]
    [InlineData("/opt/dev throttle/cc-director", "\"/opt/dev throttle/cc-director\"")]
    [InlineData("/opt/100%/cc-director", "/opt/100%%/cc-director")]
    [InlineData("/opt/plain/cc-director", "/opt/plain/cc-director")]
    // Inside quotes $ is backslash-escaped, and that backslash is then doubled by string escaping.
    [InlineData("/opt/a$b/cc-director", "\"/opt/a\\\\$b/cc-director\"")]
    // A literal backslash: escaped for the quotes, and both backslashes doubled again - four in the file.
    [InlineData("/opt/a\\b/cc-director", "\"/opt/a\\\\\\\\b/cc-director\"")]
    public void QuoteExec_QuotesAndEscapesAsTheSpecificationRequires(string path, string expected)
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
    public void Icon_OpensWithTheSvgElement_WhereTheLinuxImageLoaderLooksForIt()
    {
        // gdk-pixbuf only recognises a file as SVG when "<svg" starts within its first 256 bytes. The icon
        // used to open with a 900-byte comment, so every Linux desktop failed to load it and DevThrottle
        // showed as a blank tile in the dock and the app grid.
        var icon = LinuxDesktopEntry.IconBytes();
        var head = System.Text.Encoding.UTF8.GetString(icon, 0, Math.Min(icon.Length, 256));

        var at = head.IndexOf("<svg", StringComparison.Ordinal);

        Assert.True(at >= 0, $"\"<svg\" must appear within the first 256 bytes of the icon. The file opens with:\n{head}");
    }

    [Fact]
    public void Repair_ForADirectorThatIsNotTheInstalledOne_WritesNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");
        var devBuild = Path.Combine(_dir, "checkout", "bin", "cc-director");

        var outcome = LinuxDesktopEntry.Repair(devBuild, _exe, _dataHome);

        Assert.Equal(LinuxDesktopEntry.RepairOutcome.NotTheInstalledDirector, outcome);
        Assert.False(LinuxDesktopEntry.IsInstalled(_dataHome));
        Assert.False(File.Exists(LinuxDesktopEntry.IconPath(_dataHome)));
    }

    [Fact]
    public void Repair_WithNoEntry_WritesTheEntryAndTheIcon()
    {
        // An install from before the setup engine wrote an entry at all.
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");

        var outcome = LinuxDesktopEntry.Repair(_exe, _exe, _dataHome);

        Assert.Equal(LinuxDesktopEntry.RepairOutcome.Rewritten, outcome);
        Assert.Equal(LinuxDesktopEntry.BuildEntry(_exe), File.ReadAllText(LinuxDesktopEntry.EntryPath(_dataHome)));
        Assert.Equal(LinuxDesktopEntry.IconBytes(), File.ReadAllBytes(LinuxDesktopEntry.IconPath(_dataHome)));
    }

    [Fact]
    public void Repair_WithAStaleIcon_ReplacesIt()
    {
        // The case this exists for: an earlier build installed an icon no desktop could load, and updates
        // since then swapped only the executable.
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");
        LinuxDesktopEntry.Install(_exe, _dataHome);
        var iconPath = LinuxDesktopEntry.IconPath(_dataHome);
        File.WriteAllText(iconPath, "<?xml version=\"1.0\"?>\n<!-- an old icon -->\n<svg/>");

        var outcome = LinuxDesktopEntry.Repair(_exe, _exe, _dataHome);

        Assert.Equal(LinuxDesktopEntry.RepairOutcome.Rewritten, outcome);
        Assert.Equal(LinuxDesktopEntry.IconBytes(), File.ReadAllBytes(iconPath));
    }

    [Fact]
    public void Repair_WhenEverythingIsCurrent_WritesNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");
        LinuxDesktopEntry.Install(_exe, _dataHome);
        var entryPath = LinuxDesktopEntry.EntryPath(_dataHome);
        var iconPath = LinuxDesktopEntry.IconPath(_dataHome);
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(entryPath, past);
        File.SetLastWriteTimeUtc(iconPath, past);

        var outcome = LinuxDesktopEntry.Repair(_exe, _exe, _dataHome);

        Assert.Equal(LinuxDesktopEntry.RepairOutcome.UpToDate, outcome);
        Assert.Equal(past, File.GetLastWriteTimeUtc(entryPath));
        Assert.Equal(past, File.GetLastWriteTimeUtc(iconPath));
    }

    [Fact]
    public void Repair_ThroughASymbolicLinkInTheInstallPath_StillRecognisesTheInstall()
    {
        // The running path comes from /proc/self/exe with every link resolved, while the install path is
        // composed from the home directory - which on Fedora Silverblue and its kind is a link to /var/home.
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");
        var linkedHome = Path.Combine(_dir, "linked");
        Directory.CreateSymbolicLink(linkedHome, _dir);
        var throughTheLink = Path.Combine(linkedHome, "app", "cc-director");

        var outcome = LinuxDesktopEntry.Repair(_exe, throughTheLink, _dataHome);

        Assert.Equal(LinuxDesktopEntry.RepairOutcome.Rewritten, outcome);
        Assert.Equal(_exe, LinuxDesktopEntry.RealPath(throughTheLink));
    }

    [Fact]
    public void Repair_WithAStaleEntry_ReplacesIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a director");
        LinuxDesktopEntry.Install(_exe, _dataHome);
        var entryPath = LinuxDesktopEntry.EntryPath(_dataHome);
        File.WriteAllText(entryPath, LinuxDesktopEntry.BuildEntry(Path.Combine(_dir, "old-place", "cc-director")));

        var outcome = LinuxDesktopEntry.Repair(_exe, _exe, _dataHome);

        Assert.Equal(LinuxDesktopEntry.RepairOutcome.Rewritten, outcome);
        Assert.Equal(LinuxDesktopEntry.BuildEntry(_exe), File.ReadAllText(entryPath));
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

/// <summary>
/// Which Director counts as installed. Kept apart because it moves the process-wide CC_DIRECTOR_ROOT setting.
/// </summary>
[Collection(MachineRootCollection.Name)]
public sealed class LinuxDesktopEntryInstalledDirectorTests : IDisposable
{
    private const string RootVariable = "CC_DIRECTOR_ROOT";
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(RootVariable);

    public void Dispose() => Environment.SetEnvironmentVariable(RootVariable, _previousRoot);

    [Fact]
    public void InstalledDirector_IgnoresARigRoot()
    {
        // A Director placed in a test rig and started with CC_DIRECTOR_ROOT on that rig must not count as
        // the machine's install, or it would write the throwaway rig into the person's app menu.
        var rig = Path.Combine(Path.GetTempPath(), "cc-rig-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(RootVariable, rig);

        var installed = LinuxDesktopEntry.InstalledDirector();

        Assert.DoesNotContain(rig, installed);
        Assert.Equal(new InstallLayout(CcStorage.DefaultRoot()).PathFor(ComponentRegistry.Director), installed);
    }
}
