using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>
/// The Directors this launcher stopped ON PURPOSE, so the crash watch never mistakes one of them for a crash.
///
/// It is needed because a stop is not always clean. <see cref="DirectorSupervisor"/> force-kills a Director that
/// did not shut down when asked, and a killed Director never deletes its crash journal - so on disk it looks
/// exactly like one that crashed. Without this record, the launcher would stop a Director (for an update, or
/// because a person chose Stop) and then start it again a few seconds later, which is the launcher fighting itself.
///
/// ONE PER PROCESS. The launcher builds several supervisors - the tray, the command stream, the update pass, the
/// lifecycle signals - and a stop made by any of them must be seen by the one watch, so the default is shared.
/// </summary>
public sealed class DirectorStopLedger
{
    /// <summary>How long a recorded stop is kept when nothing claims it, so the record cannot grow without end.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    /// <summary>The one ledger every supervisor in this process records into.</summary>
    public static DirectorStopLedger Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<(string DirectorId, int Pid), DateTime> _stopped = new();

    /// <summary>Record that this launcher is stopping the Director <paramref name="directorId"/> running as
    /// <paramref name="pid"/>. Called BEFORE the stop is attempted, so a stop that ends in a kill is covered.</summary>
    public void Record(string directorId, int pid)
    {
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var old in _stopped.Where(e => e.Value < cutoff).Select(e => e.Key).ToList())
                _stopped.Remove(old);
            _stopped[(directorId, pid)] = DateTime.UtcNow;
        }
        FileLog.Write($"[DirectorStopLedger] Record: directorId={directorId}, pid={pid} is being stopped on purpose");
    }

    /// <summary>Take back a record for a stop that did NOT end the process - the kill was refused or failed - so the
    /// Director, still running, is not excused for a real crash later (review of #4118, observation 5).</summary>
    public void Forget(string directorId, int pid)
    {
        bool removed;
        lock (_gate)
            removed = _stopped.Remove((directorId, pid));
        if (removed)
            FileLog.Write($"[DirectorStopLedger] Forget: directorId={directorId}, pid={pid} was not stopped after all; a later crash counts");
    }

    /// <summary>True, once, when this launcher stopped that Director on purpose. The entry is removed, so the
    /// same answer cannot excuse a later crash of a process that reused the id.</summary>
    public bool Consume(string directorId, int pid)
    {
        lock (_gate)
            return _stopped.Remove((directorId, pid));
    }
}

/// <summary>What one look at the machine by the crash watch concluded.</summary>
public enum DirectorCrashWatchOutcome
{
    /// <summary>No installed Director has been seen running since the last decision, so there is nothing to watch.</summary>
    NothingToWatch,

    /// <summary>An installed Director is running and is being watched.</summary>
    Watching,

    /// <summary>The machine could not be read to a single answer (several claimants, or one that would not read).
    /// Nothing is done on a reading nobody understands; the watch looks again next time.</summary>
    Undecided,

    /// <summary>The watched Director's registration is gone but its process has not left yet - a shutdown in
    /// progress. Looked at again next time, when the answer is final.</summary>
    StillExiting,

    /// <summary>The watched Director ended and deleted its crash journal: a quit, a handover, a clean stop.</summary>
    EndedOnPurpose,

    /// <summary>The watched Director was stopped by this launcher (see <see cref="DirectorStopLedger"/>).</summary>
    StoppedByLauncher,

    /// <summary>The watched Director died with its crash journal still on disk, and a new one was started.</summary>
    Restarted,

    /// <summary>A crash was seen, but an update or launcher swap holds the swap lock. Started next time instead.</summary>
    RestartHeldOff,

    /// <summary>A crash was seen and a Director was already running again by the time the start ran.</summary>
    AlreadyRunningAgain,

    /// <summary>The restart limit was reached. Nothing more is started until a Director is seen running again.</summary>
    GaveUp,
}

/// <summary>
/// Starts the installed Director again when it CRASHES, and leaves it alone when it ends on purpose.
///
/// HOW A CRASH IS TOLD FROM A QUIT. Not by the exit code: on macOS the launcher does not start the Director as
/// its own child (launchd does, through /usr/bin/open), and a process cannot read the exit code of a process it
/// did not start. The Director's crash journal answers instead, on every platform. A running Director keeps
/// <c>config/director/crash-journal/&lt;id&gt;.json</c> under its instance home, and its clean shutdown deletes
/// it (<see cref="CcDirector.Core.Sessions.DirectorCrashJournal.MarkClean"/>). Every deliberate end of a running
/// Director goes through that shutdown - a person quitting it, its shutdown signal, Smart Restart and an update
/// through this launcher - so a Director whose process is gone while its journal is still there did not end on
/// purpose. The one exception is a stop this launcher made itself and finished with a kill, which leaves the
/// journal behind; <see cref="DirectorStopLedger"/> excuses exactly those.
///
/// WHY THE LAUNCHER AND NOT THE OPERATING SYSTEM. A launch agent or a scheduled task watches the one process it
/// started. The first update or Smart Restart replaces that process, and the watch ends. This watch follows the
/// INSTANCE: whichever installed Director holds it now is the one watched, so it survives every handover.
///
/// IT NEVER SPINS. At most <see cref="MaxRestarts"/> starts inside <see cref="RestartWindow"/>; after that it
/// says so in the log and starts nothing more until a Director is seen running again (a person started one).
///
/// WHAT IT DOES NOT SEE. A Director that dies before it has registered was never seen running, so it is not
/// restarted. That is the safe direction: nothing is started that was not known to be running.
/// </summary>
public sealed class DirectorCrashWatch
{
    /// <summary>How often the launcher looks.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>The most restarts inside <see cref="RestartWindow"/> before the watch gives up.</summary>
    public const int MaxRestarts = 5;

