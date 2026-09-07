using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>
/// What the launcher knows about the Director it supervises, read from outside it and with no network
/// of any kind: which Director it is, the version it is running, and how many sessions it is holding
/// (null when that could not be established).
/// </summary>
/// <param name="Conflict">
/// Null in the ordinary case. Set when more than one live process claimed this instance and the tie-break
/// resolved it anyway - the machine is still in a wrong state and every caller must pass this on to
/// somewhere a person will meet it, not swallow it because the answer came out right.
/// </param>
public sealed record DirectorStatus(string DirectorId, int Pid, string Version, int? Sessions,
    string? Conflict = null);

/// <summary>
/// How a restart ended. Three outcomes, and they are kept apart because a caller ACTS differently on
/// each: wait and try again, drain first, or go and look at the machine.
/// </summary>
public enum DirectorRestartVerdict
{
    /// <summary>The Director was stopped and a new one was started.</summary>
    Restarted,

    /// <summary>
    /// NOTHING WAS DONE. The restart was asked to happen only if the Director was empty, and it was not -
    /// or how busy it is could not be established, which is the same answer. The machine is exactly as it
    /// was, and the caller's next move is to finish the drain.
    /// </summary>
    Refused,

    /// <summary>
    /// SOMETHING WAS DONE AND IT DID NOT FINISH. The stop ran and no new Director was started, because
    /// another live process holds the instance. This is NOT a refusal and must never be reported as one:
    /// a refusal promises the machine is untouched, and here it may now be without a Director at all.
    /// </summary>
    NotStarted,
}

/// <summary>
/// What a restart asked to happen ONLY IF THE DIRECTOR IS EMPTY actually did.
/// </summary>
/// <param name="Verdict">Which of the three endings this was.</param>
/// <param name="Reason">
/// Why, when it was not a plain restart - and it always NAMES WHAT WAS OBSERVED, including the live
/// session count. Null only when <see cref="DirectorRestartVerdict.Restarted"/>. A caller must carry this
/// to whoever asked: a refusal nobody reads is indistinguishable from a restart that happened.
/// </param>
/// <param name="Sessions">
/// The live session count behind the verdict: the number that caused a refusal, and 0 for a guarded
/// restart that went ahead. NULL WHENEVER NO COUNT WAS ESTABLISHED - an unconditional restart, which never
/// asks, and a refusal whose whole reason is that the count could not be read. It is null rather than 0 on
/// purpose in both cases: "nobody looked" and "nobody was there" are the two facts this feature exists to
/// keep apart.
/// </param>
/// <param name="StartedPid">
/// The process id of the Director this restart started, when the operating system named one. Null on
/// macOS even for a successful start, where launchd owns the launch - so it is <paramref name="Restarted"/>
/// that says whether one came up, never this.
/// </param>
public sealed record DirectorRestartOutcome(DirectorRestartVerdict Verdict, string? Reason, int? Sessions,
    int? StartedPid = null)
{
    /// <summary>True only when a Director was stopped and another was started.</summary>
    public bool Restarted => Verdict == DirectorRestartVerdict.Restarted;
}

/// <summary>
/// Supervises the installed CC Director app - start, stop, restart, and the two facts the update path
/// needs (what version is running, and whether restarting it would interrupt live work).
///
/// NOTHING HERE USES A NETWORK, AND THAT IS THE POINT. This supervisor used to work by calling the
/// Director's own web interface over loopback: POST /shutdown to stop it and GET /healthz to read its
/// version and session count. Everything else in this product goes through the Gateway, deliberately,
/// so that there is one door - but process lifecycle cannot, because it has to work exactly when the
/// Gateway does not. A Director that could not be stopped without the internet could not be updated
/// without the internet either. So the three things this needs are taken from where they already are:
/// the version and the process id from the registration the Director itself writes, the session count
/// from the roster it maintains, and "exit now" from a named signal the operating system delivers.
///
/// Resolves the installed Director via <see cref="InstallLayout"/>:
///   - Windows: %LOCALAPPDATA%/cc-director/app/cc-director.exe
///   - macOS:   ~/Applications/Director.app (an application bundle - a directory)
///
/// Start strategy: on Windows the exe is started directly with UseShellExecute = true (clean parentage,
/// no pseudo-console inheritance). On macOS the bundle is handed to /usr/bin/open, so the Director
/// becomes a child of launchd, not of this launcher.
///
/// WHICH Director is answered by <see cref="DirectorInstanceLocator"/>, and the reasoning there is
/// worth reading before changing anything here: a process name and an image path are shared by every
/// named instance of one install, so a scan for them cannot tell two Directors apart on a machine that
/// runs several - which this one does.
/// </summary>
public sealed class DirectorSupervisor
{
    private readonly InstallLayout _layout;
    private readonly DirectorInstanceLocator _locator;

