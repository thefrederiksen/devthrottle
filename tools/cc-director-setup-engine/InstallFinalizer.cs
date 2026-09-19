using System.Reflection;
using System.Runtime.Versioning;
using CcDirector.Core.Setup;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The per-user finalization that turns placed files into a usable install: adds the tools bin dir to
/// the user PATH and creates the Start Menu shortcut for the Director. Mirrors what the WPF wizard does
/// (PathManager + ShortcutCreator) so a CLI-driven install ends up identical. Windows-only and
/// idempotent; safe to call after any install/update.
/// </summary>
public static class InstallFinalizer
{
    /// <summary>
    /// Put the master tool directory on the user's saved path and take off the copies it replaces.
    /// Returns true if the saved path changed.
    ///
    /// THREE THINGS THIS HAS TO GET RIGHT, all learned from damage this method did:
    ///
    /// 1. It reads and writes the RAW stored value. It used to go through
    ///    <c>Environment.GetEnvironmentVariable("Path", User)</c>, which returns the path with every
    ///    %VARIABLE% already expanded, and wrote that back - baking one moment's expansion into the
    ///    user's path permanently and destroying every variable reference in it. The raw accessors on
    ///    <see cref="FleetToolPathRepair"/> are the single safe way to touch it.
    ///
    /// 2. It refuses to write a throwaway root into permanent machine state. Every Director repairs
    ///    its own tools, and a Director running from a temporary root (a test rig, a wizard harness,
    ///    an unpacked bundle) would otherwise append ITS temporary bin to the real user path, where it
    ///    outlives the directory by months. That is exactly how
    ///    <c>...\Temp\wizard-harness-home-29ef...\cc-director\bin</c> came to be on a live machine's
    ///    path, pointing at a directory that no longer exists.
    ///
    /// 3. It REMOVES what the master replaces, by the same rule the Director's own start-time repair
    ///    uses. Adding alone was not enough and read as though it were: this install places the tools at
    ///    the machine root, and a per-Director copy left in FRONT of that entry goes on answering every
    ///    command. The install would have put the files in the right place and changed nothing about
    ///    which copy speaks.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool AddBinToPath(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (IsUnderTemp(layout.BinDir))
        {
            EngineLog.Write(
                "[InstallFinalizer] NOT adding to PATH - this install lives under the temp directory "
                + $"and would outlive itself there: {layout.BinDir}");
            return false;
        }

        var current = FleetToolPathRepair.ReadUserPathRaw();
        var rewrite = FleetToolPathRepair.RewriteForInstall(current, layout.BinDir);
        if (rewrite.Path == current) return false;

        FleetToolPathRepair.WriteUserPathRaw(rewrite.Path);
        EngineLog.Write(
            $"[InstallFinalizer] PATH now carries the machine's tools: {layout.BinDir}"
            + (rewrite.Removed.Count == 0
                ? "; nothing it replaces was on the path."
                : $"; removed {rewrite.Removed.Count} replaced entr{(rewrite.Removed.Count == 1 ? "y" : "ies")}: "
                  + string.Join("; ", rewrite.Removed) + ". The files are still on disk."));
        return true;
    }

    /// <summary>
    /// Is this directory inside the machine's temp directory? Compared on full paths so a directory
    /// merely NAMED like temp is not caught, and case-insensitively because Windows paths are.
    /// </summary>
    internal static bool IsUnderTemp(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A path we cannot resolve cannot support the claim that it is under temp, and refusing to
            // add it on a guess would break an ordinary install.
            return false;
        }
    }

    /// <summary>Create (or overwrite) the Start Menu shortcut for the Director. No-op if its exe is absent.</summary>
    [SupportedOSPlatform("windows")]
    public static bool CreateDirectorShortcut(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var exe = layout.PathFor(ComponentRegistry.Director);
        if (!File.Exists(exe)) return false;

        var programsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        Directory.CreateDirectory(programsDir);
        var lnk = Path.Combine(programsDir, "DevThrottle.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM object not available.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        var shortcut = shell.GetType().InvokeMember("CreateShortcut",
            BindingFlags.InvokeMethod, null, shell, [lnk])
            ?? throw new InvalidOperationException("CreateShortcut returned null.");

        var t = shortcut.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [exe]);
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(exe)]);
        t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{exe},0"]);
        t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["DevThrottle"]);
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

        EngineLog.Write($"[InstallFinalizer] created shortcut: {lnk}");
        return true;
    }

    /// <summary>The marker line that brackets the PATH block cc-director appends to a shell rc file.</summary>
    public const string MacPathMarker = "# cc-director: ensure ~/.local/bin on PATH";

    /// <summary>
    /// macOS: ensure ~/.local/bin (where the tool shim symlinks live) is on the user's shell PATH by
    /// appending an idempotent, marker-bracketed block to ~/.zshrc (and ~/.bash_profile if it exists).
    /// Returns true if any file changed. The marker lets <see cref="Uninstaller"/> remove the block.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool EnsureMacUserBinOnPath()
    {
        var block = $"\n{MacPathMarker}\n" +
                    "case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;; *) export PATH=\"$HOME/.local/bin:$PATH\" ;; esac\n";
        var changed = false;
        foreach (var rc in MacShellRcFiles())
        {
            var content = File.Exists(rc) ? File.ReadAllText(rc) : "";
            if (content.Contains(MacPathMarker, StringComparison.Ordinal)) continue;
            File.AppendAllText(rc, block);
            EngineLog.Write($"[InstallFinalizer] added ~/.local/bin to PATH in {rc}");
            changed = true;
        }
        return changed;
    }

    /// <summary>The shell rc files we manage on macOS: ~/.zshrc always; ~/.bash_profile only if present.</summary>
    public static IReadOnlyList<string> MacShellRcFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = new List<string> { Path.Combine(home, ".zshrc") };
        var bashProfile = Path.Combine(home, ".bash_profile");
        if (File.Exists(bashProfile)) files.Add(bashProfile);
        return files;
    }
}
