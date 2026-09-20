using CcDirector.ControlApi.Drain;
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
/// all", a run that returns at once and reports as it goes, and the one button built so far.
///
/// NOT BUILT YET, and each says so rather than pretending: "Cancel and keep working", "Shut down and
/// ignore all sessions", the record for an operating system shutdown, and the restart purpose.
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
    public DirectorSmartShutdown(
        Func<DirectorDrain?> createDrain,
        Func<CancellationToken, Task> reachGateway,
        Func<DirectorRestartEligibilityDto> eligibility,
        string directorName,
        string? directory = null,
        Func<DateTime>? utcNow = null)
    {
        _createDrain = createDrain ?? throw new ArgumentNullException(nameof(createDrain));
        _reachGateway = reachGateway ?? throw new ArgumentNullException(nameof(reachGateway));
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _directorName = string.IsNullOrWhiteSpace(directorName) ? "this Director" : directorName;
        _directory = directory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
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

        var restart = _eligibility();
        var canRestart = restart.Eligible == true;
        var answer = new SmartShutdownAvailability(
            CanSmartShutdown: refusal is null,
            SmartShutdownRefusal: refusal,
            CanRestart: canRestart,
            RestartRefusal: canRestart
                ? null
                : restart.Reason ?? "this Director could not tell whether its launcher would restart it.");

        FileLog.Write(
            $"[DirectorSmartShutdown] CheckAsync: canSmartShutdown={answer.CanSmartShutdown}, canRestart={answer.CanRestart}");
        return answer;
    }

    /// <inheritdoc />
    public ISmartShutdownRun Start(SmartShutdownRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write(
            $"[DirectorSmartShutdown] Start: purpose={request.Purpose}, minutes={request.TimeAllowed.TotalMinutes:0}");

        if (request.Purpose == SmartShutdownPurpose.Restart)
        {
            const string notYet =
                "A smart shutdown that restarts the Director is not built yet; only the one that closes it " +
                "is. Nothing has been touched.";
            FileLog.Write($"[DirectorSmartShutdown] Start FAILED: {notYet}");
            throw new NotSupportedException(notYet);
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

            run = new SmartShutdownRun(this, request, _createDrain, _directorName, _directory, _utcNow);
            _active = run;
        }

        run.Begin();
        FileLog.Write("[DirectorSmartShutdown] Start: the run has begun");
        return run;
    }

    /// <inheritdoc />
    public Task<IgnoreAllResult> ShutDownIgnoringAllAsync(SmartShutdownRequest request, CancellationToken ct)
    {
        const string notYet = "Shut down and ignore all sessions is not built yet. Nothing has been touched.";
        FileLog.Write($"[DirectorSmartShutdown] ShutDownIgnoringAllAsync FAILED: {notYet}");
        throw new NotSupportedException(notYet);
    }

    /// <inheritdoc />
    public Task<IgnoreAllResult> RecordAndLetEndAsync(CancellationToken ct)
    {
        const string notYet =
            "The record for an operating system shutdown is not built yet. Nothing has been written.";
        FileLog.Write($"[DirectorSmartShutdown] RecordAndLetEndAsync FAILED: {notYet}");
        throw new NotSupportedException(notYet);
    }
}

/// <summary>
/// One smart shutdown under way. It owns no rule about sessions - every one of those is the drain's. It
/// owns the thread the drain runs on, the latest snapshot, the event, and the result.
/// </summary>
internal sealed class SmartShutdownRun : ISmartShutdownRun
{
    private readonly DirectorSmartShutdown _engine;
    private readonly SmartShutdownRequest _request;
    private readonly Func<DirectorDrain?> _createDrain;
    private readonly string _directorName;
    private readonly string? _directory;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _shutDownNow = new();
    private readonly TaskCompletionSource<SmartShutdownResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile SmartShutdownSnapshot _current;
    private int _shutDownNowPressed;

    internal SmartShutdownRun(
        DirectorSmartShutdown engine,
        SmartShutdownRequest request,
        Func<DirectorDrain?> createDrain,
        string directorName,
        string? directory,
        Func<DateTime> utcNow)
    {
        _engine = engine;
        _request = request;
        _createDrain = createDrain;
        _directorName = directorName;
        _directory = directory;
        _utcNow = utcNow;

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
            if (!_current.CanShutDownNow)
            {
                FileLog.Write($"[SmartShutdownRun] ShutDownNow: ignored, the run is in phase {_current.Phase}");
                return;
            }
            if (Interlocked.Exchange(ref _shutDownNowPressed, 1) == 1)
            {
                FileLog.Write("[SmartShutdownRun] ShutDownNow: ignored, it was already chosen once");
                return;
            }

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
        => FileLog.Write(
            "[SmartShutdownRun] CancelAndKeepWorking: ignored - it is not built yet, and every snapshot " +
            "says so (CanCancel is false)");

    // THE RUN'S OWN TOP. The one place an unexpected failure is caught, so that Completion carries a
    // result in plain words instead of faulting on a screen that is showing sessions being shut down.
    private async Task RunAsync()
    {
        string? workspaceId = null;
        try
        {
            FileLog.Write($"[SmartShutdownRun] RunAsync: minutes={_request.TimeAllowed.TotalMinutes:0}, reason={_request.Reason}");

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
                    OnSnapshot = Publish,
                },
            };

            var result = await drain.RunAsync(options, _directory, CancellationToken.None).ConfigureAwait(false);
            workspaceId = string.IsNullOrWhiteSpace(result.Document.Id) ? workspaceId : result.Document.Id;

            if (!result.Emptied)
            {
                Finish(SmartShutdownOutcome.Failed, workspaceId,
                    result.NotEmptiedReason ?? "the Director is not empty, and nothing said why.");
                return;
            }

            var seats = result.Document.Seats;
            var ended = seats.Count(s => s.DrainState == WorkspaceDrainStates.EndedAtLimit);
            var handedOver = seats.Count(s => s.DrainState is WorkspaceDrainStates.Drained or WorkspaceDrainStates.Covered);
            Finish(SmartShutdownOutcome.Emptied, workspaceId,
                $"Every session is shut down and the Director is empty. {handedOver} handed over" +
                (ended == 0 ? "." : $"; {ended} had not, and were ended when time was up - each is noted with its saved conversation."));
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

    private void Finish(SmartShutdownOutcome outcome, string? workspaceId, string detail)
    {
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
    // the next handler.
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
