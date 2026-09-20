using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.Restart;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// THE SMART SHUTDOWN ENGINE (mission document "Smart Director Restart", phase 1).
///
/// IT IS THE DRAIN, WITH A TIME LIMIT. Everything that was learned the hard way - the capture first, the
/// chain, leaf first closing, the poll, the floor under a half-written file, the re-read at close, the
/// secret sweep, the owner questions - lives in <see cref="DirectorDrain"/> and is used from there, not
/// copied. What this class adds is what a screen needs around it: an answer to "can this be done at
/// all", a run that returns at once and reports as it goes, the two buttons, and the restart.
///
/// THE RESTORE AND THE LAUNCHER ARE NOT COPIED EITHER. "Cancel and keep working" brings closed sessions
/// back through the Director's existing restore, handed in as <see cref="BringBackClosedSessions"/>; the
/// restart purpose asks the launcher through <see cref="DirectorLauncherRestartStep"/>, the same step the
/// restart cycle takes, with the same re-check. There is no second way of doing either.
///
/// THE DRAIN COMES THROUGH A FACTORY for the reason it does in the restart cycle: a drain is built per
/// run, and whether one can be built at all - null means this Director has no Gateway client - is half
/// of the availability answer.
/// </summary>
public sealed class DirectorSmartShutdown : ISmartShutdown
{
    private const string NoGateway =
        "this Director is not connected to a Gateway. A smart shutdown keeps its record on the Gateway so " +
        "that the sessions can be brought back after this machine has been down, and without that record " +
        "every session would be closed with nothing anywhere saying what it was. Connect this Director to " +
        "a Gateway and try again, or choose to shut down and ignore all sessions.";

    // ONE RUN AT A TIME, per process, like the drain beneath it and for the same reason: there is one
    // Director per process, and the race this stops is two callers inside it.
    private static readonly object Gate = new();
    private static SmartShutdownRun? _active;

    private readonly Func<DirectorDrain?> _createDrain;
    private readonly Func<CancellationToken, Task> _reachGateway;
    private readonly Func<DirectorRestartEligibilityDto> _eligibility;
    private readonly string _directorName;
    private readonly string? _directory;
    private readonly Func<DateTime> _utcNow;
    private readonly BringBackClosedSessions? _bringBack;
    private readonly IDrainSessionControl? _sessions;
    private readonly Func<IRestartCycleGateway?>? _launcherGateway;
    private readonly string _machine;
    private readonly string? _exePath;

    /// <summary>Create the engine.</summary>
    /// <param name="createDrain">Builds a drain of this Director, or returns null when this Director has
    /// no Gateway client. Must be cheap and must change nothing: it is called to answer
    /// <see cref="CheckAsync"/> as well as to run.</param>
    /// <param name="reachGateway">Asks the Gateway something harmless and THROWS when it cannot be
    /// reached. The record must live off this machine, so a Gateway that does not answer refuses the
    /// smart shutdown before anything is touched.</param>
    /// <param name="eligibility">The Director's existing answer to "would my launcher restart ME".</param>
    /// <param name="directorName">This Director's display name, which names the record.</param>
    /// <param name="directory">Where the handover documents go. Null uses the drain's standard location
    /// under the data root; a test passes its own.</param>
    /// <param name="utcNow">Test seam for the clock. It must be the same clock the drain was given.</param>
    /// <param name="bringBack">How "Cancel and keep working" brings back the sessions already closed:
    /// the Director's existing restore, against this same Director. Null means cancel is never offered -
    /// a run that could not bring a session back must not say it can be cancelled.</param>
    /// <param name="sessions">This Director's live sessions, for "Shut down and ignore all sessions",
    /// which must end them even when there is no Gateway and so no drain to borrow a seam from.</param>
    /// <param name="launcherGateway">The seam the restart purpose asks the launcher through, read at the
    /// moment of the ask so it is the host's CURRENT Gateway client. Null when this engine was given no
    /// way to restart; a restart is then refused at the start.</param>
    /// <param name="machine">This machine, as the restart route names it.</param>
    /// <param name="exePath">This process's executable, sent to the restart route as evidence.</param>
    public DirectorSmartShutdown(
        Func<DirectorDrain?> createDrain,
        Func<CancellationToken, Task> reachGateway,
        Func<DirectorRestartEligibilityDto> eligibility,
        string directorName,
        string? directory = null,
        Func<DateTime>? utcNow = null,
        BringBackClosedSessions? bringBack = null,
        IDrainSessionControl? sessions = null,
        Func<IRestartCycleGateway?>? launcherGateway = null,
        string? machine = null,
        string? exePath = null)
    {
        _createDrain = createDrain ?? throw new ArgumentNullException(nameof(createDrain));
        _reachGateway = reachGateway ?? throw new ArgumentNullException(nameof(reachGateway));
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _directorName = string.IsNullOrWhiteSpace(directorName) ? "this Director" : directorName;
        _directory = directory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _bringBack = bringBack;
        _sessions = sessions;
        _launcherGateway = launcherGateway;
        _machine = string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine;
        _exePath = exePath;
    }

