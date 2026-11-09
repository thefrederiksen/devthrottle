using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// WHICH COMMANDS HAVE TO RUN ON THE DIRECTOR'S ONE DELIVERY LOOP, AND WHY THE REST MUST NOT (issue #4490).
///
/// The SignalR client calls the "Command" handler on a single delivery loop and moves to the next command
/// only when the handler hands back its task - at its first real wait. Everything a verb does before that
/// holds the loop, and no other command is received meanwhile. On 6 November 2026 a create that waited
/// synchronously on cc-worktrees for 430 seconds left the log with no "Command received" line from 08:06:49
/// to 08:14:04, and then about forty commands arrived in one millisecond. A verb that never hands its task
/// back at all is the 25 October failure on devthrottle-mac-mini: the Director took its last command at
/// 22:02 and none for the next fourteen hours, while its reports went up the whole time and every screen
/// called the machine healthy.
///
/// THE THREE KINDS THAT STAY. A verb stays on the loop when the loop is what gives it its meaning:
///
///   - THE KEYSTROKE VERBS, because arrival order IS the person's intent. Typing a line and then pressing
///     Escape must reach the terminal in that order; on the thread pool two of them could overtake each
///     other. This is the ordering the handler's own comment protects, and it is why "everything on the
///     thread pool" is not the fix.
///   - THE STREAM VERBS, which are connection-bound: they start and stop streams on THIS connection and
///     touch its per-stream cancellation registry. They are registry operations and return at once.
///   - THE DESKTOP VERBS, connection-bound for the same reason - the stream a desktop open starts rides
///     this connection.
///
/// EVERYTHING ELSE GOES OFF THE LOOP, including create, which is the verb that held it for 430 seconds, and
/// including the chunked image upload - its chunks carry a zero-based <c>Seq</c>, the Director refuses an
/// out-of-order chunk, and the Gateway awaits each chunk's reply before sending the next, so their order is
/// kept by the protocol rather than by the loop.
///
/// AND WHAT THAT COSTS, SAID PLAINLY, because the first version of this comment got it wrong. Many of the
/// moved verbs are fully synchronous - patch, hold, resize, the display and role writes, the settings pair,
/// the repository writes, the screenshot and upload verbs, fs-list, path-status, facts - so the loop
/// serialised them COMPLETELY, and they are newly concurrent with each other. Each one is written to be
/// callable concurrently, as the REST surface it came from was, and the two shared writers were checked by
/// hand: CcDirectorConfigService.MergePatch holds a process-wide write lock, so two settings-put calls
/// cannot clobber one another; Session.PromptQueue does NOT, which is why the queue verbs stay on the loop
/// until it does.
/// </summary>
internal static class DeliveryLoopOrder
{
    /// <summary>
    /// The ordered verbs, asked of the areas that own them rather than listed here (issue #4490). A list in
    /// this file was wrong twice inside one day - it missed clear-context, compact-context and
    /// history-picker, then "enter" and execute-action - and both times it was corrected by somebody reading
    /// the write area's source, which is not a mechanism. See
    /// <see cref="ISessionCommandArea.VerbsThatMustArriveInOrder"/>.
    /// </summary>
    private static readonly HashSet<string> InOrder =
        new(SessionCommandExecutor.VerbsThatMustArriveInOrder, StringComparer.Ordinal);

    /// <summary>The ordered verbs, for the tests that pin them.</summary>
    internal static IReadOnlyCollection<string> OrderedVerbs => InOrder;

    /// <summary>True when <paramref name="verb"/> must run on the delivery loop itself. See the class comment.</summary>
    internal static bool MustStayOnTheLoop(string? verb)
    {
        if (string.IsNullOrEmpty(verb)) return true;   // a malformed command is refused in a microsecond
        return InOrder.Contains(verb)
               || DirectorUpStreamHandler.IsStreamVerb(verb)
               || Boxes.DirectorDesktopRelayHandler.IsDesktopVerb(verb);
    }
}

