using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// ORDER WITHOUT HOLDING THE DELIVERY LOOP (issue #4816).
///
/// THE PROBLEM THIS SOLVES. Some verbs must reach a session in the order they were sent: typing a line and
/// then pressing Escape is one intention, and two of them on the thread pool could overtake each other. Until
/// now the only thing giving them that order was the Director's single command delivery loop - the verb ran ON
/// the loop, so nothing else was even RECEIVED until it finished. That is order bought with deafness. A paste
/// of ten kilobytes into a terminal paces itself eight bytes at a time with a fifteen-millisecond wait between
/// chunks, which is about nineteen seconds; for all of it the Director took no command of any kind from the
/// Gateway, while its reports went up and every screen called the machine healthy. Nineteen seconds is two
/// thirds of the Gateway's thirty-second wait, so a person pasting a page of text could make their own machine
/// answer "the Director did not answer" to somebody else's prompt.
///
/// WHAT THIS DOES INSTEAD. Each session gets a chain of its own. A verb that must arrive in order is appended
/// to its session's chain and the loop is released at once - the append is a dictionary lookup under a lock,
/// and nothing of the verb's own work happens before the handler hands its task back. The SignalR client calls
/// the command handler one command at a time, in arrival order, so appending synchronously inside that call IS
/// arrival order: the chain preserves exactly what the loop was preserving.
///
/// WHAT IT COSTS, SAID PLAINLY. A nineteen-second paste still delays the next prompt to the SAME session by
/// nineteen seconds, and that prompt can now expire against the Gateway's wait while it queues. Before this
/// change it was not delayed - it was not received at all, which is worse and is also invisible. What changes
/// is that the paste no longer reaches any other session, any other verb, or the connection itself.
///
/// WHY NOT THE FIX #4816 PROPOSED. The issue named a different one: pace the paste inside the session's input
/// gate (<c>Session.BeginInputAsync</c>), then let the verb leave the loop. That fixes the hold and opens a
/// hole. The gate defers a GUARDED SEND; it does not defer a keystroke. <c>Session.SendInput</c> holds its
/// bytes only while <c>_guardedSection is not null</c>, and in-flight input is not a guarded section - so a
/// <c>terminal-input</c> arriving mid-paste would have been written straight into the middle of the pasted
/// text. Order against the next keystroke is the whole reason the paste was kept on the loop, and the gate
/// does not provide it. The chain does, for every ordered verb at once, and changes nothing in the session's
/// input semantics.
///
/// WHAT IT DOES NOT COVER. The desktop's own paste (<c>TerminalControl.PasteTextAsync</c>) does not come
/// through here, because it never touched the delivery loop; a person pasting at the keyboard while the
/// Gateway types into the same session can still interleave with it, exactly as before.
/// </summary>
internal sealed class OrderedBySession
{
    /// <summary>
    /// The chain for a command that names no session. The fleet doorbell's <c>ring</c> is ordered and is not
    /// addressed to a session, so those share one chain rather than running loose - the alternative is a key
    /// per command, which is no order at all.
    /// </summary>
    private const string NoSession = "(no session)";

    /// <summary>
    /// How long one session's queue may be held before it is written down.
    ///
    /// SIX TIMES THE DELIVERY LOOP'S BUDGET, ON PURPOSE. Five seconds is far too long to hold the loop,
    /// because nothing legitimate works for five seconds before its first await. A chain is the opposite:
    /// holding it IS the work, and plenty of ordered verbs honestly take seconds - a ten-kilobyte paste is
    /// about nineteen. What is worth a line is a chain held past the Gateway's own thirty-second wait for a
    /// command, because from then on every command queued behind it is expiring unanswered, which is the
    /// symptom somebody will be looking at when they read this log.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many commands may be waiting on ONE session's queue before the next is refused instead of queued.
    ///
    /// WHY A CAP IS NEEDED AT ALL, AND WHY IT IS NEW. While a verb held the delivery loop, a command behind
    /// it could not even be DELIVERED, so nothing piled up - the deafness was also the backpressure. Now
    /// that the loop is free, a session whose verb has genuinely wedged goes on accepting commands for it
    /// for ever: each one is a small closure that never completes, the Gateway gives up on each after thirty
    /// seconds while the Director's task stays pending, and worst of all, if the wedge ever clears, every
    /// stale command runs at once - which for the keystroke verbs means typing hours-old input into somebody's
    /// terminal.
    ///
    /// FIFTY, because the number only has to separate a real burst from a pile-up. A person typing fast, a
    /// paste, and the Wingman acting at the same time are single figures; fifty is far above anything a
    /// session has ever had waiting, and far below the point where a stale queue is worth replaying. Past it
    /// the command is REFUSED with the reason, which is an answer the Gateway can show somebody, rather than
    /// a timeout, which is not.
    /// </summary>
    internal const int MostThatMayWait = 50;