    /// <summary>The window <see cref="MaxRestarts"/> is counted over.</summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    private readonly Func<DirectorLookup> _readMachine;
    private readonly Func<bool> _startDirector;
    private readonly DirectorStopLedger _ledger;
    private readonly Func<DateTime> _utcNow;
    private readonly List<DateTime> _restarts = new();

    private SupervisedDirector? _watched;
    private SupervisedDirector? _pendingCrash;
    private bool _gaveUp;

    public DirectorCrashWatch(DirectorSupervisor supervisor)
        : this(() => supervisor.ReadTheMachine(), () => supervisor.Start(out _), supervisor.StopLedger, () => DateTime.UtcNow) { }

    internal DirectorCrashWatch(Func<DirectorLookup> readMachine, Func<bool> startDirector, DirectorStopLedger ledger,
        Func<DateTime> utcNow)
    {
        _readMachine = readMachine ?? throw new ArgumentNullException(nameof(readMachine));
        _startDirector = startDirector ?? throw new ArgumentNullException(nameof(startDirector));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        RunUnderSwapLock = work => BinarySwapLock.RunExclusivelyAsync(
            work: () => Task.FromResult(work()),
            whenBusy: _ => DirectorCrashWatchOutcome.RestartHeldOff,
            who: "director-crash-watch",
            name: SwapLockName);
    }

    /// <summary>Whether the watched Director's process has left. A seam only so a test can say "it is gone".</summary>
    internal Func<SupervisedDirector, bool> IsProcessGone { get; set; } = ProcessIsGone;

    /// <summary>The machine-wide swap lock. <see cref="BinarySwapLock.Name"/> in production; a test uses its own.</summary>
    internal string SwapLockName { get; init; } = BinarySwapLock.Name;

    /// <summary>
    /// Runs the start under the swap lock, or answers <see cref="DirectorCrashWatchOutcome.RestartHeldOff"/> when the
    /// lock is taken. Production uses <see cref="BinarySwapLock"/> under <see cref="SwapLockName"/>. A SEAM ONLY SO A
    /// TEST DECIDES WHEN THE LOCK IS HELD: the real lock hands back its answer a moment before its own thread lets go
    /// of the mutex, so a test that looks twice in a row could find its own previous hold - an outcome decided by the
    /// machine's load, not by the code. Fifteen seconds between looks make that moment irrelevant in production.
    /// </summary>
    internal Func<Func<DirectorCrashWatchOutcome>, Task<DirectorCrashWatchOutcome>> RunUnderSwapLock { get; set; }

    /// <summary>The Director being watched, or null.</summary>
    public SupervisedDirector? Watched => _watched;

    /// <summary>
    /// The path of a Director's live crash journal - the same place the locator reads its session roster from.
    /// </summary>
    public static string CrashJournalPath(SupervisedDirector director)
        => Path.Combine(director.InstanceHome, "config", "director", "crash-journal", $"{director.DirectorId}.json");

    /// <summary>Look at the machine once, and act if the watched Director crashed.</summary>
    public async Task<DirectorCrashWatchOutcome> CheckOnceAsync(CancellationToken ct = default)
    {
        var lookup = _readMachine();

        if (lookup.Outcome == DirectorResolution.Running && lookup.Director is { } running)
            return Watch(running);

        if (lookup.Outcome != DirectorResolution.NotRunning)
            return DirectorCrashWatchOutcome.Undecided;

        if (_pendingCrash is { } pending)
            return await RestartAfterCrashAsync(pending, ct);

        if (_watched is not { } gone)
            return DirectorCrashWatchOutcome.NothingToWatch;

        if (!IsProcessGone(gone))
            return DirectorCrashWatchOutcome.StillExiting;

        _watched = null;

        if (_ledger.Consume(gone.DirectorId, gone.Pid))
        {
            FileLog.Write($"[DirectorCrashWatch] {gone.DirectorId} (pid {gone.Pid}) was stopped by this launcher; not "
                          + "starting it again.");
            return DirectorCrashWatchOutcome.StoppedByLauncher;
        }

        // Read AFTER the process is confirmed gone: the clean shutdown deletes the journal before the process exits,
        // so a journal still here now was never going to be deleted.
        var journal = CrashJournalPath(gone);
        if (!File.Exists(journal))
        {
            FileLog.Write($"[DirectorCrashWatch] {gone.DirectorId} (pid {gone.Pid}) ended on purpose - its crash "
                          + "journal was deleted by its own clean shutdown. Not starting it again.");
            return DirectorCrashWatchOutcome.EndedOnPurpose;
        }

        FileLog.Write($"[DirectorCrashWatch] {gone.DirectorId} (pid {gone.Pid}) CRASHED: its process is gone and its "
                      + $"crash journal is still at {journal}, which a clean shutdown deletes.");
        return await RestartAfterCrashAsync(gone, ct);
    }

