using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The Linux application-menu entry for the Director: a freedesktop ".desktop" file and its icon, both
/// per user under the XDG data directory (normally ~/.local/share). This is what makes "DevThrottle"
/// appear when a person opens the app menu and types its name - the Linux counterpart of the Windows
/// Start Menu shortcut.
///
/// Before this existed a Linux install placed the Director and stopped. Nothing in the menu, no icon,
/// and the only way to start it was to find the binary by hand - which reads to a person who does not
/// know Linux as "the installer did not work".
///
/// Per user and without administrator rights, like the rest of the install. Idempotent: every install
/// and update rewrites both files, so a moved install location is corrected on the next run.
/// </summary>
public static class LinuxDesktopEntry
{
    /// <summary>File name of the entry, and the icon name it refers to.</summary>
    public const string EntryFileName = "devthrottle.desktop";

    /// <summary>Icon name, resolved by the desktop through the hicolor icon theme.</summary>
    public const string IconName = "devthrottle";

    /// <summary>
    /// The window class the Director's window reports. Naming it lets the dock match the running window
    /// to this entry, so the window shows the DevThrottle icon and name instead of a generic one.
    /// </summary>
    public const string DirectorWindowClass = "cc-director";

    private const string IconResourceName = "devthrottle.svg";

    /// <summary>The per-user XDG data directory: $XDG_DATA_HOME when set, otherwise ~/.local/share.</summary>
    public static string DefaultDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)) return xdg;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    /// <summary>Where the entry file lives under <paramref name="dataHome"/>.</summary>
    public static string EntryPath(string dataHome) => Path.Combine(dataHome, "applications", EntryFileName);

    /// <summary>Where the icon lives under <paramref name="dataHome"/>.</summary>
    public static string IconPath(string dataHome)
        => Path.Combine(dataHome, "icons", "hicolor", "scalable", "apps", IconName + ".svg");

    /// <summary>
    /// The text of the entry for a Director at <paramref name="directorExe"/>. Pure. The executable is
    /// quoted in Exec when its path needs it, as the desktop entry specification requires.
    /// </summary>
    public static string BuildEntry(string directorExe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directorExe);
        var workingDirectory = Path.GetDirectoryName(directorExe) ?? "";
        return string.Join("\n",
            "[Desktop Entry]",
            "Type=Application",
            "Name=DevThrottle",
            "Comment=Run and watch your coding sessions",
            $"Exec={QuoteExec(directorExe)}",
            $"Path={workingDirectory}",
            $"Icon={IconName}",
            "Terminal=false",
            "Categories=Development;",
            $"StartupWMClass={DirectorWindowClass}",
            "");
    }

    /// <summary>
    /// Write the entry and the icon for the installed Director. Returns false, writing nothing, when the
    /// Director executable is not on disk - an entry that starts nothing is worse than no entry.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool Install(InstallLayout layout) => Install(layout.PathFor(ComponentRegistry.Director), DefaultDataHome());

    /// <summary>Write the entry and icon under <paramref name="dataHome"/>. See <see cref="Install(InstallLayout)"/>.</summary>
    public static bool Install(string directorExe, string dataHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directorExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataHome);
        EngineLog.Write($"[LinuxDesktopEntry] Install: exe={directorExe}, dataHome={dataHome}");

        if (!File.Exists(directorExe))
        {
            EngineLog.Write($"[LinuxDesktopEntry] Install: no Director at {directorExe}; no menu entry written");
            return false;
        }

        var iconPath = IconPath(dataHome);
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        using (var icon = typeof(LinuxDesktopEntry).Assembly.GetManifestResourceStream(IconResourceName)
                          ?? throw new InvalidOperationException($"The embedded icon '{IconResourceName}' is missing from the setup engine."))
        using (var file = File.Create(iconPath))
        {
            icon.CopyTo(file);
        }

        // Written beside the target and renamed over it, so a menu that re-reads the directory mid-write
        // never sees half an entry.
        var entryPath = EntryPath(dataHome);
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        var staging = entryPath + ".new";
        File.WriteAllText(staging, BuildEntry(directorExe));
        File.Move(staging, entryPath, overwrite: true);

        EngineLog.Write($"[LinuxDesktopEntry] Install: wrote {entryPath} and {iconPath}");
        return true;
    }

    /// <summary>Whether an entry is present under <paramref name="dataHome"/>.</summary>
    public static bool IsInstalled(string dataHome) => File.Exists(EntryPath(dataHome));

    /// <summary>
    /// Remove the entry and the icon. Absent is success. Returns a line per file for the uninstall report.
    /// </summary>
    public static IReadOnlyList<string> Remove(string dataHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataHome);
        EngineLog.Write($"[LinuxDesktopEntry] Remove: dataHome={dataHome}");
        var steps = new List<string>();
        foreach (var (what, path) in new[] { ("app menu entry", EntryPath(dataHome)), ("app menu icon", IconPath(dataHome)) })
        {
            if (!File.Exists(path)) { steps.Add($"{what}: not present"); continue; }
            File.Delete(path);
            steps.Add($"removed {what}: {path}");
        }
        return steps;
    }

    /// <summary>Quote a path for an Exec line when it contains a character the specification reserves.</summary>
    internal static string QuoteExec(string path)
    {
        // A percent sign starts a field code in an Exec line, so a literal one is always doubled.
        path = path.Replace("%", "%%");
        const string reserved = " \t\n\"'\\><~|&;$*?#()`";
        if (path.IndexOfAny(reserved.ToCharArray()) < 0) return path;
        var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        return $"\"{escaped}\"";
    }
}
