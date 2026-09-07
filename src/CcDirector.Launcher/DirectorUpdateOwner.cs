using CcDirector.Core.Storage;
using CcDirector.Core.Update;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>A staged Director update the launcher found, and the state file it was recorded in.</summary>
/// <param name="Version">The version waiting to be installed.</param>
/// <param name="StagedBuild">
/// What has to become the install: the downloaded executable on Windows, the downloaded application
/// bundle on macOS. The Director records the binary INSIDE the bundle, because that is what it used to
/// run in its own swap mode; the enclosing bundle is what actually gets installed, so it is resolved
/// here rather than left for each caller to remember.
/// </param>
/// <param name="InstallTarget">The path the staged build must become.</param>
/// <param name="StateFilePath">The exact file this was read from, and the file any answer is written back to.</param>
public sealed record StagedDirectorUpdate(string Version, string StagedBuild, string InstallTarget, string StateFilePath);

/// <summary>Why a staged update was not applied on this pass, or that one was.</summary>
public enum DirectorUpdateDecision
{
    /// <summary>No update is staged for the installed Director.</summary>
    NothingStaged,
    /// <summary>An update is staged but the Director is busy, so it waits. Nothing was touched.</summary>
    HeldBecauseBusy,
    /// <summary>An update is staged but the Director could not be asked whether it is busy, so it waits.</summary>
    HeldBecauseUnknown,
    /// <summary>
    /// An update is staged, but another binary swap is already running on this machine - a Director
    /// installing the launcher's own update, or a second launcher. Nothing was touched.
    /// </summary>
    HeldBecauseAnotherSwapIsRunning,
    /// <summary>An update is staged but the Director is not running, and it is not this loop's business to start one.</summary>
    HeldBecauseDirectorNotRunning,
    /// <summary>The staged update is one that already failed to start and was pinned away from.</summary>
    SkippedPinnedBadVersion,
    /// <summary>The update was applied and the new version answered.</summary>
    Applied,
    /// <summary>The update was applied, did not come up, and the previous build was put back.</summary>
    RolledBack,
    /// <summary>The attempt failed before or during the swap; see the log for what was left where.</summary>
    Failed,
}

