namespace CcDirector.Setup.Engine;

/// <summary>
/// The order in which a launcher is replaced. It differs by platform because WHO OWNS THE PROCESS
/// differs by platform, and getting it wrong does not fail loudly - it reinstates the old build and
/// reports success.
/// </summary>
public enum LauncherSwapOrder
{
    /// <summary>
    /// Stop the launcher, replace its file, start it again. Windows, where the executable is LOCKED
    /// while it runs so it cannot be replaced underneath itself; and Linux, where nothing supervises
    /// the launcher and a stopped launcher stays stopped until something starts it.
    /// </summary>
    StopThenPlaceThenStart,

    /// <summary>
    /// Replace the file while the launcher is still running, then ask its supervisor to restart it.
    ///
    /// FOR macOS, AND IT IS NOT A STYLISTIC PREFERENCE. The launcher runs as a launchd agent with
    /// KeepAlive / SuccessfulExit=false, so launchd resurrects it after a kill - FROM THE PATH IN THE
    /// PLIST, which is the file being replaced. Stop it first and launchd can start the OLD build back
    /// up in the gap before the swap lands; the swap then succeeds, the old process is the running one,
    /// and every check that follows sees a launcher that is alive and the wrong version. Replacing
    /// first is safe because Unix replaces a running binary by rename - the running process keeps the
    /// inode it started from and notices nothing.
    ///
    /// The restart is therefore one operation owned by launchd (<c>launchctl kickstart -k</c>), not a
    /// stop and a start this process performs. A stop and a start here would race the supervisor and,
    /// on a good day, leave two launchers.
    /// </summary>
    PlaceThenRestart,
}

/// <summary>
/// Orchestrates one installed CC Launcher build being replaced by another - swap, bring the new build
/// up, health-check it - with AUTO-ROLLBACK to the .old build + a version pin if the new build does not
/// come up (a bricked always-on launcher with no human present is the worst failure mode).
///
/// THE ORDER OF THOSE STEPS IS THE CALLER'S TO STATE, because it differs by platform and the wrong one
/// fails silently. See <see cref="LauncherSwapOrder"/>.
///
/// TWO CALLERS, AND THEY ARE NOT THE SAME SHAPE. On Windows this runs inside a detached helper process
/// launched from a STAGED copy of the new exe, so the installed target is free to overwrite once the
/// running tray app has exited. Everywhere else it runs inside the DIRECTOR
/// (<see cref="LauncherUpdateOwner"/>), which is already a separate process and needs no helper at all.
///
/// Process control and the health probe are injected as delegates so the rollback logic is
/// unit-testable without a real Launcher. Mirrors <see cref="GatewaySelfUpdate"/> (and shares its
/// <see cref="SelfUpdateOutcome"/>/<see cref="SelfUpdateResult"/> types).
/// </summary>
public sealed class LauncherSelfUpdate
{
    private readonly InstallLayout _layout;
    private readonly TimeSpan _unlockTimeout;

