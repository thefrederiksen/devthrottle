using System.Diagnostics;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The kind of thing an uninstall step removes.</summary>
public enum UninstallKind { Autostart, Directory, PathEntry, Shortcut, ScheduledTask, TailscaleServe, ArpEntry }

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
            // The Launcher tray app ships to BOTH roles (issue #250), so its binaries are removed
            // regardless of role.
            ("Launcher binaries", _layout.LauncherDir),
            // The retained setup executable that Apps & features points at. Removable because the
            // uninstall runs from a temp copy, never from this directory - see UninstallRegistration.
            ("Setup executable", _layout.SetupDir),
        };
        if (!OperatingSystem.IsWindows())
        {
            // On macOS the Director is the .app bundle in ~/Applications (the Windows AppDir above
            // simply does not exist there). Include the pre-rename bundle name too (issue #1821) so
            // an uninstall on a legacy host removes the app it actually has.
            dirs.Add(("Director app bundle", _layout.PathFor(ComponentRegistry.Director)));
            foreach (var alias in _layout.LegacyAliasesFor(ComponentRegistry.Director))
                dirs.Add(("Director app bundle (pre-rename name)", alias));
        }
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
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "DevThrottle.lnk");

    /// <summary>What an uninstall would remove (existence-checked). Pure: no side effects.</summary>
    public IReadOnlyList<UninstallTarget> Plan(InstallRole role)
    {
        var targets = new List<UninstallTarget>();
        if (role == InstallRole.Gateway && OperatingSystem.IsWindows())
            targets.Add(new UninstallTarget(
                UninstallKind.Autostart, "Gateway autostart (HKCU Run key)",
                GatewayAutostart.ValueName, GatewayAutostart.IsRegistered()));

        // The Launcher autostart Run key exists for both roles (the launcher ships to both).
        if (OperatingSystem.IsWindows())
            targets.Add(new UninstallTarget(
                UninstallKind.Autostart, "Launcher autostart (HKCU Run key)",
                LauncherAutostart.ValueName, LauncherAutostart.IsRegistered()));
        else if (OperatingSystem.IsMacOS())
            targets.Add(new UninstallTarget(
                UninstallKind.Autostart, "Launcher launch agent (launchd)",
                LauncherLaunchdAutostart.PlistPath, LauncherLaunchdAutostart.IsRegistered()));

        foreach (var (desc, path) in Directories(role))
            targets.Add(new UninstallTarget(UninstallKind.Directory, desc, path, Directory.Exists(path)));

        targets.Add(new UninstallTarget(UninstallKind.PathEntry, "PATH entry", _layout.BinDir, IsBinOnUserPath()));
        if (OperatingSystem.IsLinux())
        {
            var entry = LinuxDesktopEntry.EntryPath(LinuxDesktopEntry.DefaultDataHome());
            targets.Add(new UninstallTarget(UninstallKind.Shortcut, "App menu entry", entry, File.Exists(entry)));
        }
        else
        {
            var lnk = ShortcutPath();
            targets.Add(new UninstallTarget(UninstallKind.Shortcut, "Start Menu shortcut", lnk, File.Exists(lnk)));
        }

        // Add/Remove Programs registration (issue #257), Windows only. Cheap registry read.
        if (OperatingSystem.IsWindows())
            targets.Add(new UninstallTarget(
                UninstallKind.ArpEntry, "Add/Remove Programs entry",
                $@"HKCU\...\Uninstall\{AddRemovePrograms.DefaultKeyName}", AddRemovePrograms.IsRegistered()));

        return targets;
    }

    /// <summary>
    /// Remove everything in scope for the role. Best-effort: collects per-step errors.
    /// <paramref name="progress"/> (optional) reports a friendly, present-tense message as each
    /// phase begins, so a UI can show live progress instead of a frozen window.
    /// <paramref name="deleteData"/> (issue #261, default FALSE) ALSO removes the entire per-user
    /// data root (config, vault secrets, signed-in browser sessions, recordings, logs) as a final
    /// step - an explicit, opt-in full wipe. Default keeps the data exactly as before.
    /// </summary>
    public UninstallReport Apply(InstallRole role, IProgress<string>? progress = null, bool deleteData = false)
    {
        var steps = new List<string>();
        var errors = new List<string>();
        EngineLog.Write($"[Uninstaller] Apply role={role}, deleteData={deleteData}");

        if (role == InstallRole.Gateway && OperatingSystem.IsWindows())
        {
            progress?.Report("Stopping the Gateway tray app");
            StopGatewayTrayApp(steps);
            progress?.Report("Removing the Gateway autostart");
            RemoveAutostart(steps, errors);
            // The 443 front-door Serve mapping is the Gateway's, so its teardown is Gateway-scoped.
            progress?.Report("Removing the Tailscale mapping");
            RemoveTailscaleServe(steps, errors);
        }

        // The launcher ships to both roles. Stop it BY PROCESS, on BOTH platforms, before the
        // directory delete - and before any wipe, because the token that lets us ask it politely
        // lives inside the tree a wipe removes.
        //
        // macOS used to only ask launchd to boot the service out. That does nothing for a launcher
        // launchd never started, and reported success, so the product manufactured a process its own
        // uninstaller could not remove and every later install collided with it. LauncherStopper is
        // the shared fix; the launchd unregister below still runs, because a launchd-owned launcher
        // must also lose its property list or launchd resurrects a binary the next step deletes.
        // AUTOSTART FIRST, then the process. On macOS the launch agent has KeepAlive, so a launcher
        // killed while launchd still owns its job is simply restarted - and the restart binds the port
        // again after the stop has already reported success. Unregistering first removes the thing that
        // would resurrect it.
        if (OperatingSystem.IsWindows())
        {
            progress?.Report("Removing the Launcher autostart");
            RemoveLauncherAutostart(steps, errors);
        }
        else if (OperatingSystem.IsMacOS())
        {
            progress?.Report("Removing the Launcher launch agent");
            try
            {
                // VERIFIED: a nonzero bootout used to be logged and reported as success. A job still
                // loaded keeps its KeepAlive definition, so launchd restarts the launcher after the stop
                // below has already been certified - and the restart binds the port again while the
                // files are being deleted.
                if (LauncherLaunchdAutostart.UnregisterVerified(out var agentFailure))
                    steps.Add("Removed the Launcher launch agent (launchd)");
                else
                    errors.Add($"Launcher launch agent: {agentFailure}. launchd may restart the launcher, "
                               + "so this uninstall cannot be trusted to have stopped it.");
            }
            catch (Exception ex)
            {
                errors.Add($"Launcher launch agent: {ex.Message}");
            }
        }

        progress?.Report("Stopping the launcher");
        var stop = new LauncherStopper(_layout).Stop();
        steps.AddRange(stop.Steps);
        if (!stop.Stopped)
            errors.Add("The installed launcher is still running. A later install will collide with it. "
                       + "See the steps above for what was tried.");

        progress?.Report("Removing the app and CLI tools");
        if (stop.Stopped)
        {
            RemoveDirectories(role, steps, errors);
        }
        else
        {
            // Deleting the binary of a process that is still running is exactly how the original
            // orphan became unstoppable: a live launcher serving a port from files that no longer
            // existed, which no uninstall could then identify. Leave the files, say so, and let the
            // user stop it and re-run.
            steps.Add("SKIPPED removing the app and CLI tools: the launcher is still running, and "
                      + "deleting its files while it runs is what creates an unstoppable launcher");
        }

        if (OperatingSystem.IsWindows())
        {
            progress?.Report("Removing the PATH entry");
            RemovePathEntry(steps, errors);
        }
        else
        {
            progress?.Report("Removing the shell PATH entries and shims");
            RemoveMacArtifacts(steps, errors);
        }

        if (OperatingSystem.IsLinux())
        {
            progress?.Report("Removing the app menu entry");
            RemoveLinuxDesktopEntry(steps, errors);
        }
        else
        {
            progress?.Report("Removing the Start Menu shortcut");
            RemoveShortcut(steps, errors);
        }

        // Integration points common to both roles (issue #257). Scheduled tasks are per-user and
        // role-independent; the Add/Remove Programs entry is Windows-only.
        progress?.Report("Removing scheduled tasks");
        RemoveScheduledTasks(steps, errors);
        if (OperatingSystem.IsWindows())
        {
            progress?.Report("Removing the Apps & features entry");
            RemoveArpEntry(steps, errors);
        }

        // Opt-in full wipe (issue #261): LAST, after the install-owned removals above, nuke the
        // whole per-user data root. Deliberately destructive, so it only runs when asked.
        if (deleteData)
        {
            if (stop.Stopped)
            {
                progress?.Report("Removing your data");
                WipeUserData(steps, errors);
            }
            else
            {
                // The wipe deletes the WHOLE per-user root, and the launcher directory lives inside it -
                // so wiping would delete the binary and the token of a process we could not stop, which
                // is precisely the state that made the original orphan impossible to identify or
                // authenticate to. The deletion gate above is worthless if this path ignores it.
                steps.Add("SKIPPED removing your data: the launcher is still running, and the wipe would "
                          + "delete the files and token of a process nothing can then stop");
            }
        }

        var ok = errors.Count == 0;
        EngineLog.Write($"[Uninstaller] Apply done: success={ok}, errors={errors.Count}");
        return new UninstallReport(ok, steps, errors);
    }

    /// <summary>
    /// Delete the ENTIRE per-user root (config, vault secrets, signed-in browser sessions,
    /// recordings, logs) - the opt-in full wipe (issue #261). Guarded: only proceeds when the root
    /// actually ends in "cc-director", so a mis-set <see cref="InstallLayout.LocalRoot"/> can never
    /// wipe an arbitrary directory. Injectable nothing - it operates on the layout's own root.
    /// </summary>
    public void WipeUserData(List<string> steps, List<string> errors)
    {
        var root = System.IO.Path.GetFullPath(_layout.LocalRoot);
        // Safety: refuse anything that is not a per-user DevThrottle root.
        var leaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root));
        if (!string.Equals(leaf, "cc-director", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"refused to wipe data: '{root}' is not a cc-director root");
            return;
        }
        if (!Directory.Exists(root)) { steps.Add($"data: not present ({root})"); return; }

        // Keep the logs. They are the only account of how this installation behaved, and a wipe is
        // very often followed by an install that fails BECAUSE of what the old one did - which is
        // exactly what happened on the Mac: the line explaining a failed autostart registration was
        // in a log deleted while a process still held it open, and on macOS a deleted-but-open file
        // cannot be read from another process without root. That cause is permanently unknown.
        var kept = PreserveLogs(root, steps, errors);

        try
        {
            Directory.Delete(root, recursive: true);
            steps.Add(kept is null
                ? $"removed all data: {root}"
                : $"removed all data: {root} (logs kept at {kept})");
        }
        catch (Exception ex)
        {
            errors.Add($"data ({root}): {ex.Message}");
        }
    }

    /// <summary>
    /// Copy <c>logs/</c> out of the root about to be deleted, into a sibling that survives the wipe.
    /// Best effort by design: failing to keep the logs must not stop the uninstall the user asked for,
    /// but it is reported so nobody later believes logs exist when they do not.
    /// </summary>
    /// <returns>Where the logs were kept, or null when there were none or the copy failed.</returns>
    public string? PreserveLogs(string root, List<string> steps, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(errors);

        var logs = System.IO.Path.Combine(root, "logs");
        if (!Directory.Exists(logs)) return null;

        // A sibling of the root, named for what it is. Not inside the root - that is the thing going away.
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(root));
        if (string.IsNullOrEmpty(parent)) return null;
        // NOT named after the root. "cc-director-logs-kept" would be a string PREFIX of the
        // "cc-director" root, which quietly defeats any prefix-based "is this inside the root"
        // check - including the one guarding the wipe itself.
        // Unique per wipe: a fixed name would have the second uninstall overwrite the first one's
        // logs, and the whole point is not to lose them. Resolved and re-checked below, because a
        // symbolic link or junction called this could point back inside the root about to be deleted.
        // Unique even within the same second: two wipes a second apart would otherwise share a
        // directory and overwrite each other's logs, which is the loss this exists to prevent.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var destination = System.IO.Path.Combine(parent, $"devthrottle-logs-kept-{stamp}");
        for (var n = 2; Directory.Exists(destination) || File.Exists(destination); n++)
            destination = System.IO.Path.Combine(parent, $"devthrottle-logs-kept-{stamp}-{n}");

        try
        {
            if (Directory.Exists(destination))
            {
                // A link target may be RELATIVE, and relative to the LINK's directory - not to this
                // process's working directory, which is what GetFullPath would assume.
                var info = new DirectoryInfo(destination);
                var resolved = info.LinkTarget is { } target
                    ? System.IO.Path.GetFullPath(target, info.Parent?.FullName ?? parent)
                    : System.IO.Path.GetFullPath(destination);
                if (resolved.StartsWith(System.IO.Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add($"not keeping the logs: {destination} resolves back inside {root}, which is being deleted");
                    return null;
                }
            }

            CopyDirectory(logs, destination);
            steps.Add($"kept the previous installation's logs at {destination}");
            return destination;
        }
        catch (Exception ex)
        {
            // Reported, never silent - but NOT an error, because that would flip the whole uninstall to
            // failure over a courtesy copy, telling the user their uninstall failed when it did exactly
            // what they asked.
            steps.Add($"could not keep the logs ({logs}): {ex.Message}");
            return null;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file));
            try
            {
                File.Copy(file, target, overwrite: true);
            }
            catch (IOException)
            {
                // A log still held open by a live process can refuse a plain copy; read it with
                // sharing instead. This is the case that matters - the log we most want is the one
                // belonging to the process that is still running.
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(dir)));
    }

    /// <summary>Remove DevThrottle's scheduled tasks if present (issue #257). Absent tasks are
    /// reported as skipped, never errors. <paramref name="runner"/> is injectable for tests.</summary>
    public void RemoveScheduledTasks(List<string> steps, List<string> errors, ScheduledTaskRemover.Runner? runner = null)
    {
        foreach (var r in ScheduledTaskRemover.RemoveAll(runner))
        {
            if (!r.Present) steps.Add($"scheduled task '{r.TaskName}': not present");
            else if (r.Removed) steps.Add($"removed scheduled task '{r.TaskName}'");
            else errors.Add($"scheduled task '{r.TaskName}': {r.Error ?? "removal failed"}");
        }
    }

    /// <summary>Tear down DevThrottle's Tailscale Serve 443 front-door mapping (issue #257). A machine
    /// without the tailscale CLI is a clean no-op. <paramref name="runner"/> is injectable for tests.</summary>
    public void RemoveTailscaleServe(List<string> steps, List<string> errors, TailscaleServeTeardown.Runner? runner = null)
    {
        var r = TailscaleServeTeardown.RemoveFrontDoor(runner);
        if (!r.Attempted) steps.Add("Tailscale Serve: tailscale CLI not present (nothing to remove)");
        else if (r.Removed) steps.Add($"removed Tailscale Serve front-door mapping (--https={TailscaleServeTeardown.FrontDoorHttpsPort})");
        else errors.Add($"Tailscale Serve front-door mapping: {r.Error ?? "removal failed"}");
    }

    /// <summary>Remove the Add/Remove Programs registration if present (issue #257).
    /// <paramref name="keyName"/> is injectable so tests use a throwaway key.</summary>
    [SupportedOSPlatform("windows")]
    public void RemoveArpEntry(List<string> steps, List<string> errors, string keyName = AddRemovePrograms.DefaultKeyName)
    {
        try
        {
            steps.Add(AddRemovePrograms.Unregister(keyName)
                ? "removed Add/Remove Programs entry"
                : "Add/Remove Programs entry: not present");
        }
        catch (Exception ex)
        {
            errors.Add($"Add/Remove Programs entry: {ex.Message}");
        }
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
        // Both old (pre-rename) and new names so uninstalling an older install still stops it.
        foreach (var name in new[] { "cc-director-gateway", "cc-director-cockpit", "devthrottle-gateway", "devthrottle-cockpit" })
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

    [SupportedOSPlatform("windows")]
    private static void RemoveLauncherAutostart(List<string> steps, List<string> errors)
    {
        try
        {
            steps.Add(LauncherAutostart.Unregister()
                ? $"removed autostart Run key ({LauncherAutostart.ValueName})"
                : "Launcher autostart Run key: not present");
        }
        catch (Exception ex)
        {
            errors.Add($"Launcher autostart Run key: {ex.Message}");
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

    private static void RemoveLinuxDesktopEntry(List<string> steps, List<string> errors)
    {
        var dataHome = LinuxDesktopEntry.DefaultDataHome();
        try { steps.AddRange(LinuxDesktopEntry.Remove(dataHome)); }
        catch (Exception ex) { errors.Add($"app menu entry ({LinuxDesktopEntry.EntryPath(dataHome)}): {ex.Message}"); }
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
