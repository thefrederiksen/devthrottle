using System.Diagnostics;
using System.Reflection;
using CcDirector.Core.Instances;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>
/// Applies a staged update by swapping the installed build with a downloaded one, on a start a
/// person or the launcher already asked for.
///
/// A running single-file executable cannot overwrite itself (Windows holds a
/// file lock; macOS keeps the live inode), so the swap is performed by the
/// freshly downloaded build running in a hidden "<c>--apply-update</c>" mode: it
/// waits for the old process to exit, replaces the install, and relaunches.
///
/// WHAT THIS NO LONGER DOES, AND WHY (issue #1033). A running Director never decides to replace
/// itself any more. It stages an update, says so, and stops there; the launcher - which outlives the
/// swap and can therefore witness the result - stops it, swaps the build, starts it and confirms the
/// new version answers. The Director used to make that decision itself the moment it went idle, and
/// the shape of doing it from inside is what made it unsafe: the only process that could have checked
/// whether the relaunch came up was the one that had just exited, so a relaunch that never appeared
/// was recorded as a success with nobody left to notice.
///
/// The path below survives for the start a HUMAN initiates - the "Restart now" banner, or simply
/// opening the app - because that is a different event with a person present, and because removing it
/// would permanently strand any install whose launcher is missing: the thing that fetches every future
/// fix would be gone, and no later release could repair it. It now proves the relaunch instead of
/// assuming it (see <see cref="ApplyUpdate"/>), and its recovery works on macOS as well as Windows.
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
    /// "Director.app" bundle (not the binary inside it). Falls back to the
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
    /// True when a swap left the install in a broken state that must be recovered from the
    /// <c>.old</c> backup on startup (issue #242): the install exe is missing or zero-length
    /// (a half-completed copy/replace) while a non-empty <c>.old</c> backup is present.
    /// Pure decision, unit-tested.
    /// </summary>
    public static bool NeedsHalfSwapRecovery(bool installExists, long installLength, bool oldExists, long oldLength)
        => (!installExists || installLength == 0) && oldExists && oldLength > 0;

    /// <summary>
    /// True when a previously-swapped build never proved healthy and must be rolled back to its
    /// <c>.old</c> backup on this startup (issue #242): a health check is still pending for a
    /// version, the running build is NOT that version (so the new build failed to come up and
    /// hand control back), and a non-empty <c>.old</c> backup exists to roll back to.
    /// Pure decision, unit-tested.
    /// </summary>
    public static bool NeedsHealthRollback(string? pendingHealthVersion, string runningVersion, bool oldExists, long oldLength)
        => !string.IsNullOrEmpty(pendingHealthVersion)
           && !string.Equals(pendingHealthVersion, runningVersion, StringComparison.Ordinal)
           && oldExists && oldLength > 0;

    /// <summary>
    /// If a verified, newer update has been staged for THIS install path, launch the
    /// relauncher to apply it and return true (the caller must then exit so the swap can
    /// proceed). Returns false when nothing is pending or we boot the current
    /// build instead; in the latter "gave up" case <paramref name="failureNotice"/> is set
    /// to a user-facing message the caller should surface (issue #242 -- never fail silently).
    ///
    /// CALLED FROM STARTUP ONLY, and startup is the whole safety argument: no session exists yet, so
    /// applying an update here cannot lose running work, and a person or the launcher has already asked
    /// for this start. A RUNNING Director never calls this any more (issue #1033) - it used to, the
    /// moment it noticed it was idle, and that is what made an unwitnessed relaunch possible. The
    /// launcher owns the unattended route now, and it does the swap from outside so it can confirm the
    /// result.
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

            // Never re-apply a version that already failed its post-update health check and was
            // rolled back (issue #242). Pinning it here stops a re-stage/re-apply loop of a build
            // we already know does not start.
            if (!string.IsNullOrEmpty(state.PinnedBadVersion)
                && string.Equals(state.PinnedBadVersion, state.StagedVersion, StringComparison.Ordinal))
            {
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Staged {state.StagedVersion} is pinned as a failed update; clearing without applying.");
                ClearStagedState();
                return false;
            }

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
                    $"Director could not finish updating to {version} after {MaxApplyAttempts} attempts, " +
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
        var slug = InstanceContext.Slug;
        FileLog.Write($"[UpdateInstaller] LaunchRelauncher: staged={stagedExecutable}, target={installTarget}, instance={slug}");
        var psi = new ProcessStartInfo
        {
            FileName = stagedExecutable,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(installTarget);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());

        // Which instance is updating, carried as an ARGUMENT rather than left to the inherited
        // CC_DIRECTOR_ROOT, so the build we finally relaunch can be handed a clean environment and
        // still come back as this instance. A build older than this argument ignores the extra token.
        psi.ArgumentList.Add(slug);

        // The relauncher itself KEEPS the inherited override on purpose: it reads and clears this
        // instance's updater state, which lives in this instance's home.
        Process.Start(psi);
    }

    /// <summary>
    /// Entry point for the hidden "<c>--apply-update &lt;targetPath&gt; &lt;parentPid&gt; [instanceSlug]</c>"
    /// mode. Waits for the parent to exit, replaces the installed build with this
    /// (staged) one, relaunches it, and returns a process exit code.
    ///
    /// <paramref name="instanceSlug"/> is absent when the update was launched by a build older than
    /// the argument; the relaunch then carries no instance and the new process resolves the default.
    /// </summary>
    public static int ApplyUpdate(string targetPath, int parentPid, string? instanceSlug = null)
    {
        FileLog.Start();
        FileLog.Write($"[UpdateInstaller] ApplyUpdate: target={targetPath}, parentPid={parentPid}, instance={instanceSlug ?? "(none)"}, self={Environment.ProcessPath}");

        WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30));

        // Record which version we are about to install so the freshly-swapped build must
        // prove it can come up healthy before the update is trusted (issue #242). If that
        // build fails to reach its main window, a later startup sees this marker still set
        // (and the running version unchanged) and rolls back to the .old backup.
        var versionBeingInstalled = UpdaterState.Load().StagedVersion;

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Auto-update is only supported on Windows and macOS.");
        Swap(targetPath);

        // Clear the staged marker BEFORE relaunching so the freshly-installed build
        // doesn't see itself as a pending update and loop. Arm the post-update health
        // self-check at the same time (issue #242).
        ClearStagedState();
        ArmHealthCheck(versionBeingInstalled);
        Relaunch(targetPath, instanceSlug);

        // WAIT FOR A WITNESS. This used to log "complete" here, having proved nothing beyond
        // Process.Start returning - so a relaunch that never came up was written down as a success and
        // then logging stopped, leaving no record that anything was wrong (issue #1033). The relaunched
        // build clears the pending health marker only once it reaches its main window, so watching that
        // marker is a real answer about the new build rather than a statement about this helper.
        var cameUp = WaitForRelaunchedBuildToReportHealthy(versionBeingInstalled, RelaunchHealthTimeout);
        if (cameUp)
            FileLog.Write($"[UpdateInstaller] ApplyUpdate: verified - {versionBeingInstalled} came up and reported healthy.");
        else
            FileLog.Write($"[UpdateInstaller] ApplyUpdate: NOT VERIFIED - {versionBeingInstalled} did not report healthy "
                          + $"within {RelaunchHealthTimeout.TotalSeconds:F0}s. The rollback to the previous build is armed and "
                          + "runs on the next startup; the backup is kept until a build proves healthy.");

        FileLog.Stop();
        return cameUp ? 0 : 1;
    }

    /// <summary>
    /// How long the relauncher waits for the build it just started to report that it reached its main
    /// window. Generous on purpose: a cold start on a slow machine walks a splash screen, the engine
    /// and the control API before the window appears, and calling a slow start a failure would be as
    /// wrong as calling a dead start a success.
    /// </summary>
    public static readonly TimeSpan RelaunchHealthTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Watch for the freshly relaunched build to clear its pending health marker, which it does when it
    /// reaches its main window (<see cref="MarkCurrentBuildHealthy"/>). Returns true when it did within
    /// the timeout. Returns true immediately when no version was being installed, because there is then
    /// no marker to wait on and no claim being made.
    /// </summary>
    private static bool WaitForRelaunchedBuildToReportHealthy(string? versionBeingInstalled, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(versionBeingInstalled))
            return true;

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            // Every read of the state file writes a line to this helper's log, so the interval is a
            // readability choice as much as a timing one: often enough to exit promptly on a good start,
            // rare enough that the log of a failed one is still readable.
            Thread.Sleep(2000);
            if (string.IsNullOrEmpty(UpdaterState.Load().PendingHealthCheckVersion))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Startup housekeeping: delete the leftover backup from a previous swap and prune staging
    /// directories older than 7 days. Safe to call unconditionally; never throws.
    ///
    /// The backup is KEPT while a post-update health check is still pending (issue #1033). This runs
    /// before the new build reaches its main window, so deleting the backup unconditionally opened a
    /// window in which a build could start, delete the only way back, and then die before proving
    /// itself - leaving a broken install with nothing to restore and no later release able to reach the
    /// machine. A build that has proved healthy deletes its predecessor's backup itself, the moment it
    /// clears the marker; see <see cref="MarkCurrentBuildHealthy"/>.
    /// </summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            // Remove leftovers from a prior swap: the backup, and any ".new" that
            // an interrupted swap left behind (a file on Windows, a directory on macOS).
            var target = InstallTarget();
            var healthPending = !string.IsNullOrEmpty(UpdaterState.Load().PendingHealthCheckVersion);
            var leftovers = healthPending
                ? new[] { target + ".new" }
                : new[] { target + ".old", target + ".new" };
            if (healthPending)
                FileLog.Write("[UpdateInstaller] CleanupAfterUpdate: a health check is still pending, so the backup is kept.");

            foreach (var leftover in leftovers)
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
    /// Recover from a half-completed swap on startup (issue #242). If the install exe is
    /// missing or zero-length (an interrupted copy/replace) but a non-empty <c>.old</c> backup
    /// is present, restore the install from the backup so the app can boot instead of dying
    /// silently. Returns a user-facing notice when a recovery happened, otherwise null.
    /// MUST run BEFORE <see cref="CleanupAfterUpdate"/> (which deletes the <c>.old</c> backup).
    /// Best-effort; never throws.
    /// </summary>
    public static string? RecoverHalfAppliedSwap()
    {
        try
        {
            // Runs on every platform. It used to begin "if this is not Windows, return null", on the
            // reasoning that the macOS bundle swap is one atomic rename with no half-written state to
            // find. That reasoning covered the swap and not the failure: a bundle copy interrupted part
            // way leaves a directory that exists and holds no executable, which is indistinguishable
            // from a healthy install to anything that only asks whether the path is there. The shape of
            // the install is handled by DirectorBuildSwapper; the decision below is one rule for both.
            var target = InstallTarget();
            var old = DirectorBuildSwapper.BackupPathFor(target);

            var install = DirectorBuildSwapper.Inspect(target);
            var backup = DirectorBuildSwapper.Inspect(old);

            if (!NeedsHalfSwapRecovery(install.Exists, install.Length, backup.Exists, backup.Length))
                return null;

            FileLog.Start();
            FileLog.Write($"[UpdateInstaller] RecoverHalfAppliedSwap: the installed build is " +
                $"{(install.Exists ? $"zero-length ({install.Length} bytes)" : "missing or incomplete")}; restoring from {old} ({backup.Length} bytes).");

            // Keep the backup: the cleanup step deletes it a moment later anyway, and consuming it here
            // would leave nothing behind if the restored build also failed to start.
            if (!DirectorBuildSwapper.RestoreBackup(target, keepBackup: true))
                return null;
            FileLog.Write($"[UpdateInstaller] RecoverHalfAppliedSwap: restored {target} from backup.");

            return "Director detected a half-finished update and restored the previous working " +
                   "version so it could start. The interrupted update will be retried later. " +
                   "See the log for details.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] RecoverHalfAppliedSwap FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Roll back an update that produced a build which never came up healthy (issue #242). If a
    /// post-update health check is still pending for a version the running build did not become,
    /// restore the install from the <c>.old</c> backup, pin the bad version so it is not re-applied,
    /// clear the health marker, and return a "update X failed, rolled back" notice. The caller must
    /// exit and relaunch the restored build so the rolled-back version runs. Returns null when no
    /// rollback is needed. MUST run BEFORE <see cref="CleanupAfterUpdate"/>. Best-effort; never throws.
    /// </summary>
    public static string? TryRollBackFailedUpdate()
    {
        try
        {
            // Runs on every platform (issue #1033). This was the macOS hole: a Mac whose update produced
            // a build that could not start had no route back at all, because the whole method returned
            // early off-Windows. The rule about WHEN to roll back was never platform-specific - only the
            // file operations were, and those now live in DirectorBuildSwapper for both shapes.
            var state = UpdaterState.Load();
            var pending = state.PendingHealthCheckVersion;
            if (string.IsNullOrEmpty(pending))
                return null;

            var running = (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
            var target = InstallTarget();
            var old = DirectorBuildSwapper.BackupPathFor(target);
            var backup = DirectorBuildSwapper.Inspect(old);

            if (!NeedsHealthRollback(pending, running, backup.Exists, backup.Length))
                return null;

            FileLog.Start();
            FileLog.Write($"[UpdateInstaller] TryRollBackFailedUpdate: update {pending} never became healthy " +
                $"(running {running}); rolling back to backup {old} ({backup.Length} bytes) and pinning the bad version.");

            // Restore the previous build over the failing one, keeping the backup so a restored build
            // that ALSO fails to start still has something to fall back on.
            if (!DirectorBuildSwapper.RestoreBackup(target, keepBackup: true))
                return null;

            // Pin the bad version and clear the health marker so we do not loop.
            state.PinnedBadVersion = pending;
            state.PendingHealthCheckVersion = null;
            state.Save();

            FileLog.Write($"[UpdateInstaller] TryRollBackFailedUpdate: restored previous build at {target}; pinned bad version {pending}.");
            return $"Update {pending} failed to start correctly, so Director rolled back to the " +
                   "previous working version. That update has been blocked from retrying. " +
                   "See the log for details.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] TryRollBackFailedUpdate FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Clear the post-update health-check marker once the current build has reached a healthy,
    /// interactive state (issue #242). Called from the UI after the main window is shown, so a
    /// successful update is no longer treated as pending and never rolls back. No-op when nothing
    /// is pending. Best-effort; never throws.
    /// </summary>
    /// <returns>
    /// True when a pending health check was actually cleared - i.e. a Director self-update was just
    /// applied and this is its first healthy boot. This is the version-change signal the lifecycle
    /// uses to force a one-time tool reconcile (issue #827): a version bump is the strongest signal
    /// the bundled tools manifest changed. False when nothing was pending (an ordinary boot).
    /// </returns>
    public static bool MarkCurrentBuildHealthy()
    {
        try
        {
            var state = UpdaterState.Load();
            if (string.IsNullOrEmpty(state.PendingHealthCheckVersion))
                return false;

            FileLog.Write($"[UpdateInstaller] MarkCurrentBuildHealthy: clearing pending health check for {state.PendingHealthCheckVersion}.");
            state.PendingHealthCheckVersion = null;
            state.Save();

            // This build has now proved itself, so its predecessor's backup has no further purpose and
            // is deleted here rather than being left for the next startup. The startup cleanup keeps the
            // backup precisely while this marker is set (issue #1033), so the one place that knows the
            // backup is finished with is the one place that clears the marker.
            DirectorBuildSwapper.DeleteBackup(InstallTarget());
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] MarkCurrentBuildHealthy FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>Arm the post-update health self-check for a freshly-installed version (issue #242).</summary>
    private static void ArmHealthCheck(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return;
        var state = UpdaterState.Load();
        state.PendingHealthCheckVersion = version;
        state.Save();
        FileLog.Write($"[UpdateInstaller] ArmHealthCheck: armed post-update health check for {version}.");
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

    /// <summary>
    /// Install this (staged) build over <paramref name="target"/>, keeping the previous build as a
    /// backup on BOTH platforms.
    ///
    /// The macOS swap used to delete the installed bundle outright and move the new one in, so no
    /// backup existed at all - which meant the roll-back-a-bad-update path could never have worked on a
    /// Mac even with its platform guard removed, because there was never anything to roll back to
    /// (issue #1032). Keeping the backup is what makes one recovery rule serve both platforms.
    /// </summary>
    private static void Swap(string target)
    {
        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null; cannot locate the staged build.");

        // On macOS the thing that becomes the install is the whole enclosing bundle, not the binary
        // inside it that this process happens to be running as.
        var stagedSource = OperatingSystem.IsMacOS()
            ? AppBundleOf(self) ?? throw new InvalidOperationException("Staged build is not inside an .app bundle; cannot swap.")
            : self;

        var backup = DirectorBuildSwapper.Place(target, stagedSource);
        FileLog.Write($"[UpdateInstaller] Swap: installed staged build at {target} (backup: {backup ?? "none - nothing was there"})");
    }

    private static void Relaunch(string targetPath, string? instanceSlug)
    {
        var psi = BuildRelaunchStartInfo(targetPath, instanceSlug);
        FileLog.Write($"[UpdateInstaller] Relaunch: {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
        Process.Start(psi);
    }

    /// <summary>
    /// How the freshly-installed build is started after an update, and the one place that decides it.
    ///
    /// The child must NOT inherit <c>CC_DIRECTOR_ROOT</c>. A running Director sets that variable to its
    /// own instance home, so a relaunched build that inherited it would read its own home as the
    /// machine-wide root and settle one level deeper - a brand-new, empty data tree, which reads to the
    /// user as "the update wiped my Director and started the setup wizard again". The instance travels
    /// as <c>--instance &lt;slug&gt;</c> instead, so identity is carried deliberately rather than by
    /// inheritance. Same rule, same reason, as <c>InstanceProcess.Launch</c>.
    ///
    /// Pure, so the environment scrub is provable without starting a process.
    /// </summary>
    /// <param name="waitForProcessId">
    /// The process id the relaunched build must wait to exit before it starts, or null for none.
    ///
    /// WHY THIS EXISTS. Its sibling <see cref="LaunchRelauncher"/> has always passed the parent process
    /// id, and <see cref="ApplyUpdate"/> waits on it for thirty seconds before doing anything. This path
    /// - the rollback relaunch - passed nothing, so the restored build started while the process that
    /// started it was still exiting. That was survivable only because the incoming build did several
    /// seconds of update work before it claimed the single-instance mutex, which is precisely the
    /// ordering that lets updater work run over an unaccounted-for live Director. Taking the mutex first
    /// closes that, and it is safe to take it first ONLY once this path stops racing its own parent.
    ///
    /// The correct pattern was already in this file, a hundred lines away, on the sibling path.
    /// </param>
    public static ProcessStartInfo BuildRelaunchStartInfo(string targetPath, string? instanceSlug,
        int? waitForProcessId = null)
    {
        // UseShellExecute must stay false: the environment block can only be edited on a direct start.
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : targetPath,
            UseShellExecute = false,
        };

        // The arguments the RELAUNCHED APPLICATION receives, collected before they are placed - because
        // on macOS they have to travel behind a single "--args", and emitting that per-argument would
        // hand the second group to /usr/bin/open instead of to the Director. The first draft of the
        // wait handshake did exactly that whenever no instance slug was carried.
        var appArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(instanceSlug))
        {
            appArgs.Add("--instance");
            appArgs.Add(instanceSlug);
        }
        if (waitForProcessId is { } parentPid)
        {
            appArgs.Add("--wait-for-exit");
            appArgs.Add(parentPid.ToString());
        }

        if (OperatingSystem.IsMacOS())
        {
            psi.ArgumentList.Add(targetPath);
            if (appArgs.Count > 0)
            {
                psi.ArgumentList.Add("--args");
                foreach (var a in appArgs) psi.ArgumentList.Add(a);
            }
        }
        else
        {
            foreach (var a in appArgs) psi.ArgumentList.Add(a);
        }

        psi.Environment.Remove("CC_DIRECTOR_ROOT");
        return psi;
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

    /// <summary>
    /// Wait for another process to exit, bounded. PUBLIC because the startup path needs the same wait
    /// the update path has always had - see BuildRelaunchStartInfo's waitForProcessId.
    /// </summary>
    public static void WaitForProcessExit(int pid, TimeSpan timeout)
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
