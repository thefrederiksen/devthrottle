using System.Diagnostics;
using System.Reflection;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>
/// Applies a staged update by swapping the installed build with a downloaded one.
///
/// A running single-file executable cannot overwrite itself (Windows holds a
/// file lock; macOS keeps the live inode), so the swap is performed by the
/// freshly downloaded build running in a hidden "<c>--apply-update</c>" mode: it
/// waits for the old process to exit, replaces the install, and relaunches.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Name of the .NET apphost binary inside the bundle / on disk.</summary>
    public const string ExecutableName = "cc-director";

    /// <summary>Root staging directory for downloaded updates: config/director/updates/.</summary>
    public static string StagingRoot => Path.Combine(CcStorage.ToolConfig("director"), "updates");

    /// <summary>
    /// The path a new build must overwrite to "become" the installed app. On
    /// Windows this is the running cc-director.exe; on macOS it is the enclosing
    /// "CC Director.app" bundle (not the binary inside it). Falls back to the
    /// process path when not running from a bundle (e.g. a bare dev binary).
    /// </summary>
    public static string InstallTarget()
    {
        var proc = Environment.ProcessPath ?? "";
        if (OperatingSystem.IsMacOS())
            return AppBundleOf(proc) ?? proc;
        return proc;
    }

    /// <summary>
    /// Max times startup will hand a staged update to the relauncher before giving up.
    /// A staged update whose swap never completes must NOT make us relaunch-and-exit
    /// forever (issue #242), which presents to the user as "clicking does nothing".
    /// </summary>
    public const int MaxApplyAttempts = 2;

    /// <summary>
    /// True when the staged update has already failed to apply <see cref="MaxApplyAttempts"/>
    /// times for its current version. Pure decision, unit-tested.
    /// </summary>
    public static bool HasExhaustedApplyAttempts(UpdaterState state, int maxAttempts)
        => state.ApplyAttemptVersion == state.StagedVersion && state.ApplyAttempts >= maxAttempts;

    /// <summary>
    /// If a verified, newer update has been staged for THIS install path, launch the
    /// relauncher to apply it and return true (the caller must then exit so the swap can
    /// proceed). Called at startup, before any session exists, so applying an update never
    /// loses running work. Returns false when nothing is pending or we boot the current
    /// build instead; in the latter "gave up" case <paramref name="failureNotice"/> is set
    /// to a user-facing message the caller should surface (issue #242 -- never fail silently).
    /// </summary>
    public static bool TryApplyStagedUpdateAtStartup(out string? failureNotice)
    {
        failureNotice = null;
        try
        {
            var state = UpdaterState.Load();
            if (string.IsNullOrEmpty(state.StagedVersion)
                || string.IsNullOrEmpty(state.StagedExecutable)
                || string.IsNullOrEmpty(state.InstallTarget))
                return false;

            // Only ever apply an update that targets the path we are running from.
            if (!PathsEqual(state.InstallTarget, InstallTarget()))
                return false;

            if (!StagedIsNewer(state.StagedVersion))
            {
                // The staged version is not newer than what's running -- the apply already
                // succeeded (or is obsolete). Clear it so we never re-evaluate it again.
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Staged {state.StagedVersion} is not newer than running build; clearing.");
                ClearStagedState();
                return false;
            }

            if (!File.Exists(state.StagedExecutable))
            {
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Staged executable missing, clearing: {state.StagedExecutable}");
                ClearStagedState();
                return false;
            }

            // Bound the apply: if it has already failed MaxApplyAttempts times, give up,
            // clear the staged state, and boot the current build with a visible notice
            // instead of relaunching-and-exiting forever (issue #242).
            if (HasExhaustedApplyAttempts(state, MaxApplyAttempts))
            {
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Giving up on staged update {state.StagedVersion} after {state.ApplyAttempts} failed apply attempts; clearing and booting current build.");
                var version = state.StagedVersion;
                ClearStagedState();
                failureNotice =
                    $"CC Director could not finish updating to {version} after {MaxApplyAttempts} attempts, " +
                    "so it has started on the current version instead. The pending update was cleared and " +
                    "will be retried later. See the log for details.";
                return false;
            }

            // Record this attempt BEFORE launching, so a crash mid-apply still counts toward
            // the bound (otherwise a swap that crashes silently would never increment). Reset
            // the counter when a different version is now staged.
            int priorAttempts = state.ApplyAttemptVersion == state.StagedVersion ? state.ApplyAttempts : 0;
            state.ApplyAttemptVersion = state.StagedVersion;
            state.ApplyAttempts = priorAttempts + 1;
            state.Save();

            FileLog.Start();
            FileLog.Write($"[UpdateInstaller] Applying staged update {state.StagedVersion} at startup (attempt {state.ApplyAttempts}/{MaxApplyAttempts}) -> {state.InstallTarget}");
            LaunchRelauncher(state.StagedExecutable, state.InstallTarget);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] TryApplyStagedUpdateAtStartup FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Spawn the staged build detached, instructing it to wait for us (the current
    /// process) to exit and then swap itself over <paramref name="installTarget"/>.
    /// The caller should request application shutdown immediately after this returns.
    /// </summary>
    public static void LaunchRelauncher(string stagedExecutable, string installTarget)
    {
        FileLog.Write($"[UpdateInstaller] LaunchRelauncher: staged={stagedExecutable}, target={installTarget}");
        var psi = new ProcessStartInfo
        {
            FileName = stagedExecutable,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(installTarget);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>
    /// Entry point for the hidden "<c>--apply-update &lt;targetPath&gt; &lt;parentPid&gt;</c>"
    /// mode. Waits for the parent to exit, replaces the installed build with this
    /// (staged) one, relaunches it, and returns a process exit code.
    /// </summary>
    public static int ApplyUpdate(string targetPath, int parentPid)
    {
        FileLog.Start();
        FileLog.Write($"[UpdateInstaller] ApplyUpdate: target={targetPath}, parentPid={parentPid}, self={Environment.ProcessPath}");

        WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30));

        if (OperatingSystem.IsWindows())
            SwapWindows(targetPath);
        else if (OperatingSystem.IsMacOS())
            SwapMac(targetPath);
        else
            throw new PlatformNotSupportedException("Auto-update is only supported on Windows and macOS.");

        // Clear the staged marker BEFORE relaunching so the freshly-installed build
        // doesn't see itself as a pending update and loop.
        ClearStagedState();
        Relaunch(targetPath);

        FileLog.Write("[UpdateInstaller] ApplyUpdate: complete");
        FileLog.Stop();
        return 0;
    }

    /// <summary>
    /// Startup housekeeping: delete the leftover "<c>.old</c>" file from a previous
    /// Windows swap and prune staging directories older than 7 days. Safe to call
    /// unconditionally; never throws.
    /// </summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            // Remove leftovers from a prior swap: ".old" backup and any ".new" that
            // an interrupted swap left behind (a file on Windows, a dir on macOS).
            var target = InstallTarget();
            foreach (var leftover in new[] { target + ".old", target + ".new" })
            {
                if (File.Exists(leftover)) File.Delete(leftover);
                else if (Directory.Exists(leftover)) Directory.Delete(leftover, recursive: true);
                else continue;
                FileLog.Write($"[UpdateInstaller] CleanupAfterUpdate: removed {leftover}");
            }

            if (Directory.Exists(StagingRoot))
            {
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        FileLog.Write($"[UpdateInstaller] CleanupAfterUpdate: pruned stale staging {dir}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] CleanupAfterUpdate FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Return the enclosing ".app" bundle path for a path inside one, or null when
    /// the path is not inside a bundle.
    /// </summary>
    public static string? AppBundleOf(string path)
    {
        const string marker = ".app" + "/";
        var idx = path.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
            return path.Substring(0, idx + 4); // keep the ".app"
        if (path.EndsWith(".app", StringComparison.Ordinal))
            return path;
        return null;
    }

    private static void SwapWindows(string targetExe)
    {
        var staged = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null; cannot locate the staged build.");

        // Copy the new build onto the target volume FIRST, so the install is never
        // left without an exe if the copy fails. Then atomically replace, keeping
        // the old exe as a ".old" backup (cleaned up on the next normal startup).
        var newPath = targetExe + ".new";
        var old = targetExe + ".old";
        if (File.Exists(newPath)) File.Delete(newPath);
        File.Copy(staged, newPath);

        if (File.Exists(old)) File.Delete(old);
        if (File.Exists(targetExe))
            File.Replace(newPath, targetExe, old); // target <- new, old <- previous target
        else
            File.Move(newPath, targetExe);
        FileLog.Write($"[UpdateInstaller] SwapWindows: installed staged build at {targetExe}");
    }

    private static void SwapMac(string targetApp)
    {
        var stagedApp = AppBundleOf(Environment.ProcessPath ?? "")
            ?? throw new InvalidOperationException("Staged build is not inside an .app bundle; cannot swap.");

        // Build the replacement bundle fully BESIDE the target first (de-quarantined
        // and executable), then swap with a fast rename so the install is never left
        // half-written. macOS keeps this process's running binary alive via its inode
        // even after the old bundle is unlinked, so replacing it underfoot is safe.
        var newApp = targetApp + ".new";
        Run("/bin/rm", "-rf", newApp);
        Run("/usr/bin/ditto", stagedApp, newApp);
        Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", newApp);
        Run("/bin/chmod", "+x", Path.Combine(newApp, "Contents", "MacOS", ExecutableName));

        Run("/bin/rm", "-rf", targetApp);
        Run("/bin/mv", newApp, targetApp);
        FileLog.Write($"[UpdateInstaller] SwapMac: installed staged bundle at {targetApp}");
    }

    private static void Relaunch(string targetPath)
    {
        if (OperatingSystem.IsMacOS())
            Run("/usr/bin/open", targetPath);
        else
            Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
    }

    private static void ClearStagedState()
    {
        try
        {
            var s = UpdaterState.Load();
            s.StagedVersion = null;
            s.StagedExecutable = null;
            s.InstallTarget = null;
            s.ApplyAttempts = 0;
            s.ApplyAttemptVersion = null;
            s.Save();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] ClearStagedState FAILED: {ex.Message}");
        }
    }

    private static bool StagedIsNewer(string stagedVersion)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        if (!Version.TryParse(stagedVersion, out var staged)) return false;
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
        return Norm(staged) > Norm(current);
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            comparison);
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {file}");
        p.WaitForExit();
        if (p.ExitCode != 0)
            FileLog.Write($"[UpdateInstaller] Run non-zero exit ({p.ExitCode}): {file} {string.Join(' ', args)}");
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited) return;
            }
            catch (ArgumentException)
            {
                return; // no such process == already exited
            }
            Thread.Sleep(200);
        }
        FileLog.Write($"[UpdateInstaller] WaitForProcessExit: pid {pid} still alive after {timeout.TotalSeconds}s; proceeding");
    }
}