    /// <summary>The run under way on this Director, or null. For a screen deciding what to offer.</summary>
    public static ISmartShutdownRun? Active
    {
        get { lock (Gate) return _active is { Completion.IsCompleted: false } ? _active : null; }
    }

    /// <inheritdoc />
    public async Task<SmartShutdownAvailability> CheckAsync(SmartShutdownPurpose purpose, CancellationToken ct)
    {
        FileLog.Write($"[DirectorSmartShutdown] CheckAsync: purpose={purpose}");

        string? refusal = null;
        if (_createDrain() is null)
        {
            refusal = NoGateway;
        }
        else
        {
            try
            {
                await _reachGateway(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                FileLog.Write($"[DirectorSmartShutdown] CheckAsync: the Gateway could not be reached: {ex.Message}");
                refusal =
                    $"the Gateway could not be reached ({ex.Message}). A smart shutdown keeps its record on " +
                    "the Gateway so that the sessions can be brought back after this machine has been down, " +
                    "so it does not start without one. Nothing has been touched. Try again when the Gateway " +
                    "answers, or choose to shut down and ignore all sessions.";
            }
        }

        var restartRefusal = RestartRefusal();
        var answer = new SmartShutdownAvailability(
            CanSmartShutdown: refusal is null,
            SmartShutdownRefusal: refusal,
            CanRestart: restartRefusal is null,
            RestartRefusal: restartRefusal);

        FileLog.Write(
            $"[DirectorSmartShutdown] CheckAsync: canSmartShutdown={answer.CanSmartShutdown}, canRestart={answer.CanRestart}");
        return answer;
    }

    /// <summary>
    /// Why this Director's launcher would not restart it, or null when it would. ONE PLACE, read by
    /// <see cref="CheckAsync"/> and by <see cref="Start"/>, so the dialog and the start cannot disagree.
    /// == against the one answer that permits: "could not tell" refuses exactly as "no" does.
    /// </summary>
    private string? RestartRefusal()
    {
        var restart = _eligibility();
        return restart.Eligible == true
            ? null
            : restart.Reason ?? "this Director could not tell whether its launcher would restart it.";
    }

    /// <inheritdoc />
    public ISmartShutdownRun Start(SmartShutdownRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write(
            $"[DirectorSmartShutdown] Start: purpose={request.Purpose}, minutes={request.TimeAllowed.TotalMinutes:0}");

        if (request.Purpose == SmartShutdownPurpose.Restart)
        {
            // REFUSED BEFORE ANYTHING IS TOUCHED. Emptying a Director its launcher would not restart
            // closes every session for a restart that then never comes.
            var refusal = RestartRefusal()
                ?? (_launcherGateway is null
                    ? "this smart shutdown was given no way to ask the launcher for a restart."
                    : null);
            if (refusal is not null)
            {
                var message =
                    $"This Director cannot be restarted through its launcher: {refusal} " +
                    "Nothing has been touched. Choose to close it instead, or restart it by hand.";
                FileLog.Write($"[DirectorSmartShutdown] Start FAILED: {message}");
                throw new InvalidOperationException(message);
            }
        }

        SmartShutdownRun run;
        lock (Gate)
        {
            if (_active is { Completion.IsCompleted: false } active)
            {
                var message =
                    "A smart shutdown is already under way on this Director - it started at " +
                    $"{active.Current.StartedUtc:yyyy-MM-dd HH:mm:ss}Z. A second one would be two engines " +
                    "closing the same sessions, so it is refused and nothing has been touched.";
                FileLog.Write($"[DirectorSmartShutdown] Start FAILED: {message}");
                throw new InvalidOperationException(message);
            }

            if (DirectorDrain.Running is { } drain)
            {
                var message =
                    "A drain of this Director is already running - it started at " +
                    $"{drain.StartedUtc:yyyy-MM-dd HH:mm:ss}Z and is writing workspace '{drain.WorkspaceId}'. " +
                    "A smart shutdown beside it would be two engines closing the same sessions, so it is " +
                    "refused and nothing has been touched.";
                FileLog.Write($"[DirectorSmartShutdown] Start FAILED: {message}");
                throw new InvalidOperationException(message);
            }

            run = new SmartShutdownRun(this, request, _createDrain, _directorName, _directory, _utcNow,
                _bringBack, _launcherGateway, _machine, _exePath);
            _active = run;
        }

        run.Begin();
        FileLog.Write("[DirectorSmartShutdown] Start: the run has begun");
        return run;
    }

    /// <inheritdoc />
    public async Task<IgnoreAllResult> ShutDownIgnoringAllAsync(SmartShutdownRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write($"[DirectorSmartShutdown] ShutDownIgnoringAllAsync: purpose={request.Purpose}");

        var sessions = _sessions ?? throw new InvalidOperationException(
            "This smart shutdown was given no way to end this Director's sessions, so it cannot shut down " +
            "and ignore them. Nothing has been touched.");

        lock (Gate)
        {
            if (_active is { Completion.IsCompleted: false } || DirectorDrain.Running is not null)
            {
                const string busy =
                    "A smart shutdown or a drain is already under way on this Director, and it is closing " +
                    "these same sessions. A smart shutdown offers \"Shut down now\"; a drain has no such button " +
                    "and has to finish first. Nothing has been touched by this request.";
                FileLog.Write($"[DirectorSmartShutdown] ShutDownIgnoringAllAsync FAILED: {busy}");
                throw new InvalidOperationException(busy);
            }
        }

        // THE RECORD FIRST. And when it cannot be written, THE SESSIONS ARE STILL ENDED. That is not a
        // fallback: it is the owner's stated choice (mission document section 7). He chose to discard
        // these sessions; a Gateway that is down takes away the history entry and not his decision. What
        // he is told is exactly that - RecordWritten is false and RecordRefusal says why.
        var (drain, workspaceId, refusal, recordedIds) = await WriteRecordAsync(
            request.Reason, "shut down ignoring all", (d, o, c) => d.RecordIgnoreAllAsync(o, c), ct).ConfigureAwait(false);

        var recorded = new HashSet<string>(recordedIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var ended = 0;
        var inNoRecord = new List<string>();
        var wouldNotEnd = new List<string>();

        async Task EndEachAsync(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                var end = await sessions.EndAsync(id, "Shut down and ignore all sessions.").ConfigureAwait(false);
                if (end.Ended)
                {
                    ended++;
                    // Only a record that was written can be missing a session. When none was written,
                    // RecordRefusal already says that nothing names any of them.
                    if (refusal is null && !recorded.Contains(id)) inNoRecord.Add(id);
                }
                else if (!end.SessionGone)
                {
                    wouldNotEnd.Add($"{id} ({end.Reason ?? "no reason given"})");
                    FileLog.Write($"[DirectorSmartShutdown] ShutDownIgnoringAllAsync: session {id} could not be ended: {end.Reason}");
                }
            }
        }

        var first = sessions.LiveSessionIds();
        await EndEachAsync(first).ConfigureAwait(false);

        // ONE MORE PASS, AND ONLY ONE. Nothing interrupted the sessions before they were ended, so one of
        // them may have started another while the record was being written or the others were being
        // ended. That session is in no record and was not in the list above; the application is about to
        // close on top of it, which would end it and say nothing. It is ended with the rest and NAMED,
        // as the smart path does for the same sessions. Not a loop: a Director that keeps producing
        // sessions is not something a second or a tenth pass settles.
        var asked = new HashSet<string>(first, StringComparer.OrdinalIgnoreCase);
        await EndEachAsync(sessions.LiveSessionIds().Where(id => !asked.Contains(id)).ToList()).ConfigureAwait(false);

        var said = new List<string>();
        if (inNoRecord.Count > 0)
            said.Add(
                $"{inNoRecord.Count} session(s) were ended that are NOT in the record, because they appeared " +
                $"after it was written: {string.Join(", ", inNoRecord)}. Nothing describes them and they cannot be offered back.");
        if (wouldNotEnd.Count > 0)
            said.Add($"{wouldNotEnd.Count} session(s) could not be ended and are still on this Director: {string.Join(", ", wouldNotEnd)}.");

        if (drain is not null && refusal is null)
        {
            // The close times. The record that matters - what was running - is already on the Gateway; a
            // failure here is said in the log and does not turn a written record into an unwritten one.
            try
            {
                await drain.SaveSessionsEndedAsync(said, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                FileLog.Write($"[DirectorSmartShutdown] ShutDownIgnoringAllAsync: the close times could not be saved: {ex.Message}");
            }
        }

        var result = new IgnoreAllResult(refusal is null, refusal is null ? workspaceId : null, refusal, ended,
            said.Count == 0 ? null : string.Join(" ", said));
        FileLog.Write(
            $"[DirectorSmartShutdown] ShutDownIgnoringAllAsync: recordWritten={result.RecordWritten}, ended={ended}, workspace={result.WorkspaceId ?? "-"}, detail={result.Detail ?? "-"}");
        return result;
    }

    /// <inheritdoc />
    public async Task<IgnoreAllResult> RecordAndLetEndAsync(CancellationToken ct)
    {
        FileLog.Write("[DirectorSmartShutdown] RecordAndLetEndAsync");

        // A smart shutdown already under way has ALREADY written this record: it names every session with
        // its repository and its conversation id from before anything was asked. Writing a second one
        // beside it would be two records of one moment.
        ISmartShutdownRun? active;
        lock (Gate) active = _active is { Completion.IsCompleted: false } ? _active : null;
        if (active is not null)
        {
            var existing = active.Current.WorkspaceId;
            var answer = existing is not null
                ? new IgnoreAllResult(true, existing, null, 0)
                : new IgnoreAllResult(false, null,
                    "a smart shutdown had just started on this Director and had not written its record yet.", 0);
            FileLog.Write(
                $"[DirectorSmartShutdown] RecordAndLetEndAsync: a run is under way, recordWritten={answer.RecordWritten}, workspace={existing ?? "-"}");
            return answer;
        }

        var (_, workspaceId, refusal, _) = await WriteRecordAsync(
            "The operating system was shutting down.", "operating system shutdown",
            (d, o, c) => d.RecordOperatingSystemShutdownAsync(o, c), ct).ConfigureAwait(false);

        // IT ENDS NOTHING. The operating system is doing that.
        var result = new IgnoreAllResult(refusal is null, refusal is null ? workspaceId : null, refusal, 0);
        FileLog.Write($"[DirectorSmartShutdown] RecordAndLetEndAsync: recordWritten={result.RecordWritten}, workspace={result.WorkspaceId ?? "-"}");
        return result;
    }

    /// <summary>
    /// Write a record with no handovers in it, and say in plain words why when it could not be written.
    /// This is the entry point both record-only calls share, so it is where a Gateway that does not
    /// answer is caught and turned into the sentence the caller hands back.
    /// </summary>
    private async Task<(DirectorDrain? Drain, string? WorkspaceId, string? Refusal, IReadOnlyList<string>? SessionIds)> WriteRecordAsync(
        string? reason, string what,
        Func<DirectorDrain, DrainOptions, CancellationToken, Task<DrainRecordOnly>> write,
        CancellationToken ct)
    {
        var drain = _createDrain();
        if (drain is null)
            return (null, null,
                "this Director is not connected to a Gateway, so there is nowhere off this machine to keep " +
                "the record of what was closed.", null);

        var startedLocal = _utcNow().ToLocalTime();
        var options = new DrainOptions
        {
            WorkspaceId = DrainPaths.WorkspaceIdFor(_directorName, startedLocal),
            WorkspaceName = $"{_directorName} {what} {startedLocal:yyyy-MM-dd HH:mm}",
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            DrivenByNote = "Written by the Director itself; no session drove it.",
        };

        try
        {
            var record = await write(drain, options, ct).ConfigureAwait(false);
            var id = string.IsNullOrWhiteSpace(record.Document.Id) ? options.WorkspaceId : record.Document.Id;
            return (drain, id, null, record.SessionIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[DirectorSmartShutdown] the record ({what}) could not be written: {ex.Message}");
            return (drain, null, $"the record of what was closed could not be written to the Gateway: {ex.Message}", null);
        }
    }
}

/// <summary>
/// One smart shutdown under way. It owns no rule about sessions - every one of those is the drain's. It
/// owns the thread the drain runs on, the latest snapshot, the event, the two buttons, and the result.
/// </summary>
internal sealed class SmartShutdownRun : ISmartShutdownRun
{
    private enum Button { None, ShutDownNow, Cancel }

    private readonly DirectorSmartShutdown _engine;
    private readonly SmartShutdownRequest _request;
    private readonly Func<DirectorDrain?> _createDrain;
    private readonly string _directorName;
    private readonly string? _directory;
    private readonly Func<DateTime> _utcNow;
    private readonly BringBackClosedSessions? _bringBack;
    private readonly Func<IRestartCycleGateway?>? _launcherGateway;
    private readonly string _machine;
    private readonly string? _exePath;
    private readonly CancellationTokenSource _shutDownNow = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<SmartShutdownResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ONE BUTTON, ONCE. The two are rivals: whichever is chosen first is the one the run does, and the
    // other is ignored from then on. The lock guards these two fields and NOTHING ELSE - no handler is
    // ever called and no token is ever cancelled while it is held.
    private readonly object _buttons = new();
    private Button _pressed = Button.None;
    private bool _sessionsAreDone;

    private volatile SmartShutdownSnapshot _current;

    internal SmartShutdownRun(
        DirectorSmartShutdown engine,
        SmartShutdownRequest request,
        Func<DirectorDrain?> createDrain,
        string directorName,
        string? directory,
        Func<DateTime> utcNow,
        BringBackClosedSessions? bringBack,
        Func<IRestartCycleGateway?>? launcherGateway,
        string machine,
        string? exePath)
    {
        _engine = engine;
        _request = request;
        _createDrain = createDrain;
        _directorName = directorName;
        _directory = directory;
        _utcNow = utcNow;
        _bringBack = bringBack;
        _launcherGateway = launcherGateway;
        _machine = machine;
        _exePath = exePath;

        var started = utcNow();
        _current = new SmartShutdownSnapshot(
            SmartShutdownPhase.Starting,
            SmartShutdownWords.PhaseLabel(SmartShutdownPhase.Starting),
            Array.Empty<SmartShutdownSessionProgress>(),
            Total: 0,
            Gone: 0,
            SmartShutdownWords.CountLabel(0, 0),
            started,
            started + TimeSpan.FromTicks(request.TimeAllowed.Ticks / 3 * 2),
            started + request.TimeAllowed,
            CanShutDownNow: true,
            CanCancel: false,
            WorkspaceId: null,
            Note: null);
    }

    /// <inheritdoc />
    public SmartShutdownSnapshot Current => _current;

    /// <inheritdoc />
    public event Action<SmartShutdownSnapshot>? Changed;

    /// <inheritdoc />
    public Task<SmartShutdownResult> Completion => _completion.Task;

    /// <summary>Start the work on a thread of its own, so <see cref="ISmartShutdown.Start"/> returns
    /// before anything is asked of any session.</summary>
    internal void Begin() => _ = Task.Run(RunAsync);

    /// <inheritdoc />
    public void ShutDownNow()
    {
        try
        {
            if (!Choose(Button.ShutDownNow, _current.CanShutDownNow)) return;
            FileLog.Write("[SmartShutdownRun] ShutDownNow: jumping to the limit");
            _shutDownNow.Cancel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownRun] ShutDownNow FAILED: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void CancelAndKeepWorking()
    {
        try
        {
            if (!Choose(Button.Cancel, _current.CanCancel)) return;
            FileLog.Write("[SmartShutdownRun] CancelAndKeepWorking: the restart is off");
            _cancel.Cancel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownRun] CancelAndKeepWorking FAILED: {ex.Message}");
        }
    }

    /// <summary>Whether this press is the one the run acts on: the snapshot offers the button, no button
    /// has been chosen before, and the sessions are not already done with. Ignored and logged otherwise.</summary>
    private bool Choose(Button button, bool offered)
    {
        string? ignored = null;
        lock (_buttons)
        {
            if (!offered) ignored = $"the run is in phase {_current.Phase} and does not offer it";
            else if (_sessionsAreDone) ignored = "every session is already dealt with";
            else if (_pressed != Button.None) ignored = $"{_pressed} was already chosen";
            else _pressed = button;
        }
        if (ignored is null) return true;
        FileLog.Write($"[SmartShutdownRun] {button}: ignored, {ignored}");
        return false;
    }

    // THE RUN'S OWN TOP. The one place an unexpected failure is caught, so that Completion carries a
    // result in plain words instead of faulting on a screen that is showing sessions being shut down.
    private async Task RunAsync()
    {
        string? workspaceId = null;
        try
        {
            FileLog.Write($"[SmartShutdownRun] RunAsync: purpose={_request.Purpose}, minutes={_request.TimeAllowed.TotalMinutes:0}, reason={_request.Reason}");

            // THE SAME QUESTION THE DIALOG ASKED, asked the same way, before anything is touched. A
            // Gateway that went away between the dialog and the button must refuse here too.
            var availability = await _engine.CheckAsync(_request.Purpose, CancellationToken.None).ConfigureAwait(false);
            var drain = availability.CanSmartShutdown ? _createDrain() : null;
            if (drain is null)
            {
                Finish(SmartShutdownOutcome.Refused, null,
                    availability.SmartShutdownRefusal ?? "this Director is not connected to a Gateway.");
                return;
            }

            var startedLocal = _utcNow().ToLocalTime();
            workspaceId = DrainPaths.WorkspaceIdFor(_directorName, startedLocal);
            var options = new DrainOptions
            {
                WorkspaceId = workspaceId,
                WorkspaceName = $"{_directorName} smart shutdown {startedLocal:yyyy-MM-dd HH:mm}",
                Reason = string.IsNullOrWhiteSpace(_request.Reason) ? null : _request.Reason.Trim(),
                DrivenByNote = "Run by the Director's own smart shutdown; no session drove it.",
                SmartShutdown = new SmartShutdownDrainOptions
                {
                    TimeAllowed = _request.TimeAllowed,
                    ShutDownNow = _shutDownNow.Token,
                    Cancel = _cancel.Token,
                    BringBack = _bringBack,
                    OnSnapshot = Publish,
                },
            };

            var result = await drain.RunAsync(options, _directory, CancellationToken.None).ConfigureAwait(false);
            workspaceId = string.IsNullOrWhiteSpace(result.Document.Id) ? workspaceId : result.Document.Id;

            // From here on neither button does anything, whatever the last snapshot offered.
            Button pressed;
            lock (_buttons)
            {
                _sessionsAreDone = true;
                pressed = _pressed;
            }

            if (result.Cancelled)
            {
                Finish(SmartShutdownOutcome.Cancelled, workspaceId,
                    result.CancelDetail ?? "The smart shutdown was cancelled.");
                return;
            }

            // A cancel the run said yes to and the drain could no longer act on - it arrived as the time
            // ran out, or after the last session had gone. The owner is told, not left to wonder.
            var tooLate = pressed == Button.Cancel
                ? " \"Cancel and keep working\" was chosen too late to act on: the sessions were already being shut down."
                : "";

            if (!result.Emptied)
            {
                Finish(SmartShutdownOutcome.Failed, workspaceId,
                    (result.NotEmptiedReason ?? "the Director is not empty, and nothing said why.") + tooLate);
                return;
            }

            var seats = result.Document.Seats;
            var ended = seats.Count(s => s.DrainState == WorkspaceDrainStates.EndedAtLimit);
            var handedOver = seats.Count(s => s.DrainState is WorkspaceDrainStates.Drained or WorkspaceDrainStates.Covered);
            var emptied =
                $"Every session is shut down and the Director is empty. {handedOver} handed over" +
                (ended == 0 ? "." : $"; {ended} had not, and were ended when time was up - each is noted with its saved conversation.") +
                tooLate;

            if (_request.Purpose == SmartShutdownPurpose.Restart)
            {
                await AskLauncherAsync(workspaceId, emptied).ConfigureAwait(false);
                return;
            }

            Finish(SmartShutdownOutcome.Emptied, workspaceId, emptied);
        }
        catch (DrainAlreadyRunningException ex)
        {
            // Another drain took the Director between Start's check and this run's first step. Nothing
            // was touched by this run.
            FileLog.Write($"[SmartShutdownRun] RunAsync: refused, a drain is already running: {ex.Message}");
            Finish(SmartShutdownOutcome.Refused, null, ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownRun] RunAsync FAILED: {ex}");
            Finish(SmartShutdownOutcome.Failed, workspaceId,
                $"The smart shutdown stopped on an error: {ex.Message} The record on the Gateway says how far it got.");
        }
    }

    /// <summary>
    /// THE RESTART PURPOSE. Reached ONLY after the drain has answered that nothing at all is left on this
    /// Director. The launcher is asked through the restart cycle's own launcher step - the machine is
    /// checked again first, exactly as the cycle does - and never any other way.
    ///
    /// A refusal leaves the record standing: every session is shut down and recorded, and the way up
    /// offers them whenever the Director is next started.
    /// </summary>
    private async Task AskLauncherAsync(string? workspaceId, string emptied)
    {
        Publish(_current with
        {
            Phase = SmartShutdownPhase.Restarting,
            PhaseLabel = SmartShutdownWords.PhaseLabel(SmartShutdownPhase.Restarting),
            CanShutDownNow = false,
            CanCancel = false,
            Note = "every session is shut down: asking this Director's launcher to restart it",
        });

        var stands = $" Every session is shut down and recorded in workspace '{workspaceId}'; it is offered when the Director is next started.";
        var gateway = _launcherGateway?.Invoke();
        if (gateway is null)
        {
            Finish(SmartShutdownOutcome.RestartRefused, workspaceId,
                "The launcher was not asked: this Director is no longer connected to a Gateway, and the restart is asked for through it." + stands);
            return;
        }

        // THE RUN'S OWN LAST STEP, so this is where what it throws is caught. A Gateway that dies between
        // the drain's final save and this ask is the same fact as a Gateway client that is gone, which is
        // RestartRefused a few lines up: the Director is verifiably empty and the record stands. Left to
        // the run's catch-all it would read as a shutdown that stopped on an error, and the owner would
        // not be told the record is offered on the next start. Nothing is retried and nothing is hidden:
        // the outcome carries the error's own words.
        var launcherWasAsked = false;
        LauncherRestartStepResult step;
        try
        {
            step = await DirectorLauncherRestartStep.RunAsync(gateway, _machine, _exePath,
                () => { launcherWasAsked = true; return Task.CompletedTask; }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownRun] AskLauncherAsync FAILED: launcherWasAsked={launcherWasAsked}: {ex}");
            Finish(SmartShutdownOutcome.RestartRefused, workspaceId,
                (launcherWasAsked
                    ? "The launcher was asked to restart this Director, but its answer never came back: " + ex.Message +
                      " If the launcher did receive the request, this Director is stopped and started again shortly; if it did not, restart it by hand."
                    : "The launcher was not asked, because the machine could not be checked again first: " + ex.Message) +
                stands);
            return;
        }

        switch (step.Verdict)
        {
            case LauncherRestartStepVerdict.Accepted:
                Finish(SmartShutdownOutcome.RestartAccepted, workspaceId,
                    emptied + " The launcher accepted the restart; this Director is being stopped and started again.");
                return;
            case LauncherRestartStepVerdict.CapabilityRefused:
                Finish(SmartShutdownOutcome.RestartRefused, workspaceId,
                    "The launcher was not asked, because the machine was checked again and a guarded restart must not be sent: " +
                    step.Refusal + stands);
                return;
            default:
                Finish(SmartShutdownOutcome.RestartRefused, workspaceId,
                    "The Director was emptied, but " + (step.Refusal ?? "the launcher did not accept the restart.") + stands);
                return;
        }
    }

    private void Finish(SmartShutdownOutcome outcome, string? workspaceId, string detail)
    {
        lock (_buttons) _sessionsAreDone = true;
        var final = _current with
        {
            Phase = SmartShutdownPhase.Finished,
            PhaseLabel = SmartShutdownWords.PhaseLabel(SmartShutdownPhase.Finished),
            CanShutDownNow = false,
            CanCancel = false,
            WorkspaceId = workspaceId ?? _current.WorkspaceId,
            Note = detail,
        };
        Publish(final);
        FileLog.Write($"[SmartShutdownRun] finished: outcome={outcome}, workspace={workspaceId ?? "-"}, detail={detail}");
        _completion.TrySetResult(new SmartShutdownResult(outcome, workspaceId, detail, final));
    }

    // THE EVENT RAISE. Each handler on its own, so one screen that throws stops neither the shutdown nor
    // the next handler. Never called with the button lock held.
    private void Publish(SmartShutdownSnapshot snapshot)
    {
        _current = snapshot;
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<SmartShutdownSnapshot>)handler)(snapshot); }
            catch (Exception ex) { FileLog.Write($"[SmartShutdownRun] a Changed handler threw: {ex.Message}"); }
        }
    }
}