/// <summary>
/// The launcher's ownership of the Director's update (issue #1033).
///
/// THE RULE, applied without asking anybody: if a Director update is staged and the Director has no
/// sessions running, install it. Nothing is lost and nothing is interrupted, so there is nothing to
/// wait for and no reason to prompt. The gate on running sessions is the policy the Director already
/// had and it was the right policy - a Director with live work is never restarted out from under it -
/// it simply belonged here, in the process that can act on it safely.
///
/// One thing this does NOT do is start a Director that is not running. Updating a closed Director would
/// mean reopening an application somebody deliberately closed, and installing a build with nothing
/// running to witness whether it works; a closed Director picks the update up on its next start, which
/// is a start somebody asked for.
///
/// WHY IT MOVED. The Director cannot honestly apply its own update: whatever replaces the binary has to
/// outlive the process being replaced, so the only witness to the relaunch is the process that just
/// exited. The launcher is still running afterwards, so it can stop, swap, start, and then keep asking
/// until the NEW VERSION answers - and put the previous build back when it never does.
///
/// TWO THINGS HERE ARE EASY TO GET WRONG AND BOTH WOULD FAIL SILENTLY:
///
/// First, WHERE the staged update is recorded. The state file resolves against the calling process's
/// own storage home, and the installed Director keeps its whole home one level in, under its instance
/// folder. A launcher that simply asked for "the" updater state would read an absent file at the
/// storage root and conclude every single time that nothing was staged - wired, logged, and never once
/// firing. Every candidate location is scanned, the same way instance registrations are.
///
/// Second, WHEN the staged record is cleared. It is cleared BEFORE the new build is started, not after
/// it is certified. If it were still set when a Director came up, that Director's own startup path
/// would see a staged update newer than itself and hand itself to the swap again - and after a rollback
/// that means the restored build immediately reinstalls the build that just failed, on a loop, with the
/// machine ending on the broken version. Losing the record of a download is cheap; that loop is not
/// recoverable in the field.
/// </summary>
public sealed class DirectorUpdateOwner
{
    private readonly DirectorSupervisor _supervisor;
    private readonly DirectorUpdateApply _apply;
    private readonly TimeSpan _healthTimeout;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public DirectorUpdateOwner(DirectorSupervisor supervisor, DirectorUpdateApply? apply = null, TimeSpan? healthTimeout = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _apply = apply ?? new DirectorUpdateApply();
        // A cold Director start walks a splash screen, the engine and the control interface before it
        // answers anything, and this has to be long enough that a slow machine is not called a failure.
        _healthTimeout = healthTimeout ?? TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// The machine-wide lock this swap takes. <see cref="BinarySwapLock.Name"/> in production, always;
    /// a test overrides it so it can hold a lock of its own without contending with everything else on
    /// the machine. See the parameter note on <see cref="BinarySwapLock.RunExclusivelyAsync"/>.
    ///
    /// INTERNAL, so the override is reachable only from the test assemblies. Public, it was a knob a
    /// production caller could turn - and two owners on two names exclude nothing at all while looking,
    /// in every log and every test, exactly like a lock that works. The guarantee should not rest on
    /// nobody happening to set it.
    /// </summary>
    internal string SwapLockName { get; init; } = BinarySwapLock.Name;

    /// <summary>
    /// Decide whether a staged update may be installed right now: only when one is staged AND the
    /// Director holds no sessions, so restarting into the new build cannot interrupt live work.
    ///
    /// Pure decision, unit-tested. This is the gate that used to sit inside the Director; the rule is
    /// unchanged, only the process that acts on it.
    /// </summary>
    /// <remarks>
    /// The rule itself moved to <see cref="UpdateApplyRule"/> when the status display began offering an
    /// "install it now" action (issue #1030). That offer has to be governed by the same rule the launcher
    /// acts on, and a second copy of it would eventually let the display offer something the launcher
    /// refuses to do. This stays as the launcher's name for it.
    /// </remarks>
    public static bool ShouldApply(bool hasStagedUpdate, int runningSessionCount)
        => UpdateApplyRule.ShouldApply(hasStagedUpdate, runningSessionCount);

    /// <summary>
    /// One pass of the launcher's ownership: look for a staged Director update, and install it if the
    /// Director is idle. Never throws - this runs on a background loop, so every failure is reported
    /// through the returned decision and the log.
    /// </summary>
    public async Task<DirectorUpdateDecision> RunOnceAsync(CancellationToken ct = default)
    {
        if (!await _oneAtATime.WaitAsync(TimeSpan.Zero, ct))
        {
            FileLog.Write("[DirectorUpdateOwner] a previous pass is still running; skipping this one.");
            return DirectorUpdateDecision.HeldBecauseUnknown;
        }

        try
        {
            return await RunOnceCoreAsync(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpdateOwner] RunOnceAsync FAILED: {ex}");
            return DirectorUpdateDecision.Failed;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<DirectorUpdateDecision> RunOnceCoreAsync(CancellationToken ct)
    {
        var staged = FindStagedUpdate();
        if (staged is null)
            return DirectorUpdateDecision.NothingStaged;

        // A Director that is not running is left alone. It is tempting to treat "no process" as the
        // emptiest possible idle case and update it, but that would mean starting an application the
        // person at this machine had deliberately closed - and it would install a build with nothing
        // running to witness whether it works. A closed Director picks the update up the next time it is
        // opened, which is a start somebody asked for.
        if (!_supervisor.IsRunning)
        {
            FileLog.Write($"[DirectorUpdateOwner] {staged.Version} is staged but the Director is not running. Leaving both "
                          + "alone: it will be applied on the next start, and nothing here reopens a closed Director.");
            return Record(staged, DirectorUpdateDecision.HeldBecauseDirectorNotRunning,
                "The Director was not running when the launcher looked, so nothing was installed.");
        }

        // How busy is it? A Director whose state cannot be established must NOT be read as idle: it may
        // well be holding sessions, and acting without the evidence is exactly the guess this change
        // exists to remove. This is also where an UNDECIDABLE machine stops - two live processes both
        // claiming the supervised instance means the launcher does not know whose sessions it would be
        // interrupting, and a Director that cannot be identified is never updated over.
        var status = _supervisor.ReadStatus();
        if (status is null)
        {
            FileLog.Write($"[DirectorUpdateOwner] {staged.Version} is staged but the running Director could not be "
                          + "identified or read, so whether it is idle is unknown; holding the update rather than guessing.");
            return Record(staged, DirectorUpdateDecision.HeldBecauseUnknown,
                "The running Director could not be identified when the launcher looked at how busy it was.");
        }

        // A resolved conflict is carried into every decision this pass records, so it reaches the update
        // status a person actually reads rather than only the launcher's log. The machine is in a state
        // that should not exist; the tie-break made it possible to carry on, which is exactly why it must
        // not become invisible.
        var conflictNote = status.Conflict is { Length: > 0 } c
            ? $"MORE THAN ONE PROCESS CLAIMS THIS DIRECTOR'S INSTANCE - {c}. The installed one was used. "
            : "";
        if (conflictNote.Length > 0)
            FileLog.Write($"[DirectorUpdateOwner] proceeding under a resolved instance conflict: {conflictNote}");

        // THE UPDATE MUST NOT BEGIN A CYCLE IT CANNOT FINISH, and this is the regression the fix for
        // issue #2730 introduced before an independent review caught it.
        //
        // A corrupt registration beside one good live Director resolves Running, so this pass used to
        // proceed: it stops the Director - which deletes that Director's OWN good registration - and then
        // only the corrupt file is left, at which point DirectorSupervisor.Start REFUSES rather than
        // launching a second Director into an instance home it cannot read. The health wait then times
        // out, the rollback tries the same refused start, and the machine is left with NO Director and
        // every relaunch refusing.
        //
        // That is worse than the fail-open it replaced: before, the corrupt file was ignored and the
        // Director came back. A fix that turns a recoverable state into a permanent one is not a fix.
        //
        // So the rule is the same one already applied to an unreadable roster, on the same evidence: a
        // pass that cannot establish the state of this instance home holds, and says what it could not
        // read. The corrupt file is repaired or removed, and the update proceeds on the next pass.
        if (status.Unreadable.Count > 0)
        {
            var what = string.Join("; ", status.Unreadable);
            FileLog.Write($"[DirectorUpdateOwner] {staged.Version} is staged and the Director is running, but "
                          + $"{status.Unreadable.Count} other claim(s) on this instance home could not be read: {what}. "
                          + "HOLDING the update: stopping this Director would remove its own registration and leave "
                          + "only what cannot be read, and the start afterwards would refuse - which would take a "
                          + "working Director down permanently rather than updating it.");
            return Record(staged, DirectorUpdateDecision.HeldBecauseUnknown,
                conflictNote + "Something else claiming this Director's instance home could not be read, so "
                + "restarting it could not be completed safely: " + what);
        }

        if (status.Sessions is null)
        {
            FileLog.Write($"[DirectorUpdateOwner] {staged.Version} is staged but the Director's session roster could "
                          + "not be read; holding the update rather than guessing.");
            return Record(staged, DirectorUpdateDecision.HeldBecauseUnknown,
                conflictNote + "The Director did not have a readable roster saying how many sessions it holds.");
        }

        // The Director is already the staged version - it was installed by a route other than this one,
        // or the record simply outlived the install. Clear the record instead of reinstalling.
        if (status.Version is { Length: > 0 } reportedVersion && DirectorUpdateApply.VersionsMatch(staged.Version, reportedVersion))
        {
            FileLog.Write($"[DirectorUpdateOwner] the running Director already reports {reportedVersion}; "
                          + "clearing the staged record without installing anything.");
            ClearStagedRecord(staged);
            return DirectorUpdateDecision.NothingStaged;
        }

        var sessions = status.Sessions.Value;
        if (!ShouldApply(hasStagedUpdate: true, runningSessionCount: sessions))
        {
            FileLog.Write($"[DirectorUpdateOwner] {staged.Version} is staged but {sessions} session(s) are running; "
                          + "holding it until the Director is idle. No session is ever interrupted to update.");
            return Record(staged, DirectorUpdateDecision.HeldBecauseBusy,
                conflictNote + $"{sessions} session(s) were running, so the update waits rather than interrupting them.");
        }

        FileLog.Write($"[DirectorUpdateOwner] installing {staged.Version}: staged={staged.StagedBuild}, "
                      + $"target={staged.InstallTarget}, sessions=0, currentVersion="
                      + $"{(status.Version.Length > 0 ? status.Version : "unknown")}");

        // THE SWAP IS EXCLUSIVE, MACHINE-WIDE (issue #2719). The Director now owns the LAUNCHER's
        // update, which stops and replaces this process while it runs - so the two owners stop each
        // other's process and each one destroys the other's evidence. Taken here, BEFORE the staged
        // record is cleared: clearing it and then finding the lock held would throw away the record of
        // a download for a pass that did nothing. See BinarySwapLock for both directions of the race.
        return await BinarySwapLock.RunExclusivelyAsync(
            () => SwapAsync(staged, conflictNote, ct),
            whenBusy: why =>
            {
                FileLog.Write($"[DirectorUpdateOwner] {staged.Version} is staged, but {why}");
                return Record(staged, DirectorUpdateDecision.HeldBecauseAnotherSwapIsRunning, conflictNote + why);
            },
            who: "director-update",
            name: SwapLockName);
    }

    /// <summary>
    /// The swap itself, run while <see cref="BinarySwapLock"/> is held: claim the staged record, stop,
    /// replace, start, and wait for the new version to answer.
    /// </summary>
    private async Task<DirectorUpdateDecision> SwapAsync(StagedDirectorUpdate staged, string conflictNote, CancellationToken ct)
    {
        // Claim it BEFORE anything is started, so no Director that comes up during this can hand itself
        // to its own swap. See the class comment - the alternative is a rollback loop into a dead build.
        ClearStagedRecord(staged);

        var result = await _apply.ApplyAsync(
            staged.InstallTarget,
            staged.StagedBuild,
            staged.Version,
            stopDirector: token => _supervisor.StopAsync(token),
            startDirector: () => _supervisor.Start(),
            // The version of whatever is running NOW, re-read on every poll while the swap settles. It
            // comes from the registration the running process wrote, so a Director that never came up
            // cannot supply it - which is what makes the roll-back below reachable.
            readRunningVersion: _ => Task.FromResult(_supervisor.ReadStatus()?.Version),
            healthTimeout: _healthTimeout,
            ct: ct);

        FileLog.Write($"[DirectorUpdateOwner] {staged.Version}: {result.Outcome} - {result.Message}");
        foreach (var step in result.Steps)
            FileLog.Write($"[DirectorUpdateOwner]   step: {step}");

        if (result.Outcome == SelfUpdateOutcome.Updated)
            return Record(staged, DirectorUpdateDecision.Applied, conflictNote + result.Message);

        // The build did not come up. Pin it so neither the launcher nor the Director tries it again when
        // it is offered a second time - the Director re-downloads on its own schedule and would
        // otherwise present the same dead build every hour, for ever.
        PinBadVersion(staged);
        return Record(staged,
            result.Outcome == SelfUpdateOutcome.RolledBack
                ? DirectorUpdateDecision.RolledBack
                : DirectorUpdateDecision.Failed,
            conflictNote + result.Message);
    }

    /// <summary>
    /// Find a staged update that belongs to the INSTALLED Director, across every place the record can
    /// live. Returns null when there is nothing to do, having said in the log why.
    /// </summary>
    public StagedDirectorUpdate? FindStagedUpdate()
    {
        foreach (var stateFile in UpdaterStateFiles())
        {
            if (!File.Exists(stateFile)) continue;

            var state = UpdaterState.LoadFrom(stateFile);
            if (string.IsNullOrEmpty(state.StagedVersion)
                || string.IsNullOrEmpty(state.StagedExecutable)
                || string.IsNullOrEmpty(state.InstallTarget))
                continue;

            // Only ever install over the Director this launcher manages. A development slot build
            // records its own path here, and installing that over the shipped install would replace the
            // user's Director with somebody's test build.
            if (!PathsEqual(state.InstallTarget, _supervisor.DirectorExePath))
            {
                FileLog.Write($"[DirectorUpdateOwner] ignoring a staged update in {stateFile}: it targets "
                              + $"{state.InstallTarget}, which is not the installed Director ({_supervisor.DirectorExePath}).");
                continue;
            }

            if (!string.IsNullOrEmpty(state.PinnedBadVersion)
                && string.Equals(state.PinnedBadVersion, state.StagedVersion, StringComparison.Ordinal))
            {
                FileLog.Write($"[DirectorUpdateOwner] ignoring staged {state.StagedVersion}: it is pinned as a build that "
                              + "already failed to start.");
                continue;
            }

            var stagedBuild = ResolveStagedBuild(state.StagedExecutable);
            if (!BuildExists(stagedBuild))
            {
                FileLog.Write($"[DirectorUpdateOwner] ignoring staged {state.StagedVersion}: the downloaded build is no "
                              + $"longer at {stagedBuild}.");
                continue;
            }

            return new StagedDirectorUpdate(state.StagedVersion, stagedBuild, state.InstallTarget, stateFile);
        }

        return null;
    }

    /// <summary>
    /// What actually has to be installed, given what the Director recorded. On macOS it records the
    /// binary inside the downloaded application bundle, and the bundle is the thing that becomes the
    /// install; on Windows the recorded executable IS the build.
    /// </summary>
    public static string ResolveStagedBuild(string stagedExecutable)
        => UpdateInstaller.AppBundleOf(stagedExecutable) ?? stagedExecutable;

    private static bool BuildExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Every file a Director could have recorded a staged update in: this process's own storage home,
    /// plus one per named instance home. Mirrors the instance-registration scan in
    /// <see cref="DirectorSupervisor.InstanceRegistrationDirectories"/> and exists for the same reason -
    /// the launcher runs at the storage root while the installed Director lives one level in.
    /// </summary>
    public static IEnumerable<string> UpdaterStateFiles()
    {
        yield return UpdaterState.FilePath;

        var instancesRoot = Path.Combine(CcStorage.Root(), "instances");
        string[] instanceHomes;
        try
        {
            if (!Directory.Exists(instancesRoot)) yield break;
            instanceHomes = Directory.GetDirectories(instancesRoot);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpdateOwner] cannot list {instancesRoot}: {ex.Message}");
            yield break;
        }

        foreach (var home in instanceHomes)
            yield return Path.Combine(home, "config", "director", "updater-state.json");
    }

    /// <summary>
    /// Write down what this pass decided, so something other than the launcher's own log can say it
    /// (issue #1030).
    ///
    /// Until this existed, every one of these decisions was reachable only by reading a log file on the
    /// machine. Two of them are the whole difference between a working feature and an apparently broken
    /// one: HeldBecauseBusy is "waiting for your sessions to finish", which from outside looks exactly
    /// like a stall, and RolledBack is "the new build did not come up, so the old one is back", which a
    /// person had no way to learn at all. Both were invisible, and invisible is the defect.
    ///
    /// Written into the same file the staged record lives in, and into that exact file rather than the
    /// launcher's own storage home - see <see cref="UpdaterStateFiles"/> for why the two are not the
    /// same place. Failing to write it must never change what the launcher DID, so this only logs.
    /// </summary>
    internal static DirectorUpdateDecision Record(StagedDirectorUpdate staged, DirectorUpdateDecision decision, string? detail)
    {
        try
        {
            var state = UpdaterState.LoadFrom(staged.StateFilePath);
            state.LastApplyDecision = decision.ToString();
            state.LastApplyDecisionAt = DateTimeOffset.UtcNow;
            state.LastApplyVersion = staged.Version;
            state.LastApplyDetail = detail;
            state.SaveTo(staged.StateFilePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpdateOwner] could not record decision {decision} in {staged.StateFilePath}: {ex.Message}");
        }

        return decision;
    }

    /// <summary>
    /// Clear the staged record in the file it was read from, leaving every other field alone. Read again
    /// before writing, because the Director owns this file too and may have touched it since.
    /// </summary>
    private static void ClearStagedRecord(StagedDirectorUpdate staged)
    {
        try
        {
            var state = UpdaterState.LoadFrom(staged.StateFilePath);
            state.StagedVersion = null;
            state.StagedExecutable = null;
            state.InstallTarget = null;
            state.ApplyAttempts = 0;
            state.ApplyAttemptVersion = null;
            state.SaveTo(staged.StateFilePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpdateOwner] could not clear the staged record in {staged.StateFilePath}: {ex.Message}");
        }
    }

    /// <summary>Record that this version does not start, so it is never offered again.</summary>
    private static void PinBadVersion(StagedDirectorUpdate staged)
    {
        try
        {
            var state = UpdaterState.LoadFrom(staged.StateFilePath);
            state.PinnedBadVersion = staged.Version;
            state.SaveTo(staged.StateFilePath);
            FileLog.Write($"[DirectorUpdateOwner] pinned {staged.Version} as a build that does not start.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpdateOwner] could not pin {staged.Version} in {staged.StateFilePath}: {ex.Message}");
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                comparison);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpdateOwner] cannot compare paths '{a}' and '{b}': {ex.Message}");
            return false;
        }
    }
}
