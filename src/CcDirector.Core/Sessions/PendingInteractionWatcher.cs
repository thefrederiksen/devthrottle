using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Keeps <see cref="Session.PendingInteraction"/> true to what the agent is actually holding, so the
/// Smart shutdown dialog can show the owner which sessions have a question box open BEFORE it asks him
/// anything (the Smart Director Restart mission, issue #3167).
///
/// Until this existed the property had a private setter and nothing in the product ever assigned it, so
/// it was null for every session forever and that section of the dialog could never appear on a real
/// Director. This is what makes it appear.
///
/// THE TRIGGER IS A TURN END, AND THERE IS NO TIMER. It subscribes to
/// <see cref="Session.OnActivityStateChanged"/> and reads when a session flips to
/// <see cref="ActivityState.WaitingForInput"/> - the same trigger <see cref="SessionRecordsWatcher"/>
/// and <see cref="Storage.ConversationIngestor"/> use, and exactly the moment a session has stopped and
/// may be holding a question. A session that is mid-turn cannot be waiting on the user.
///
/// THE CLEARING IS NOT THIS CLASS'S ALONE, ON PURPOSE. Two things clear the property, and both are
/// needed:
/// - <see cref="Session"/> itself drops it the moment the session leaves the waiting states, so a user
///   who answers sees the flag go the instant the session goes back to work. That is what the property
///   has always documented and it is now true.
/// - This class writes null at the next turn end once the transcript shows the ask has gained its
///   result. That covers the case where the session settles again without ever passing through Working.
/// A stale question box in the shutdown dialog is worse than none: the owner would go hunting for a
/// question nobody is asking.
///
/// It is read-only over the transcript, swallows every fault, and never blocks the event thread. One
/// instance per Director.
/// </summary>
public sealed class PendingInteractionWatcher : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private bool _started;
    private int _disposed;

    public PendingInteractionWatcher(SessionManager sessionManager)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        _sessionManager = sessionManager;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[PendingInteractionWatcher] Start");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var session in _sessionManager.ListSessions())
            WireSession(session);
    }

    /// <summary>
    /// Watch one session's turn ends. Internal rather than private so a test can drive the REAL handler
    /// through a REAL activity-state flip, instead of calling the refresh directly and proving nothing
    /// about the trigger.
    /// </summary>
    internal void WireSession(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            _ = oldState; // only the state we moved INTO matters here
            if (newState != ActivityState.WaitingForInput) return;
            // The generation is read HERE, on the event thread, so the refresh below can tell whether
            // the session has moved on while it was reading the transcript. See Refresh.
            var generation = session.ActivityGeneration;
            // Off the event thread: reading and parsing a transcript must not stall the detector or the
            // interface.
            Task.Run(() => Refresh(session, generation));
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;

        // A session that is ALREADY parked when it is wired - a restored session after a Director
        // restart, which is precisely the case this mission is about - would otherwise carry nothing
        // until it happened to end another turn, and a session waiting on a question will not end one.
        // This is one read at wire-up, not a poll.
        if (session.ActivityState == ActivityState.WaitingForInput)
        {
            var generation = session.ActivityGeneration;
            _ = Task.Run(() => Refresh(session, generation));
        }
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var handler))
            session.OnActivityStateChanged -= handler;
    }

    /// <summary>
    /// Read the transcript and stamp what it says onto the session. A boundary and a fire-and-forget
    /// target, so it owns its try/catch: a transcript fault must never escape onto a background thread
    /// and must never break a turn.
    /// </summary>
    /// <param name="session">The session that just parked at a turn end.</param>
    /// <param name="generation">The session's <see cref="Session.ActivityGeneration"/> at the moment the
    /// turn end fired. Reading a transcript takes real time, and in that time the user can answer and
    /// the session can go back to work - which clears the property. Writing the read's answer after that
    /// would put a question box back on a session that no longer has one, which is the stale flag this
    /// whole class is careful about. A changed generation means the answer is about a moment that has
    /// passed, so it is dropped.</param>
    internal static void Refresh(Session session, long generation)
    {
        try
        {
            var result = PendingInteractionDetector.Detect(session);
            if (result.Reading == PendingInteractionReading.NoAnswer) return;

            if (session.ActivityGeneration != generation)
            {
                FileLog.Write($"[PendingInteractionWatcher] Refresh: sessionId={session.Id} moved on while " +
                              $"its transcript was read (generation {generation} -> " +
                              $"{session.ActivityGeneration}); the reading is dropped rather than stamped");
                return;
            }

            session.SetPendingInteraction(result.Interaction);
            FileLog.Write($"[PendingInteractionWatcher] Refresh: sessionId={session.Id} " +
                          (result.Interaction is null
                              ? "has no outstanding ask; pending interaction cleared"
                              : $"is holding a {result.Interaction.Kind}: {result.Interaction.Prompt}"));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PendingInteractionWatcher] Refresh FAILED (swallowed) sessionId={session.Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var session in _sessionManager.ListSessions())
            UnwireSession(session);
        _handlers.Clear();
    }
}
