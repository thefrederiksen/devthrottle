using CcDirector.Core.Update;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Applies a staged Director update FROM OUTSIDE THE DIRECTOR: stop it, swap the build, start it, and
/// confirm the new version actually answers - with a roll back to the previous build, on both Windows
/// and macOS, when it does not (issue #1033).
///
/// This is the shape of the fix, not a detail of it. A process cannot replace its own binary and then
/// vouch for what came back, because the only thing that could check is the process that just exited.
/// The Director therefore used to swap itself, start something, write down that it had finished, and
/// stop logging - so a relaunch that never appeared was indistinguishable in the record from one that
/// worked. The launcher is still there afterwards, so it can hold the question open until there is a
/// real answer, and act on the answer.
///
/// The proof required here is the NEW VERSION answering, never a start call returning. That is a
/// genuine witness rather than a liveness check: a staged update is by definition newer than what was
/// running, so a health answer carrying the new version cannot have come from the old build. Process
/// control and the version read are injected as delegates, so the rollback can be driven to completion
/// in a test without a real Director anywhere. Mirrors <see cref="LauncherSelfUpdate"/> and shares its
/// <see cref="SelfUpdateOutcome"/> and <see cref="SelfUpdateResult"/> types.
/// </summary>
public sealed class DirectorUpdateApply
{
    private readonly TimeSpan _unlockTimeout;
    private readonly TimeSpan _pollInterval;