    /// <summary>Look every <see cref="PollInterval"/> until <paramref name="ct"/> is cancelled. Never throws: a
    /// failed look is logged and the next one runs, so one bad reading cannot end the watch.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        FileLog.Write($"[DirectorCrashWatch] RunAsync: watching the installed Director every {PollInterval.TotalSeconds:F0}s "
                      + $"(at most {MaxRestarts} restarts in {RestartWindow.TotalMinutes:F0} minutes)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCrashWatch] CheckOnceAsync FAILED: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
        FileLog.Write("[DirectorCrashWatch] RunAsync: stopped");
    }

    private DirectorCrashWatchOutcome Watch(SupervisedDirector running)
    {
        // A Director running again - restarted by this watch, started by a person, or handed over to - ends a
        // pending crash and lifts a give-up: whatever was wrong, a Director now holds the instance.
        _pendingCrash = null;
        if (_gaveUp)
        {
            FileLog.Write($"[DirectorCrashWatch] a Director is running again ({running.DirectorId}, pid {running.Pid}); "
                          + "crash restarts are back on.");
            _gaveUp = false;
            _restarts.Clear();
        }

        if (!running.IsInstalledImage)
        {
            // A development build holding the instance is not the installed Director, and starting the installed
            // one when it ends would be starting something nobody was running.
            if (_watched is not null)
                FileLog.Write($"[DirectorCrashWatch] {running.DirectorId} (pid {running.Pid}) is not the installed "
                              + "Director's image; it is not watched.");
            _watched = null;
            return DirectorCrashWatchOutcome.NothingToWatch;
        }

        if (_watched is null || _watched.DirectorId != running.DirectorId || _watched.Pid != running.Pid)
            FileLog.Write($"[DirectorCrashWatch] watching {running.DirectorId} (pid {running.Pid}, version {running.Version})");
        _watched = running;
        return DirectorCrashWatchOutcome.Watching;
    }

    private async Task<DirectorCrashWatchOutcome> RestartAfterCrashAsync(SupervisedDirector crashed, CancellationToken ct)
    {
        var now = _utcNow();
        _restarts.RemoveAll(t => t < now - RestartWindow);
        if (_restarts.Count >= MaxRestarts)
        {
            _pendingCrash = null;
            if (!_gaveUp)
                FileLog.Write($"[DirectorCrashWatch] GIVING UP: the Director has crashed again after {_restarts.Count} "
                              + $"restarts in {RestartWindow.TotalMinutes:F0} minutes. Nothing more is started, so a broken "
                              + "build cannot spin this machine. Start the Director by hand once the cause is fixed; the "
                              + "watch resumes when it sees one running.");
            _gaveUp = true;
            return DirectorCrashWatchOutcome.GaveUp;
        }

        // Kept pending until the start has run, so a start that throws is tried again on the next look - and counts.
        _pendingCrash = crashed;

        // Under the swap lock, and without waiting for it: an update stopping and swapping the Director holds it,
        // and starting the old build in the middle of that swap is the one start that must never happen.
        var outcome = await RunUnderSwapLock(() =>
        {
            // Counted before the start, so a start that throws also counts toward the limit.
            _restarts.Add(now);
            return _startDirector()
                ? DirectorCrashWatchOutcome.Restarted
                : DirectorCrashWatchOutcome.AlreadyRunningAgain;
        });

        _pendingCrash = outcome == DirectorCrashWatchOutcome.RestartHeldOff ? crashed : null;
        FileLog.Write(outcome switch
        {
            DirectorCrashWatchOutcome.Restarted =>
                $"[DirectorCrashWatch] started the Director again after {crashed.DirectorId} crashed (restart "
                + $"{_restarts.Count} of {MaxRestarts} in {RestartWindow.TotalMinutes:F0} minutes)",
            DirectorCrashWatchOutcome.RestartHeldOff =>
                "[DirectorCrashWatch] a binary swap holds the swap lock; the restart after the crash waits for the next look",
            _ => "[DirectorCrashWatch] a Director already holds the instance again; nothing was started",
        });
        return outcome;
    }

    /// <summary>Whether the watched process has left. A live process with the same id that started at another time
    /// is a different process that reused the id, and the Director it was is gone.</summary>
    private static bool ProcessIsGone(SupervisedDirector director)
    {
        try
        {
            using var process = Process.GetProcessById(director.Pid);
            if (process.HasExited) return true;
            var startedAt = process.StartTime.ToUniversalTime();
            return Math.Abs((startedAt - director.ProcessStartedAtUtc).TotalSeconds) > DirectorInstanceLocator.RegistrationSkew.TotalSeconds;
        }
        catch (ArgumentException)
        {
            return true;   // no such process id
        }
    }
}
