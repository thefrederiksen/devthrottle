using System.Diagnostics;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The kind of thing an uninstall step removes.</summary>
public enum UninstallKind { Autostart, Directory, PathEntry, Shortcut }

/// <summary>One thing the uninstaller would remove, with whether it is currently present.</summary>
public sealed record UninstallTarget(UninstallKind Kind, string Description, string Path, bool Present);

/// <summary>Result of an uninstall run.</summary>
public sealed record UninstallReport(bool Success, IReadOnlyList<string> Steps, IReadOnlyList<string> Errors);

/// <summary>
/// Removes exactly the files the installer creates - and nothing else. It only touches the canonical
/// install locations from <see cref="InstallLayout"/> (the Director app dir, the tools bin dir, the
/// Gateway/Cockpit binaries, setup state, the PATH entry, the Start Menu shortcut, and the Gateway
/// tray app's autostart Run key). It NEVER deletes the per-user root itself, so user data that lives
/// alongside the install (vault, connections, config, coaches, dictation, logs) is preserved.
///
/// Stale/orphaned artifacts from older tooling are out of scope by design - those are handled
/// manually, with explicit approval, not by this uninstaller.
/// </summary>
public sealed class Uninstaller
{
    private readonly InstallLayout _layout;

    public Uninstaller(InstallLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    /// <summary>The directories this uninstaller is allowed to remove, by role. Used by Plan + Apply.</summary>
    private IReadOnlyList<(string Desc, string Path)> Directories(InstallRole role)
    {
        var dirs = new List<(string, string)>
        {
            ("Director app", _layout.AppDir),
            ("CLI tools", _layout.BinDir),
            ("Python runtime", _layout.PythonDir),
            ("Python tools venv", _layout.PyenvDir),
        };
        if (role == InstallRole.Gateway)
        {
            dirs.Add(("Gateway binaries", _layout.GatewayDir));
            dirs.Add(("Cockpit binaries", _layout.CockpitDir));
            dirs.Add(("Setup state", _layout.StateDir));
        }
        return dirs;
    }

    private string ShortcutPath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "CC Director.lnk");

    /// <summary>What an uninstall would remove (existence-checked). Pure: no side effects.</summary>
    public IReadOnlyList<UninstallTarget> Plan(InstallRole role)
    {
        var targets = new List<UninstallTarget>();
        if (role == InstallRole.Gateway && OperatingSystem.IsWindows())
            targets.Add(new UninstallTarget(
                UninstallKind.Autostart, "Gateway autostart (HKCU Run key)",
                GatewayAutostart.ValueName, GatewayAutostart.IsRegistered()));

        foreach (var (desc, path) in Directories(role))
            targets.Add(new UninstallTarget(UninstallKind.Directory, desc, path, Directory.Exists(path)));

        targets.Add(new UninstallTarget(UninstallKind.PathEntry, "PATH entry", _layout.BinDir, IsBinOnUserPath()));
        var lnk = ShortcutPath();
        targets.Add(new UninstallTarget(UninstallKind.Shortcut, "Start Menu shortcut", lnk, File.Exists(lnk)));
        return targets;
    }

    /// <summary>Remove everything in scope for the role. Best-effort: collects per-step errors.</summary>
    public UninstallReport Apply(InstallRole role)
    {
        var steps = new List<string>();
        var errors = new List<string>();
        EngineLog.Write($"[Uninstaller] Apply role={role}");

        if (role == InstallRole.Gateway && OperatingSystem.IsWindows())
        {
            StopGatewayTrayApp(steps);
            RemoveAutostart(steps, errors);
        }

        RemoveDirectories(role, steps, errors);

        if (OperatingSystem.IsWindows())
        {
            RemovePathEntry(steps, errors);
        }
        else
        {
            RemoveMacArtifacts(steps, errors);
        }

        RemoveShortcut(steps, errors);

        var ok = errors.Count == 0;
        EngineLog.Write($"[Uninstaller] Apply done: success={ok}, errors={errors.Count}");
        return new UninstallReport(ok, steps, errors);
    }

