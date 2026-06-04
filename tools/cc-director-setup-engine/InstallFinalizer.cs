using System.Reflection;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The per-user finalization that turns placed files into a usable install: adds the tools bin dir to
/// the user PATH and creates the Start Menu shortcut for the Director. Mirrors what the WPF wizard does
/// (PathManager + ShortcutCreator) so a CLI-driven install ends up identical. Windows-only and
/// idempotent; safe to call after any install/update.
/// </summary>
public static class InstallFinalizer
{
    /// <summary>Add the tools bin dir to the user PATH if not already present. Returns true if it changed.</summary>
    [SupportedOSPlatform("windows")]
    public static bool AddBinToPath(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var updated = ComputePathWith(current, layout.BinDir);
        if (updated == current) return false;
        // SetEnvironmentVariable(User) persists to the registry and broadcasts WM_SETTINGCHANGE, so new
        // processes pick it up (existing shells still need to be reopened).
        Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
        EngineLog.Write($"[InstallFinalizer] added to PATH: {layout.BinDir}");
        return true;
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
        var lnk = Path.Combine(programsDir, "CC Director.lnk");

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
        t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["CC Director"]);
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

    /// <summary>Return <paramref name="path"/> with <paramref name="dir"/> appended unless already present. Pure.</summary>
    public static string ComputePathWith(string path, string dir)
    {
        var entries = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(e => string.Equals(e.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return path ?? "";
        return string.IsNullOrEmpty(path) ? dir : path.TrimEnd(';') + ";" + dir;
    }
}
