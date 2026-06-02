using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The kind of thing an uninstall step removes.</summary>
public enum UninstallKind { Service, Directory, PathEntry, Shortcut }

/// <summary>One thing the uninstaller would remove, with whether it is currently present.</summary>
public sealed record UninstallTarget(UninstallKind Kind, string Description, string Path, bool Present);

/// <summary>Result of an uninstall run.</summary>
public sealed record UninstallReport(bool Success, IReadOnlyList<string> Steps, IReadOnlyList<string> Errors);

/// <summary>
/// Removes exactly the files the installer creates - and nothing else. It only touches the canonical
/// install locations from <see cref="InstallLayout"/> (the Director app dir, the tools bin dir, the
/// Gateway/Cockpit binaries, the machine service data, the PATH entry, the Start Menu shortcut, and
/// the cc-gateway-service). It NEVER deletes the per-user root itself, so user data that lives
/// alongside the install (vault, connections, config, coaches, dictation, logs) is preserved.
///
/// Stale/orphaned artifacts from older tooling (e.g. a differently-named NSSM service) are out of
/// scope by design - those are handled manually, with explicit approval, not by this uninstaller.
/// </summary>
public sealed class Uninstaller
{
    public const string ServiceName = GatewayServiceCommands.ServiceName;

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
        };
        if (role == InstallRole.Gateway)
        {
            dirs.Add(("Gateway binaries", _layout.GatewayDir));
            dirs.Add(("Cockpit binaries", _layout.CockpitDir));
            dirs.Add(("Service config", _layout.ServiceConfigDir));
            dirs.Add(("Service state", _layout.ServiceStateDir));
            dirs.Add(("Service logs", _layout.ServiceLogsDir));
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
        if (role == InstallRole.Gateway)
            targets.Add(new UninstallTarget(UninstallKind.Service, "Gateway service", ServiceName, ServiceExists()));

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
            RemoveService(steps, errors);

        RemoveDirectories(role, steps, errors);

        if (OperatingSystem.IsWindows())
        {
            RemovePathEntry(steps, errors);
            RemoveEmptyParents(role, steps);
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

    [SupportedOSPlatform("windows")]
    private void RemoveService(List<string> steps, List<string> errors)
    {
        if (!ServiceExists()) { steps.Add($"service {ServiceName}: not present"); return; }
        var (stopExit, _) = ProcessRunner.Run(GatewayServiceCommands.Stop());
        steps.Add($"sc stop {ServiceName} -> exit {stopExit}");
        // give the SCM a moment to release the exe before delete
        Thread.Sleep(1500);
        var (delExit, delOut) = ProcessRunner.Run(GatewayServiceCommands.Delete());
        steps.Add($"sc delete {ServiceName} -> exit {delExit}");
        if (delExit != 0) errors.Add($"service delete failed ({delExit}): {delOut.Trim()}");
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

    /// <summary>Remove the now-empty machine parent dirs (CC Director, cc-director) - but only if empty.</summary>
    private void RemoveEmptyParents(InstallRole role, List<string> steps)
    {
        if (role != InstallRole.Gateway) return;
        foreach (var dir in new[] { _layout.ProgramFilesRoot, _layout.ProgramDataRoot })
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    steps.Add($"removed empty {dir}");
                }
            }
            catch { /* leaving a non-empty/locked parent is fine */ }
        }
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

    private bool ServiceExists()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return ProcessRunner.Run(GatewayServiceCommands.Query()).exit == 0; }
        catch { return false; }
    }
}
