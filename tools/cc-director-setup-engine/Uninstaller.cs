using System.Diagnostics;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The kind of thing an uninstall step removes.</summary>
public enum UninstallKind { Autostart, Directory, PathEntry, Shortcut, Skill, ScheduledTask, TailscaleServe, ArpEntry }

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

    /// <summary>The per-user Claude Code skills directory (%USERPROFILE%\.claude\skills). Skills are
    /// installed here per-user; only the names in the <see cref="SkillManifest"/> are ours to remove.</summary>
    private static string SkillsBaseDir() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

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

        // Owned skills (issue #257): one entry per skill the install recorded, existence-checked
        // against %USERPROFILE%\.claude\skills. Only manifested skills are ever listed, so the
        // user's own skills never appear here.
        var skillsBase = SkillsBaseDir();
        foreach (var skill in SkillManifest.Load(_layout).OwnedSkills)
        {
            var dir = System.IO.Path.Combine(skillsBase, skill);
            targets.Add(new UninstallTarget(UninstallKind.Skill, $"Skill '{skill}'", dir, Directory.Exists(dir)));
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

        progress?.Report("Removing the app and CLI tools");
        RemoveDirectories(role, steps, errors);

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

        progress?.Report("Removing the Start Menu shortcut");
        RemoveShortcut(steps, errors);

        // Integration points common to both roles (issue #257). Skills + scheduled tasks are per-user
        // and role-independent; the Add/Remove Programs entry is Windows-only.
        progress?.Report("Removing the CC Director skills");
        RemoveSkills(steps, errors);
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
            progress?.Report("Removing your data");
            WipeUserData(steps, errors);
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
        // Safety: refuse anything that is not a per-user CC Director root.
        var leaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root));
        if (!string.Equals(leaf, "cc-director", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"refused to wipe data: '{root}' is not a cc-director root");
            return;
        }
        if (!Directory.Exists(root)) { steps.Add($"data: not present ({root})"); return; }
        try
        {
            Directory.Delete(root, recursive: true);
            steps.Add($"removed all data: {root}");
        }
        catch (Exception ex)
        {
            errors.Add($"data ({root}): {ex.Message}");
        }
    }

    /// <summary>
    /// Remove ONLY the skills the install recorded in the <see cref="SkillManifest"/> (issue #257).
    /// A user-authored skill that is not in the manifest is never touched - that is the whole point
    /// of the manifest (AC8). <paramref name="skillsBaseDir"/> is injectable for tests/sandbox.
    /// </summary>
    public void RemoveSkills(List<string> steps, List<string> errors, string? skillsBaseDir = null)
    {
        var owned = SkillManifest.Load(_layout).OwnedSkills;
        if (owned.Count == 0) { steps.Add("skills: none recorded (nothing to remove)"); return; }

        var baseDir = skillsBaseDir ?? SkillsBaseDir();
        var baseFull = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(baseDir));
        foreach (var skill in owned)
        {
            // SAFETY: never trust a manifest entry to be a simple child name. A blank entry would
            // make Path.Combine resolve to baseDir itself (deleting the WHOLE skills tree), and a
            // "..\x" entry would escape it. Refuse anything whose resolved parent is not exactly the
            // skills dir - this is a destructive op, so the guard lives at the point of deletion.
            var dirFull = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, skill));
            if (string.IsNullOrWhiteSpace(skill) ||
                !string.Equals(System.IO.Path.GetDirectoryName(dirFull), baseFull, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"skill '{skill}': refused (resolves outside {baseDir})");
                continue;
            }

            if (!Directory.Exists(dirFull)) { steps.Add($"skill '{skill}': not present"); continue; }
            try
            {
                Directory.Delete(dirFull, recursive: true);
                steps.Add($"removed skill '{skill}': {dirFull}");
            }
            catch (Exception ex)
            {
                errors.Add($"skill '{skill}' ({dirFull}): {ex.Message}");
            }
        }
    }

    /// <summary>Remove CC Director's scheduled tasks if present (issue #257). Absent tasks are
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

    /// <summary>Tear down CC Director's Tailscale Serve 443 front-door mapping (issue #257). A machine
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