    private readonly object _gate = new();
    private readonly Dictionary<string, Chain> _chains = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly Action<string> _write;

    internal OrderedBySession(TimeProvider? clock = null, Action<string>? write = null)
    {
        _clock = clock ?? TimeProvider.System;
        _write = write ?? (line => FileLog.Write(line));
    }

    private sealed class Chain
    {
        internal Task Tail = Task.CompletedTask;
        internal int Waiting;
        internal string? RunningVerb;
        internal string? RunningCommandId;
        internal long StartedAt;
        internal bool Said;
    }

    /// <summary>
    /// Run <paramref name="work"/> after everything already queued for <paramref name="sessionId"/>, and
    /// return its task. The call itself does no work: it takes a lock, appends, and returns, so the delivery
    /// loop is free before <paramref name="work"/> has started.
    /// </summary>
    internal Task<T> Run<T>(string? sessionId, string? verb, string? commandId, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var key = string.IsNullOrEmpty(sessionId) ? NoSession : sessionId;

        lock (_gate)
        {
            if (!_chains.TryGetValue(key, out var chain))
                _chains[key] = chain = new Chain();
            chain.Waiting++;

            // TaskScheduler.Default, so the continuation runs on the thread pool and never on whatever thread
            // completed the one before it. Unwrap, so the task handed back is the work's own task and not a
            // task that merely started it.
            var ran = chain.Tail.ContinueWith(
                _ => Watched(key, verb, commandId, work), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

            // The tail is the work's COMPLETION, whatever that completion is. A link that faults or is
            // cancelled must not stop the next one: ContinueWith runs regardless of how its antecedent ended,
            // which is why the chain is built from continuations rather than from awaits.
            chain.Tail = ran.ContinueWith(_ => Finished(key), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);

            return ran;
        }
    }

    /// <summary>
    /// True when <paramref name="sessionId"/> already has <see cref="MostThatMayWait"/> commands waiting, so
    /// the next one is refused rather than added to a queue that is not moving. <paramref name="why"/> is the
    /// sentence the refusal carries, and it names what is in front and how long it has been there, because
    /// "too many commands are waiting" on its own tells whoever reads it nothing they can act on.
    ///
    /// Checked by the caller rather than inside <see cref="Run{T}"/> so that the refusal is a real command
    /// result shaped by the delivery layer. The check and the append are not one atomic step, so a burst can
    /// land one or two over the cap; that is deliberate - a lock held across both would serialise every
    /// session's append against every other, which is the thing this type exists to stop.
    /// </summary>
    internal bool TooManyWaiting(string? sessionId, out string why)
    {
        var key = string.IsNullOrEmpty(sessionId) ? NoSession : sessionId;
        lock (_gate)
        {
            if (!_chains.TryGetValue(key, out var chain) || chain.Waiting <= MostThatMayWait)
            {
                why = "";
                return false;
            }

            var inFront = chain.RunningVerb is null
                ? "nothing is recorded as running"
                : $"{chain.RunningVerb} (cmdId={chain.RunningCommandId}) has been running for "
                  + $"{_clock.GetElapsedTime(chain.StartedAt).TotalSeconds:F1}s";
            why = $"this session already has {chain.Waiting - 1} command(s) waiting in the order they were sent, "
                  + $"and {inFront}. This command is refused rather than queued behind them, because a queue "
                  + "that is not moving would run all of it at once when it did (issue #4816).";
            return true;
        }
    }

    /// <summary>
    /// Say so, ONCE per run, for every session whose queue has been held past <see cref="Budget"/>.
    ///
    /// WHY THIS EXISTS AT ALL, AND WHY IT IS NOT OPTIONAL. The #4490 change added a watch on the delivery
    /// loop because a loop that never comes back says nothing about itself - only another thread can notice.
    /// This change takes the last long-running verb OFF that loop, so the loop watch will now almost never
    /// fire, and the thing that CAN still wedge is a session's chain. Without this, #4816 would have quietly
    /// removed the only instrument #4490 added. Called from the periodic re-push tick, which runs on the
    /// background scheduler and not on any chain.
    /// </summary>
    internal void SayIfAnyQueueIsHeld()
    {
        lock (_gate)
        {
            _timesAsked++;
            foreach (var (key, chain) in _chains)
            {
                if (chain.RunningVerb is null || chain.Said) continue;
                var held = _clock.GetElapsedTime(chain.StartedAt);
                if (held < Budget) continue;

                chain.Said = true;
                _timesSaid++;
                _write($"[OrderedBySession] SESSION INPUT QUEUE HELD for session={key} by verb={chain.RunningVerb}, "
                       + $"cmdId={chain.RunningCommandId} for {held.TotalSeconds:F1}s, with {chain.Waiting - 1} command(s) "
                       + "waiting behind it - every one of those is expiring against the Gateway's own wait. Other "
                       + "sessions and the delivery loop itself are unaffected (issue #4816).");
            }
        }
    }

    /// <summary>
    /// How many chains are being held right now, and nearly the only thing this type says about itself. A
    /// test reads it to prove a finished session's chain is let go: the Director's sessions come and go for
    /// as long as it runs, and a dictionary that only ever grows is a leak however small each entry is.
    /// </summary>
    internal int ChainsHeld { get { lock (_gate) return _chains.Count; } }

    /// <summary>
    /// How many times something asked whether a queue was held, and how many times the answer was yes. Two
    /// different facts, and the first is the one that was missing from the loop watch until 8 November 2026:
    /// nothing asked. A test proves the periodic tick asks, with no clock in it; the fake-clock tests prove
    /// what the answer is past the budget.
    /// </summary>
    internal int TimesItWasAsked { get { lock (_gate) return _timesAsked; } }

    /// <summary>How many held queues this has reported. See <see cref="TimesItWasAsked"/>.</summary>
    internal int TimesItSaidAQueueWasHeld { get { lock (_gate) return _timesSaid; } }

    private int _timesAsked;
    private int _timesSaid;

    /// <summary>What is running on <paramref name="sessionId"/>'s queue and for how long, or null when it is
    /// free. The question the loop watch answers for the loop, asked of one session.</summary>
    internal (string Verb, string CommandId, TimeSpan Held, int Waiting)? Inside(string? sessionId)
    {
        var key = string.IsNullOrEmpty(sessionId) ? NoSession : sessionId;
        lock (_gate)
        {
            if (!_chains.TryGetValue(key, out var chain) || chain.RunningVerb is null) return null;
            return (chain.RunningVerb, chain.RunningCommandId ?? "", _clock.GetElapsedTime(chain.StartedAt), chain.Waiting);
        }
    }

    private Task<T> Watched<T>(string key, string? verb, string? commandId, Func<Task<T>> work)
    {
        lock (_gate)
        {
            if (_chains.TryGetValue(key, out var chain))
            {
                chain.RunningVerb = string.IsNullOrEmpty(verb) ? "(no verb)" : verb;
                chain.RunningCommandId = commandId ?? "";
                chain.StartedAt = _clock.GetTimestamp();
                chain.Said = false;
            }
        }

        // Outside the lock: the work is the whole point, and it must never run holding the gate that every
        // other session's append takes.
        return work();
    }

    private void Finished(string key)
    {
        lock (_gate)
        {
            if (!_chains.TryGetValue(key, out var chain)) return;
            chain.RunningVerb = null;
            chain.RunningCommandId = null;
            if (--chain.Waiting <= 0)
                _chains.Remove(key);
        }
    }
}