    public DirectorUpdateApply(TimeSpan? unlockTimeout = null, TimeSpan? pollInterval = null)
    {
        _unlockTimeout = unlockTimeout ?? TimeSpan.FromSeconds(30);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Swap <paramref name="stagedBuild"/> in as the installed Director at <paramref name="installTarget"/>
    /// and confirm <paramref name="newVersion"/> comes up, rolling back to the previous build if it does
    /// not.
    /// </summary>
    /// <param name="readRunningVersion">
    /// The version the running Director reports, or null when nothing answers. Null and a stale version
    /// are treated the same - as "not yet proved" - so a build that never starts and a build whose port
    /// is answered for a moment by something else both end in the same place: not certified.
    /// </param>
    public async Task<SelfUpdateResult> ApplyAsync(
        string installTarget,
        string stagedBuild,
        string newVersion,
        Func<CancellationToken, Task> stopDirector,
        Action startDirector,
        Func<CancellationToken, Task<string?>> readRunningVersion,
        TimeSpan healthTimeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        ArgumentNullException.ThrowIfNull(stopDirector);
        ArgumentNullException.ThrowIfNull(startDirector);
        ArgumentNullException.ThrowIfNull(readRunningVersion);

        var steps = new List<string>();
        EngineLog.Write($"[DirectorUpdateApply] applying {newVersion} -> {installTarget}");

        // 1. Stop the Director so its build unlocks. On Windows a single-file executable stays locked
        // until the process has fully exited, so waiting for the file to become writable is the real
        // exit barrier rather than the stop call returning.
        await stopDirector(ct);
        steps.Add("stopped the Director");
        if (!WaitUntilReplaceable(installTarget))
            return Fail(steps, $"the installed Director is still locked after stopping it ({installTarget}); nothing was swapped.");

        string? backup;
        try
        {
            backup = DirectorBuildSwapper.Place(installTarget, stagedBuild, DirectorBuildSwapper.LauncherBackupSuffix);
            steps.Add($"swapped in {newVersion} (backup: {backup ?? "none - nothing was installed"})");
        }
        catch (Exception exception)
        {
            // The swap failed. Almost always that means it failed while materializing the replacement
            // beside the target, so the installed build is untouched and there is nothing to undo.
            //
            // The one case that must not be left alone is a failure at the final rename, AFTER the
            // previous build was moved aside: that leaves NOTHING installed, and with the staged record
            // already cleared there would be nothing to try again either - a machine no future release
            // could reach. So the backup is put back precisely when there is no installed build to put it
            // over, which is the only condition under which restoring cannot possibly replace something
            // healthy with something older.
            if (!DirectorBuildSwapper.Inspect(installTarget).Exists
                && DirectorBuildSwapper.RestoreBackup(installTarget, DirectorBuildSwapper.LauncherBackupSuffix))
                steps.Add("the swap left nothing installed, so the previous build was put back");

            TryStart(startDirector, steps, "the build that is installed");
            return Fail(steps, $"the swap failed: {exception.Message}");
        }

        // 2. Start the new build and wait for IT to say so.
        TryStart(startDirector, steps, "the new build");
        var answered = await WaitForVersionAsync(readRunningVersion, newVersion, healthTimeout, ct);
        if (answered is not null)
        {
            // The new build is proved, so the backup has served its purpose. Deleting it here is what
            // stops a hundred-megabyte copy of every superseded build accumulating in the install
            // directory - and this is the only place that knows the answer.
            DirectorBuildSwapper.DeleteBackup(installTarget, DirectorBuildSwapper.LauncherBackupSuffix);
            steps.Add($"confirmed {answered} is answering; removed the backup");
            EngineLog.Write($"[DirectorUpdateApply] success: {newVersion} came up and answered");
            return new SelfUpdateResult(SelfUpdateOutcome.Updated, $"Director updated to {newVersion}.", steps);
        }

        // 3. The new build never answered as the new version. Put the previous build back.
        steps.Add($"the new build did NOT answer as {newVersion} within {healthTimeout.TotalSeconds:F0}s; rolling back");
        EngineLog.Write($"[DirectorUpdateApply] {newVersion} never answered; rolling back");

        await stopDirector(ct);
        WaitUntilReplaceable(installTarget);

        bool restored;
        try
        {
            restored = DirectorBuildSwapper.RestoreBackup(installTarget, DirectorBuildSwapper.LauncherBackupSuffix);
        }
        catch (Exception exception)
        {
            // The restore itself failed - for instance the failing build is wedged and still holding its
            // own file. Letting this throw would abandon the steps list, and the steps are the only
            // account of what is now on disk. Report it and start whatever is there, so the machine is at
            // least running something and the log says exactly what.
            restored = false;
            steps.Add($"THE ROLLBACK ITSELF FAILED: {exception.Message}");
            EngineLog.Write($"[DirectorUpdateApply] the rollback of {newVersion} FAILED: {exception.Message}");
        }

        if (!restored)
        {
            // Nothing to restore. Start whatever is installed and say plainly that the machine is on the
            // build that failed - a silent return here would read as a handled rollback.
            TryStart(startDirector, steps, "the build that failed, because there was nothing else");
            steps.Add("ROLLBACK NOT POSSIBLE - the previous build was not restored");
            return Fail(steps,
                $"{newVersion} did not come up and the previous build could not be restored; the Director is on {newVersion}.");
        }

        TryStart(startDirector, steps, "the restored previous build");
        var afterRollback = await WaitForAnyVersionAsync(readRunningVersion, healthTimeout, ct);
        steps.Add(afterRollback is not null
            ? $"rolled back to the previous build, which is answering as {afterRollback}"
            : "rolled back to the previous build, which has NOT answered yet");
        EngineLog.Write($"[DirectorUpdateApply] rolled back from {newVersion}; restored build answering={afterRollback ?? "not yet"}");

        return new SelfUpdateResult(
            SelfUpdateOutcome.RolledBack,
            $"Director update to {newVersion} failed to come up and was rolled back "
            + $"(restored build answering={afterRollback ?? "not yet"}).",
            steps,
            RestoredBuildAnswered: afterRollback is not null);
    }

    /// <summary>
    /// Poll until the running Director reports <paramref name="expectedVersion"/>, and return the
    /// version it reported. Null means it never did - which is the only outcome that may be called a
    /// failure, and the only one that triggers a rollback.
    /// </summary>
    private async Task<string?> WaitForVersionAsync(
        Func<CancellationToken, Task<string?>> readRunningVersion, string expectedVersion, TimeSpan timeout, CancellationToken ct)
        => await PollAsync(readRunningVersion, reported => VersionsMatch(expectedVersion, reported), timeout, ct);

    /// <summary>Poll until the running Director reports ANY version, for confirming a rolled-back build came up.</summary>
    private async Task<string?> WaitForAnyVersionAsync(
        Func<CancellationToken, Task<string?>> readRunningVersion, TimeSpan timeout, CancellationToken ct)
        => await PollAsync(readRunningVersion, reported => !string.IsNullOrWhiteSpace(reported), timeout, ct);

    private async Task<string?> PollAsync(
        Func<CancellationToken, Task<string?>> readRunningVersion, Func<string?, bool> accept, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var reported = await readRunningVersion(ct);
                if (accept(reported)) return reported;
            }
            catch
            {
                // Not up yet. A Director that is still starting refuses connections, and that is
                // expected for most of this wait rather than an error worth reporting.
            }
            try { await Task.Delay(_pollInterval, ct); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    /// <summary>Do these two versions describe the same build, ignoring any build metadata?</summary>
    public static bool VersionsMatch(string expected, string? reported)
    {
        var left = VersionUtil.TryParse(expected);
        var right = VersionUtil.TryParse(reported);
        return left is not null && right is not null && left == right;
    }

    /// <summary>
    /// Wait until the installed build can be replaced - meaning the stopped process has released it.
    /// A single file is tested by opening it for writing. An application bundle is a directory and is
    /// never held that way, so there is nothing to wait for and nothing to test; the same is true when
    /// no build is installed yet.
    /// </summary>
    private bool WaitUntilReplaceable(string installTarget)
    {
        if (!File.Exists(installTarget)) return true;

        var deadline = DateTime.UtcNow + _unlockTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var handle = File.Open(installTarget, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
        }
        EngineLog.Write($"[DirectorUpdateApply] {installTarget} is still locked after {_unlockTimeout.TotalSeconds:F0}s");
        return false;
    }

    /// <summary>
    /// Start the Director and record that it was started, or record why it could not be.
    ///
    /// A start that throws must not abandon the rest of this method: the steps taken so far are the only
    /// account of what is now on disk, and losing them to an exception on the way out is how a machine
    /// ends up in a state nobody can describe. The failure is reported, never swallowed - it lands in
    /// the steps and in the log, and the caller's own health poll then reports that nothing answered.
    /// </summary>
    private static void TryStart(Action startDirector, List<string> steps, string what)
    {
        try
        {
            startDirector();
            steps.Add($"started the Director on {what}");
        }
        catch (Exception exception)
        {
            steps.Add($"COULD NOT START the Director on {what}: {exception.Message}");
            EngineLog.Write($"[DirectorUpdateApply] starting the Director on {what} FAILED: {exception.Message}");
        }
    }

    private static SelfUpdateResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[DirectorUpdateApply] FAILED: {message}");
        return new SelfUpdateResult(SelfUpdateOutcome.Failed, message, steps);
    }
}