/// <summary>
/// What is holding the delivery loop, and for how long - the only instrument that can see it.
///
/// WHY THE TIMING WE ALREADY HAVE CANNOT. <see cref="CommandAnswerTiming"/> measures a command's answer,
/// and it starts measuring INSIDE the call: <c>var answering = dispatch();</c>. A verb that holds the loop
/// holds that line too, so the notice that exists to say "this answer is running long" is held by the very
/// thing it would report. The 430-second hold of 6 November produced no slow-answer line at all, and the
/// fourteen-hour silence of 25 October produced nothing but an absence of lines - which no alarm can read.
///
/// So the hold is measured from OUTSIDE the call, by the handler's wrapper, and the periodic re-push tick -
/// which runs on the background scheduler, not on the loop - asks this once a cycle whether the loop is
/// held RIGHT NOW. That is the question that was unanswerable: a loop that never comes back says nothing
/// about itself, and only another thread can notice.
/// </summary>
internal sealed class DeliveryLoopWatch
{
    /// <summary>
    /// How long a verb may hold the loop before it is written down. Five seconds is far longer than any
    /// verb's inline part should ever be - the dispatcher hands back at its first await - and well inside
    /// the Gateway's own 30-second wait for a command, so a hold is reported while the Gateway is still
    /// waiting rather than after it has given up.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock;
    private readonly Action<string> _write;
    private readonly object _gate = new();

    private string? _verb;
    private string? _commandId;
    private long _enteredAt;
    private bool _saidItWasHeld;
    private int _timesSaid;
    private int _timesAsked;

    internal DeliveryLoopWatch(TimeProvider? clock = null, Action<string>? write = null)
    {
        _clock = clock ?? TimeProvider.System;
        _write = write ?? (line => FileLog.Write(line));
    }

    /// <summary>
    /// Mark the loop as being inside <paramref name="verb"/> until the returned hold is disposed. One
    /// command is inside at a time, because there is one loop; a nested call would mean that is no longer
    /// true, and the later one wins rather than throwing - an instrument may not break delivery.
    /// </summary>
    internal Hold Enter(string? verb, string? commandId)
    {
        lock (_gate)
        {
            _verb = string.IsNullOrEmpty(verb) ? "(no verb)" : verb;
            _commandId = commandId ?? "";
            _enteredAt = _clock.GetTimestamp();
            _saidItWasHeld = false;
        }
        return new Hold(this);
    }

    /// <summary>
    /// How many holds this watch has reported, and how many times it was asked. Read-only, and an
    /// instrument's own counts. They are two different facts and the second is the one that was missing
    /// before 8 November 2026: nothing asked. A test proves the periodic tick asks (<see cref="TimesItWasAsked"/>)
    /// without any clock in it, and the fake-clock tests prove what the answer is past the budget.
    /// </summary>
    internal int TimesItSaidTheLoopWasHeld { get { lock (_gate) return _timesSaid; } }

    /// <summary>How many times something asked whether the loop was held. See <see cref="TimesItSaidTheLoopWasHeld"/>.</summary>
    internal int TimesItWasAsked { get { lock (_gate) return _timesAsked; } }

    /// <summary>The verb inside the loop and how long it has been there, or null when the loop is free.</summary>
    internal (string Verb, string CommandId, TimeSpan Held)? Inside()
    {
        lock (_gate)
        {
            if (_verb is null) return null;
            return (_verb, _commandId ?? "", _clock.GetElapsedTime(_enteredAt));
        }
    }

    /// <summary>
    /// Say so, ONCE per hold, if the loop has been held past the budget. Called from the periodic tick, on
    /// another thread - the loop cannot report on itself.
    /// </summary>
    internal void SayIfHeld()
    {
        lock (_gate)
        {
            _timesAsked++;
            if (_verb is null || _saidItWasHeld) return;
            var held = _clock.GetElapsedTime(_enteredAt);
            if (held < Budget) return;

            _saidItWasHeld = true;
            _timesSaid++;
            _write($"[GatewayStreamClient] DELIVERY LOOP HELD by verb={_verb}, cmdId={_commandId} for "
                   + $"{held.TotalSeconds:F1}s - no other Gateway command can be received while it runs, and this "
                   + "Director's reports keep going up regardless (issue #4490).");
        }
    }

    private void Left()
    {
        lock (_gate)
        {
            if (_verb is null) return;
            var held = _clock.GetElapsedTime(_enteredAt);
            var verb = _verb;
            var commandId = _commandId ?? "";
            _verb = null;
            _commandId = null;

            // A hold already reported by the tick is reported again on the way out, because the first line
            // says only that it started: the one that matters to whoever reads the log afterwards is how
            // long it lasted in the end.
            if (held >= Budget)
                _write($"[GatewayStreamClient] delivery loop released by verb={verb}, cmdId={commandId} after "
                       + $"{held.TotalSeconds:F1}s (issue #4490).");
        }
    }

    /// <summary>The loop's time inside one command. Disposed by the handler's wrapper.</summary>
    internal readonly struct Hold(DeliveryLoopWatch watch) : IDisposable
    {
        public void Dispose() => watch.Left();
    }
}