    public LauncherSelfUpdate(InstallLayout? layout = null, TimeSpan? unlockTimeout = null)
    {
        _layout = layout ?? InstallLayout.Default();
        _unlockTimeout = unlockTimeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// Swap in <paramref name="stagedExePath"/> as the Launcher build and verify the new build is
    /// healthy, rolling back to .old (and pinning <paramref name="newVersion"/>) if it is not.
    /// </summary>
    /// <param name="order">
    /// REQUIRED, AND DELIBERATELY NOT DEFAULTED. A default would be the Windows order, silently applied
    /// on a platform where it reinstates the old build and reports success - which is the exact shape of
    /// failure this whole change exists to remove. A caller must say which platform it is on.
    /// </param>
    /// <param name="restartLauncher">
    /// Ask the launcher's supervisor to restart it. Required by, and used only by,
    /// <see cref="LauncherSwapOrder.PlaceThenRestart"/>.
    /// </param>
    public async Task<SelfUpdateResult> ApplyAsync(
        string targetExePath,
        string stagedExePath,
        string newVersion,
        LauncherSwapOrder order,
        Func<bool> stopLauncher,
        Func<bool> startLauncher,
        Func<CancellationToken, Task<bool>> isHealthy,
        TimeSpan healthTimeout,
        CancellationToken ct = default,
        Func<bool>? restartLauncher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedExePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        ArgumentNullException.ThrowIfNull(stopLauncher);
        ArgumentNullException.ThrowIfNull(startLauncher);
        ArgumentNullException.ThrowIfNull(isHealthy);
        if (order == LauncherSwapOrder.PlaceThenRestart && restartLauncher is null)
            throw new ArgumentNullException(nameof(restartLauncher),
                "PlaceThenRestart has no meaning without a supervisor to ask; see LauncherSwapOrder.");

        var steps = new List<string>();
        var target = targetExePath;
        EngineLog.Write($"[LauncherSelfUpdate] applying {newVersion} -> {target} ({order})");

        // 1. Get the launcher out of the way of its own file, in whichever sense this platform means.
        if (order == LauncherSwapOrder.StopThenPlaceThenStart)
        {
            var stopped = stopLauncher();
            steps.Add("stopped the running Launcher");
            if (!ConfirmStopped(target, stopped, steps, out var whyNot))
            {
                // NOTHING IS STARTED HERE. Failing this check means the launcher did NOT go - the file
                // is still locked, or processes are still running - so the old build is still serving
                // the machine and starting one would give it a second launcher. Nothing has been
                // replaced, so there is nothing to recover from either.
                return Fail(steps, whyNot);
            }
        }
        // PlaceThenRestart stops nothing: see LauncherSwapOrder. The file is replaced underneath a
        // running process, which Unix allows, and the supervisor is asked afterwards.

        string? backup;
        try
        {
            backup = InstallSwapper.Place(target, stagedExePath);
            // A file that arrived over HTTP has no execute bit, and on macOS it carries a quarantine
            // attribute that has to go BEFORE first launch. Without this the swap succeeds and the
            // launcher never starts again - which reads as a bad build and gets a good version pinned.
            RunnableBuild.Prepare(target);
            steps.Add($"swapped Launcher build -> {newVersion} (backup: {backup})");
        }
        catch (Exception ex)
        {
            // Put the machine back to a running launcher on the old (still-installed) build - but only
            // where this code is the thing that stopped it. Under PlaceThenRestart nothing was stopped,
            // the launcher is still running, and its supervisor still owns it; starting one here would
            // leave the machine with two.
            if (order == LauncherSwapOrder.StopThenPlaceThenStart) startLauncher();
            return Fail(steps, $"swap failed: {ex.Message}");
        }

        // 2. Bring the new build up and health-check it.
        BringUp(order, startLauncher, restartLauncher, steps);
        if (await WaitHealthyAsync(isHealthy, healthTimeout, ct))
        {
            RecordInstalled(newVersion);
            steps.Add($"healthy on {newVersion}");
            EngineLog.Write($"[LauncherSelfUpdate] success: {newVersion}");
            return new SelfUpdateResult(SelfUpdateOutcome.Updated, $"Launcher updated to {newVersion}.", steps);
        }

        // 3. The new build did not come up -> roll back to .old and pin the bad version.
        steps.Add($"new build NOT healthy within {healthTimeout.TotalSeconds:F0}s; rolling back");
        EngineLog.Write($"[LauncherSelfUpdate] {newVersion} unhealthy; rolling back");
        if (order == LauncherSwapOrder.StopThenPlaceThenStart)
        {
            stopLauncher();
            WaitUntilWritable(target);
        }
        var restored = InstallSwapper.Rollback(target);
        RunnableBuild.Prepare(target);
        Pin(newVersion);
        BringUp(order, startLauncher, restartLauncher, steps);
        var healthyAfter = await WaitHealthyAsync(isHealthy, healthTimeout, ct);
        steps.Add(restored
            ? $"rolled back to previous build (healthy={healthyAfter}); pinned away from {newVersion}"
            : $"ROLLBACK FAILED - no .old backup; pinned away from {newVersion}");
        return new SelfUpdateResult(
            SelfUpdateOutcome.RolledBack,
            $"Rolled back from bad {newVersion} (healthy after rollback={healthyAfter}).",
            steps);
    }

    /// <summary>Start the launcher, or ask its supervisor to - whichever this platform's order means.</summary>
    private static void BringUp(
        LauncherSwapOrder order, Func<bool> startLauncher, Func<bool>? restartLauncher, List<string> steps)
    {
        if (order == LauncherSwapOrder.PlaceThenRestart)
        {
            var asked = restartLauncher!();
            steps.Add(asked
                ? "asked the supervisor to restart the Launcher (new build)"
                : "the supervisor REFUSED the restart request");
            return;
        }

        startLauncher();
        steps.Add("relaunched the Launcher (new build)");
    }

    /// <summary>
    /// Is the launcher really out of the way of its own file?
    ///
    /// THE BARRIER IS NOT THE SAME FACT ON EVERY PLATFORM, and reading it as one is how a check stops
    /// being able to fail. On Windows a single-file executable stays LOCKED until its process has fully
    /// exited, so waiting for the file to become writable is a direct observation of that exit (and of
    /// the tray app's single-instance mutex being released for the relaunch).
    ///
    /// On Unix there is no such lock: opening a running binary for write succeeds immediately. The same
    /// wait would therefore return true the instant it was asked, on a machine where the old launcher is
    /// still running, and certify nothing whatsoever. So off Windows the barrier is the only fact that
    /// does mean something - whether the stop actually left no installed launcher process behind, which
    /// is exactly what the stop delegate answers and what this code used to discard.
    /// </summary>
    private bool ConfirmStopped(string target, bool stopReportedClean, List<string> steps, out string whyNot)
    {
        if (OperatingSystem.IsWindows())
        {
            if (WaitUntilWritable(target)) { whyNot = ""; return true; }
            whyNot = $"Launcher exe still locked after stop ({target}); aborting swap.";
            return false;
        }

        if (stopReportedClean) { whyNot = ""; return true; }
        steps.Add("the stop did not clear every installed launcher process");
        whyNot = "the launcher was still running after it was asked and told to stop; aborting swap "
                 + "rather than replacing a build underneath a process that is about to be restarted.";
        return false;
    }

    private async Task<bool> WaitHealthyAsync(Func<CancellationToken, Task<bool>> isHealthy, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { if (await isHealthy(ct)) return true; }
            catch { /* not up yet */ }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>Wait until the target exe can be opened for write (i.e. the old Launcher process exited and released it).</summary>
    private bool WaitUntilWritable(string path)
    {
        if (!File.Exists(path)) return true; // nothing to unlock (fresh)
        var deadline = DateTime.UtcNow + _unlockTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
        }
        return false;
    }

    private void RecordInstalled(string version)
    {
        try
        {
            var m = InstalledManifest.Load(_layout);
            m.Set(ComponentRegistry.Launcher.Id, version);
            m.Save(_layout);
        }
        catch (Exception ex) { EngineLog.Write($"[LauncherSelfUpdate] record version failed: {ex.Message}"); }
    }

    private void Pin(string version)
    {
        try
        {
            var pins = PinStore.Load(_layout);
            pins.Pin(ComponentRegistry.Launcher.Id, version);
            PinStore.Save(_layout, pins);
        }
        catch (Exception ex) { EngineLog.Write($"[LauncherSelfUpdate] pin failed: {ex.Message}"); }
    }

    private static SelfUpdateResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[LauncherSelfUpdate] FAILED: {message}");
        return new SelfUpdateResult(SelfUpdateOutcome.Failed, message, steps);
    }
}