    public DirectorSupervisor() : this(InstallLayout.Default()) { }

    public DirectorSupervisor(InstallLayout layout)
        : this(layout, new DirectorInstanceLocator(
            Path.Combine(CcStorage.Root(), "instances", InstanceContext.DefaultSlug),
            CcStorage.DirectorInstances(),
            layout.PathFor(ComponentRegistry.Director))) { }

    public DirectorSupervisor(InstallLayout layout, DirectorInstanceLocator locator)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        ReadTheMachine = _locator.Resolve;
    }

    /// <summary>
    /// How long a Director is given to shut down after being asked, before the launcher stops waiting.
    /// </summary>
    public static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The installed Director path (per InstallLayout): the exe on Windows, the application bundle
    /// directory on macOS. This is what the supervisor manages. To launch arbitrary slot builds, use
    /// LaunchService directly with an explicit path.
    /// </summary>
    public string DirectorExePath => _layout.PathFor(ComponentRegistry.Director);

    /// <summary>Whether the installed Director exists on disk (exe on Windows, bundle directory on macOS).</summary>
    public bool DirectorExeExists => OperatingSystem.IsWindows()
        ? File.Exists(DirectorExePath)
        : Directory.Exists(DirectorExePath) || File.Exists(DirectorExePath);

    /// <summary>Where the supervised Director's identity is resolved from.</summary>
    public DirectorInstanceLocator Locator => _locator;

    /// <summary>
    /// How this supervisor reads the machine. Production always uses <see cref="DirectorInstanceLocator.Resolve"/>.
    ///
    /// IT IS A SEAM ONLY SO THAT DECIDING ON ONE READING AND ACTING ON ANOTHER CAN BE REPRODUCED. That
    /// defect needs two consecutive reads to DISAGREE, and outside a test they disagree only when the
    /// machine changes underneath them - a registration finishing its write, a Director starting - which
    /// cannot be arranged deterministically from the outside. Without the seam the fix is real and
    /// untested: a mutation that puts the second read back passes every test in the suite, which was
    /// verified before this was added rather than assumed.
    ///
    /// The same argument, in the same words, is why <see cref="DirectorInstanceLocator.ReadExecutablePath"/>
    /// exists. A guard that fails open is worse for being present and never exercised.
    /// </summary>
    internal Func<DirectorLookup> ReadTheMachine { get; set; } = null!;

    /// <summary>
    /// Whether a Director holds the supervised instance.
    ///
    /// An AMBIGUOUS result counts as running. It is not known which process it is, so nothing may be
    /// done TO it - but starting another one on top of two that already exist would make the mess
    /// worse, and that is the only decision this property is used for.
    /// </summary>
    public bool IsRunning => _locator.Resolve().Outcome != DirectorResolution.NotRunning;

    /// <summary>
    /// Start the installed Director if it is not already running.
    /// Windows: UseShellExecute = true for clean parentage (no pseudo-console inheritance).
    /// macOS: /usr/bin/open so the Director is parented by launchd, not this launcher.
    /// </summary>
    public void Start() => Start(out _);

    /// <summary>
    /// Start the installed Director if it is not already running, and SAY WHETHER ONE WAS ACTUALLY
    /// STARTED. The plain <see cref="Start()"/> above is the same call for callers that do not care.
    ///
    /// THE TWO ANSWERS ARE KEPT APART BECAUSE MERGING THEM PRODUCES A FALSE REPORT. Skipping the launch
    /// because another process already holds the instance is a perfectly ordinary outcome, and a restart
    /// that ends that way stopped a Director and started nothing - which must not be reported as
    /// "restarted". An earlier version of this returned only a process id and left the caller to guess
    /// from a null, which cannot be done: macOS legitimately has no process id to give.
    /// </summary>
    /// <param name="launchedProcessId">
    /// The process id the operating system gave the Director this call started, when there is one.
    /// Null both when nothing was started and on macOS, where /usr/bin/open hands the launch to launchd
    /// and no id comes back - which is why the RETURN VALUE, not this, says whether a launch happened.
    /// </param>
    /// <returns>True when this call launched a Director; false when one already held the instance.</returns>
    public bool Start(out int? launchedProcessId)
    {
        launchedProcessId = null;
        FileLog.Write($"[DirectorSupervisor] Start: target={DirectorExePath}");

        if (!DirectorExeExists)
            throw new FileNotFoundException($"Installed Director not found: {DirectorExePath}", DirectorExePath);

        var lookup = _locator.Resolve();
        if (lookup.Outcome != DirectorResolution.NotRunning)
        {
            FileLog.Write($"[DirectorSupervisor] Start: a Director already holds {_locator.InstanceHome} "
                          + $"({lookup.Outcome}); skipping. Claimants: {Describe(lookup)}");
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo
            {
                FileName = DirectorExePath,
                WorkingDirectory = Path.GetDirectoryName(DirectorExePath) ?? "",
                UseShellExecute = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for: {DirectorExePath}");

            FileLog.Write($"[DirectorSupervisor] Start: launched Director pid={proc.Id}");
            launchedProcessId = proc.Id;
            return true;
        }

        // macOS: hand the bundle to launchd via /usr/bin/open. open exits immediately;
        // the Director's own PID becomes visible through its instance registration file.
        var openPsi = new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            UseShellExecute = false,
        };
        openPsi.ArgumentList.Add(DirectorExePath);

        using var open = Process.Start(openPsi)
            ?? throw new InvalidOperationException($"Process.Start returned null for: /usr/bin/open {DirectorExePath}");
        open.WaitForExit();
        if (open.ExitCode != 0)
            throw new InvalidOperationException($"/usr/bin/open exited with code {open.ExitCode} for: {DirectorExePath}");

        FileLog.Write($"[DirectorSupervisor] Start: /usr/bin/open accepted launch of {DirectorExePath}");
        // launchd owns the launch, so there is no process id to report - the Director's own registration
        // file names it. The launch itself DID happen, which is what the return value says.
        return true;
    }

    /// <summary>
    /// Stop the running Director: raise its shutdown signal, then wait for the process to go.
    ///
    /// THE KILL IS STILL LAST AND IS STILL LOUD. A force-kill gives the Director no chance to end its
    /// sessions, say goodbye to the Gateway, or delete its crash journal - so it leaves a phantom
    /// "interrupted" entry and degrades the very signal used to tell a real crash from a deliberate
    /// stop. It stays only because a Director that cannot be stopped at all would be worse. What
    /// changed is that the graceful path no longer depends on a credential, a port, or anything
    /// answering: the signal is delivered by the operating system to one named Director.
    ///
    /// AN AMBIGUOUS DIRECTOR IS NOT STOPPED AT ALL. When two processes claim the supervised instance
    /// there is no way to tell which one the launcher is responsible for, and stopping the wrong
    /// Director is a worse outcome than stopping nothing - it takes somebody's live sessions with it.
    /// </summary>
    public Task StopAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DirectorSupervisor] StopAsync");
        return StopResolvedAsync(ReadTheMachine(), ct);
    }

    /// <summary>
    /// Stop the Director named by a reading of the machine that has ALREADY BEEN TAKEN.
    ///
    /// IT EXISTS SO THAT ONE READING CAN BOTH DECIDE AND ACT. A guarded restart asks whether the
    /// Director is empty and then stops it, and while those were two separate resolutions the second
    /// could see something the first never did - an independent review demonstrated exactly that, with a
    /// registration that was unreadable when the guard looked (so the machine read as nothing running,
    /// and the restart was permitted) and readable a moment later, at which point the stop found a live
    /// Director holding three sessions and would have taken them. Deciding on one reading and acting on
    /// another is the whole defect; handing the reading forward is the whole fix.
    ///
    /// <see cref="StopAsync"/> keeps its own behaviour by taking a fresh reading and calling this - which
    /// is right for a caller that has not already decided something on an earlier one.
    /// </summary>
    private async Task StopResolvedAsync(DirectorLookup lookup, CancellationToken ct)
    {
        if (lookup.Outcome == DirectorResolution.NotRunning)
        {
            FileLog.Write("[DirectorSupervisor] StopAsync: Director not running");
            return;
        }

        if (lookup.Outcome is DirectorResolution.Ambiguous or DirectorResolution.NotSupervised
            || lookup.Director is not { } director)
        {
            FileLog.Write($"[DirectorSupervisor] StopAsync: REFUSING to stop anything ({lookup.Outcome}) - "
                          + (lookup.Conflict ?? $"more than one live process claims {_locator.InstanceHome}, so "
                             + "stopping one of them would be a guess that could take somebody's live sessions "
                             + "with it.")
                          + $" Claimants: {Describe(lookup)}");
            return;
        }

        var signal = LifecycleSignalNames.DirectorShutdown(director.DirectorId);
        var delivered = LifecycleSignal.Raise(signal);
        FileLog.Write($"[DirectorSupervisor] StopAsync: asked {director.DirectorId} (pid={director.Pid}) to shut "
                      + $"down via {signal}; delivered={delivered}");

        if (delivered && await WaitForExitAsync(director.Pid, GracefulShutdownTimeout, ct))
        {
            FileLog.Write($"[DirectorSupervisor] StopAsync: pid={director.Pid} shut down cleanly");
            return;
        }

        // A KILL NEEDS THE IMAGE CERTIFIED, NOT MERELY A RESOLVED IDENTITY. Asking a process to stop is
        // harmless if it is not ours - it is a named signal nothing else listens for. Killing one is not,
        // and it is the irreversible half. The locator marks a claimant certified only when it was
        // compared against the installed application's image; when there was nothing to compare against
        // the answer is "not certified", never "probably fine". This is the last gate before the kill and
        // it deliberately does not trust the resolution alone.
        if (!director.IsInstalledImage)
        {
            FileLog.Write($"[DirectorSupervisor] StopAsync: pid={director.Pid} ({director.DirectorId}) did not shut "
                          + $"down after {signal}, and it is NOT certified as the installed Director's image, so it "
                          + "will NOT be force-killed. Ending a process this launcher cannot confirm is its own is "
                          + "worse than leaving it running: the same registration could name something that merely "
                          + "inherited the process id. Resolve the claim on this instance by hand.");
            return;
        }

        FileLog.Write(delivered
            ? $"[DirectorSupervisor] StopAsync: pid={director.Pid} was asked to shut down and was still running "
              + $"{GracefulShutdownTimeout.TotalSeconds:F0}s later. Force-killing it; expect a phantom crash-journal "
              + "entry, because a killed Director never gets to clean up."
            : $"[DirectorSupervisor] StopAsync: pid={director.Pid} is running but is NOT listening for {signal}, so "
              + "it cannot be asked to stop. Force-killing it; expect a phantom crash-journal entry. A Director that "
              + "does not listen for its own shutdown signal is a build older than this launcher, or one whose "
              + "startup failed before it began listening.");

        KillProcess(director.Pid);
    }

    /// <summary>
    /// What the supervised Director is running and how busy it is, read from the files it maintains.
    ///
    /// THIS IS THE WITNESS THE UPDATE PATH NEEDS, AND IT STILL COMES FROM OUTSIDE THE DIRECTOR. The
    /// version certifies a swap: a staged update is newer than what was running, so an answer carrying
    /// the new version cannot have come from the old build - where a mere liveness check ("something is
    /// there") could. That property survives the move off the network because the version is read from
    /// the registration the RUNNING PROCESS wrote when it started, not from the executable on disk.
    /// Reading the version off disk would look equivalent and would silently destroy the whole
    /// mechanism: the file on disk is replaced BEFORE the new Director is started, so it would report
    /// the new version whether or not anything came up, the wait for a healthy build would succeed
    /// instantly every time, and the roll-back to the previous build could never fire.
    ///
    /// Returns null when nothing is running, when which process is the Director is undecidable, or when
    /// the running Director's registration cannot be read - all of which mean "no answer", and none of
    /// which may be read as "idle".
    /// </summary>
    public DirectorStatus? ReadStatus()
    {
        var lookup = _locator.Resolve();
        if (lookup.Outcome != DirectorResolution.Running || lookup.Director is not { } director)
        {
            FileLog.Write($"[DirectorSupervisor] ReadStatus: no single Director owns {_locator.InstanceHome} "
                          + $"({lookup.Outcome}), so there is nothing to report. Claimants: {Describe(lookup)}");
            return null;
        }

        var sessions = _locator.ReadSessionCount(director);
        return new DirectorStatus(director.DirectorId, director.Pid, director.Version, sessions, lookup.Conflict);
    }

    /// <summary>
    /// Restart the Director: stop gracefully, wait, then start fresh.
    /// A staged update is applied by the launcher's update loop, not by the Director itself.
    ///
    /// UNCONDITIONAL, and its two callers are why. A person clicking "Restart Director" in the tray menu
    /// has decided; and the lifecycle signal a Director raises to have its own staged update installed is
    /// that Director asking for itself. A caller that must not interrupt live work asks for
    /// <see cref="RestartAsync(bool, CancellationToken)"/> with onlyIfEmpty instead.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DirectorSupervisor] RestartAsync");
        await StopAsync(ct);
        // Brief pause to let file locks release before relaunching.
        await Task.Delay(500, ct);
        Start();
        FileLog.Write("[DirectorSupervisor] RestartAsync: Director restarted");
    }

    /// <summary>
    /// Restart the Director, optionally ONLY IF IT IS EMPTY - that is, holding no live sessions.
    ///
    /// THIS IS THE MECHANICAL GUARANTEE BEHIND "NEVER FORCE". A drain empties a Director one session at a
    /// time, and a restart that arrives in the middle of one takes every session that is left with it.
    /// Until now the rule against that was a sentence in a written instruction, and a sentence cannot stop
    /// a mistake. This can: the launcher reads the count itself, from the files the Director maintains,
    /// and declines.
    ///
    /// THE REFUSAL NAMES THE COUNT, and that is the feature rather than a nicety. "Refused" tells the
    /// reader nothing they can act on; "3 live sessions" tells them the drain is not finished and roughly
    /// how much of it is left. Every refusal below says what was observed.
    ///
    /// WHAT IT DOES NOT PROMISE. The count is read, and then the Director is stopped - a session created
    /// between those two moments is not seen, and nothing here takes a lock. This closes the case that
    /// actually happens, a restart fired at a Director that is visibly still busy; it is not a settlement
    /// between two operators driving one machine against each other.
    /// </summary>
    /// <param name="onlyIfEmpty">When true, refuse unless the Director is provably holding no sessions.</param>
    public async Task<DirectorRestartOutcome> RestartAsync(bool onlyIfEmpty, CancellationToken ct = default)
    {
        int? sessions = null;

        // The reading the guard decided on, kept so the STOP can act on the same one. Null for an
        // unconditional restart, which decides nothing and so has nothing to carry forward.
        DirectorLookup? decidedOn = null;

        if (onlyIfEmpty)
        {
            if (RefuseRestartUnlessEmpty(out sessions, out var lookup) is { } refusal)
            {
                FileLog.Write($"[DirectorSupervisor] RestartAsync REFUSED (onlyIfEmpty): {refusal}");
                return new DirectorRestartOutcome(DirectorRestartVerdict.Refused, refusal, sessions);
            }

            decidedOn = lookup;

            // A PERMIT WITHOUT A ZERO COUNT IS A FAULT, NOT A PERMISSION. The guard reports its verdict as
            // the ABSENCE of a refusal, which is a shape a future edit can fail open through - one added
            // path that returns no refusal without having established a count, and a busy Director is
            // restarted and reported as having held none. It cannot pass here quietly.
            if (sessions is not 0)
                throw new InvalidOperationException(
                    "the emptiness guard permitted a restart without establishing that the Director is "
                    + $"holding no sessions (count={(sessions.HasValue ? sessions.Value.ToString() : "unknown")}). "
                    + "That is a fault in the guard, and the restart is abandoned rather than performed on "
                    + "an answer nobody has.");
        }

        FileLog.Write($"[DirectorSupervisor] RestartAsync: proceeding (onlyIfEmpty={onlyIfEmpty})");

        // A GUARDED RESTART STOPS WHAT THE GUARD SAW, AND NOTHING ELSE. Taking a second reading here
        // would let the stop act on a Director the decision never covered - including the case an
        // independent review demonstrated, where the guard read an unreadable registration as nothing
        // running, and a moment later the same registration resolved to a live Director holding three
        // sessions. An unconditional restart has decided nothing, so it still reads the machine here.
        if (decidedOn is { } reading)
            await StopResolvedAsync(reading, ct);
        else
            await StopAsync(ct);

        // Brief pause to let file locks release before relaunching.
        await Task.Delay(500, ct);
        var started = Start(out var pid);

        // STOPPED AND NOTHING STARTED IS NOT A RESTART. Start skips when something already holds the
        // instance, and reporting that as a restart would tell the caller their Director came back when
        // it did not.
        if (!started)
        {
            var noStart = $"no new Director was started on {Environment.MachineName}: another live process "
                          + $"already holds {_locator.InstanceHome}, and starting a second Director on top "
                          + "of it would make that worse. The stop ran first, so this machine may now be "
                          + "without the Director it had. Resolve the claim on this instance by hand.";
            FileLog.Write($"[DirectorSupervisor] RestartAsync: {noStart}");
            return new DirectorRestartOutcome(DirectorRestartVerdict.NotStarted, noStart, sessions);
        }

        FileLog.Write("[DirectorSupervisor] RestartAsync: Director restarted "
                      + $"(startedPid={(pid.HasValue ? pid.Value.ToString() : "none - launchd owns the launch")})");
        return new DirectorRestartOutcome(DirectorRestartVerdict.Restarted, null, sessions, pid);
    }

    /// <summary>
    /// Why a restart that was told to happen only if the Director is empty must NOT proceed, or null when
    /// it may.
    ///
    /// AN UNKNOWN COUNT IS A REFUSAL, NEVER AN EMPTY ONE. Two of the ways this answers no are answers it
    /// could not get: more than one live process claims the instance, so which one is the Director is
    /// undecidable; or the resolved Director's session roster will not read. A guard that treated either
    /// as idle would open exactly when the machine is already in a state nobody understands, which is the
    /// worst possible moment for a guard to open.
    ///
    /// A DIRECTOR THAT IS NOT RUNNING IS EMPTY, and that is a real answer rather than a missing one:
    /// nothing is holding a session because nothing is holding anything. The restart then does what a
    /// restart of a stopped Director has always done, which is start one.
    ///
    /// AND HERE IS THE LIMIT OF THAT, STATED RATHER THAN LEFT TO BE INFERRED. "Not running" is the
    /// locator's answer, and the locator reaches it by SKIPPING what it cannot read: a registration file
    /// that will not parse, or a live process that will not say when it started, is passed over, and a
    /// machine whose only registration is one of those looks empty. The locator does that deliberately and
    /// it is safe for the question it was written for - whether to STOP something, where skipping means
    /// declining - but this guard asks the opposite question, and there the same answer means PERMIT. So a
    /// Director whose registration is corrupt is not protected by this guard. That is inherited behaviour,
    /// identical for an unconditional restart and for the update path, and it is NOT something the flag
    /// closes; closing it needs the locator to distinguish "nothing is there" from "I could not read what
    /// is there", which is a change to <see cref="DirectorInstanceLocator"/> and not to this guard.
    /// </summary>
    /// <param name="sessions">
    /// The count this verdict was reached on: the live sessions found, 0 when nothing is running, and null
    /// when it could not be established. It is handed out here rather than read again by the caller so the
    /// number reported and the number decided on cannot be two different readings of a moving machine.
    /// </param>
    /// <param name="lookup">
    /// THE READING THIS VERDICT WAS REACHED ON, handed to the caller so the thing that ACTS can act on
    /// the same one. A restart that resolves the machine again between deciding and stopping is deciding
    /// on one reading and acting on another, which is the defect this whole guard exists to prevent,
    /// one layer down.
    /// </param>
    public string? RefuseRestartUnlessEmpty(out int? sessions, out DirectorLookup lookup)
    {
        sessions = null;
        lookup = ReadTheMachine();

        if (lookup.Outcome == DirectorResolution.NotRunning)
        {
            FileLog.Write("[DirectorSupervisor] RefuseRestartUnlessEmpty: no Director is running, so it is "
                          + "holding no sessions and the restart may proceed as a start.");
            sessions = 0;
            return null;
        }

        // EXHAUSTIVE BY NAME, NOT BY NULLNESS. Only a Running lookup carries a Director today, so testing
        // the Director alone would be right - and it would stop being right the moment a new outcome is
        // added that carries one, silently, by treating it as running and reading its count. The rule this
        // guard exists for is that an answer nobody named must never become the permissive branch, and
        // that has to hold for answers nobody has invented yet.
        if (lookup.Outcome != DirectorResolution.Running || lookup.Director is not { } director)
            return $"refusing to restart the Director on {Environment.MachineName}: which process owns "
                   + $"{_locator.InstanceHome} is undecidable ({lookup.Outcome}), so how many sessions it is "
                   + "holding cannot be read, and an unknown count is never read as empty. Claimants: "
                   + $"{Describe(lookup)}.";

        // A RESOLVED CONFLICT IS STILL A MACHINE IN A WRONG STATE. The locator reports one when several
        // live processes claimed this instance and the tie-break could name the installed one - so there
        // IS a Director and its count can be read, and reading it would be answering the wrong question.
        // Something else is also claiming this home; stopping the one we chose leaves the other holding
        // it, and the start that follows finds the instance taken and declines. The locator's own
        // contract says every caller must carry a resolved conflict somewhere a person meets it, and for
        // a guard whose job is to refuse on a machine nobody understands, that means refusing.
        if (lookup.Conflict is { } conflict)
            return $"refusing to restart the Director {director.DirectorId} (pid {director.Pid}) on "
                   + $"{Environment.MachineName}: more than one live process claims "
                   + $"{_locator.InstanceHome}, and although the tie-break could say which one is the "
                   + $"installed Director, the machine is still in a state nobody asked for - {conflict} "
                   + $"Claimants: {Describe(lookup)}. Resolve the claim on this instance by hand, then "
                   + "ask again.";

        sessions = _locator.ReadSessionCount(director);
        if (sessions is null)
            return $"refusing to restart the Director {director.DirectorId} (pid {director.Pid}) on "
                   + $"{Environment.MachineName}: it is running and its live session roster could not be "
                   + "read, so how many sessions it is holding is unknown, and an unknown count is never "
                   + "read as empty.";

        if (sessions > 0)
            return $"refusing to restart the Director {director.DirectorId} (pid {director.Pid}) on "
                   + $"{Environment.MachineName}: it is holding {sessions} live "
                   + $"{(sessions == 1 ? "session" : "sessions")}. Drain it first - every session writes a "
                   + "handover and is closed - then ask again.";

        FileLog.Write($"[DirectorSupervisor] RefuseRestartUnlessEmpty: {director.DirectorId} (pid "
                      + $"{director.Pid}) is holding 0 live sessions; the restart may proceed.");
        return null;
    }

    private static string Describe(DirectorLookup lookup)
        => lookup.Candidates.Count == 0 ? "none" : string.Join(" | ", lookup.Candidates);

    /// <summary>
    /// Wait for a process id to leave the process table. True when it did within
    /// <paramref name="timeout"/>.
    /// </summary>
    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private static void KillProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                FileLog.Write($"[DirectorSupervisor] killed pid={pid}");
            }
        }
        catch (ArgumentException)
        {
            // It exited between the wait and the kill. Nothing to do.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] kill of pid={pid} failed: {ex.Message}");
        }
    }
}