    /// <summary>Delete only the install-owned directories (never the per-user root or sibling data dirs).</summary>
    public void RemoveDirectories(InstallRole role, List<string> steps, List<string> errors)
    {
        foreach (var (desc, path) in Directories(role))
        {
            // Hard guard: never delete the per-user root itself.
            if (string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(_layout.LocalRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"refused to delete the per-user root ({path})");
                continue;
            }
            if (!Directory.Exists(path)) { steps.Add($"{desc}: not present ({path})"); continue; }
            try
            {
                Directory.Delete(path, recursive: true);
                steps.Add($"removed {desc}: {path}");
            }
            catch (Exception ex)
            {
                errors.Add($"{desc} ({path}): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stop the INSTALLED Gateway tray app (and the Cockpit it supervises) so their exes unlock
    /// before the directory delete. Scoped strictly to processes whose image lives under the
    /// install-owned Gateway/Cockpit dirs - a dev gateway running from a repo is never touched.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void StopGatewayTrayApp(List<string> steps)
    {
        var stopped = 0;
        foreach (var name in new[] { "cc-director-gateway", "cc-director-cockpit" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    var inGateway = path.StartsWith(_layout.GatewayDir, StringComparison.OrdinalIgnoreCase);
                    var inCockpit = path.StartsWith(_layout.CockpitDir, StringComparison.OrdinalIgnoreCase);
                    if (!inGateway && !inCockpit) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    stopped++;
                }
                catch (Exception ex) { EngineLog.Write($"[Uninstaller] stop {name} pid={p.Id}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
        }
        steps.Add(stopped > 0
            ? $"stopped {stopped} installed Gateway/Cockpit process(es)"
            : "Gateway tray app: not running");
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveAutostart(List<string> steps, List<string> errors)
    {
        try
        {
            steps.Add(GatewayAutostart.Unregister()
                ? $"removed autostart Run key ({GatewayAutostart.ValueName})"
                : "autostart Run key: not present");
        }
        catch (Exception ex)
        {
            errors.Add($"autostart Run key: {ex.Message}");
        }
    }

    /// <summary>
    /// macOS removals the cross-platform directory pass does not cover: the Director .app in
    /// ~/Applications, the ~/.local/bin shim symlinks that point into our pyenv, and the PATH block
    /// cc-director appended to the shell rc files. (PythonDir/PyenvDir are removed by RemoveDirectories.)
    /// </summary>
    private void RemoveMacArtifacts(List<string> steps, List<string> errors)
    {
        // 1. The Director .app.
        var app = _layout.PathFor(ComponentRegistry.Director);
        try
        {
            if (Directory.Exists(app)) { Directory.Delete(app, recursive: true); steps.Add($"removed Director app: {app}"); }
            else steps.Add($"Director app: not present ({app})");
        }
        catch (Exception ex) { errors.Add($"Director app ({app}): {ex.Message}"); }

        // 2. Our shim symlinks in ~/.local/bin (only those pointing into our pyenv).
        var userBin = _layout.MacUserBinDir;
        if (Directory.Exists(userBin))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(userBin))
            {
                try
                {
                    var target = new FileInfo(entry).LinkTarget;
                    if (target is not null && target.StartsWith(_layout.PyenvDir, StringComparison.Ordinal))
                    {
                        File.Delete(entry);
                        steps.Add($"removed shim: {entry}");
                    }
                }
                catch (Exception ex) { errors.Add($"shim ({entry}): {ex.Message}"); }
            }
        }

        // 3. The PATH block in the shell rc files.
        foreach (var rc in InstallFinalizer.MacShellRcFiles())
            RemoveMacPathBlock(rc, steps, errors);
    }

    /// <summary>Strip the marker line + its following PATH line that EnsureMacUserBinOnPath appended.</summary>
    private static void RemoveMacPathBlock(string rc, List<string> steps, List<string> errors)
    {
        if (!File.Exists(rc)) return;
        try
        {
            var lines = File.ReadAllLines(rc).ToList();
            var idx = lines.FindIndex(l => l.Trim() == InstallFinalizer.MacPathMarker);
            if (idx < 0) { steps.Add($"PATH block: not present in {rc}"); return; }
            // Remove the marker line and the export line that follows it.
            var count = (idx + 1 < lines.Count) ? 2 : 1;
            lines.RemoveRange(idx, count);
            File.WriteAllLines(rc, lines);
            steps.Add($"removed PATH block from {rc}");
        }
        catch (Exception ex) { errors.Add($"PATH block ({rc}): {ex.Message}"); }
    }

    [SupportedOSPlatform("windows")]
    private void RemovePathEntry(List<string> steps, List<string> errors)
    {
        try
        {
            var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var updated = ComputePathWithout(current, _layout.BinDir);
            if (updated == current) { steps.Add("PATH entry: not present"); return; }
            Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
            steps.Add($"removed PATH entry: {_layout.BinDir}");
        }
        catch (Exception ex)
        {
            errors.Add($"PATH entry: {ex.Message}");
        }
    }

    private void RemoveShortcut(List<string> steps, List<string> errors)
    {
        var lnk = ShortcutPath();
        if (!File.Exists(lnk)) { steps.Add("Start Menu shortcut: not present"); return; }
        try { File.Delete(lnk); steps.Add($"removed Start Menu shortcut: {lnk}"); }
        catch (Exception ex) { errors.Add($"shortcut ({lnk}): {ex.Message}"); }
    }

    /// <summary>Return <paramref name="path"/> with <paramref name="dir"/> removed (case-insensitive). Pure.</summary>
    public static string ComputePathWithout(string path, string dir)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var kept = path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => !string.Equals(e.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        return string.Join(";", kept);
    }

    private bool IsBinOnUserPath()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        return ComputePathWithout(current, _layout.BinDir) != current;
    }
}
