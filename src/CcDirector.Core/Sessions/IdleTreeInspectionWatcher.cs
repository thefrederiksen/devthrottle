using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// When a session parks at the end of a turn, inspect the folder it works in and report that folder's
/// repository to the Gateway (the owner's decision, 5 November 2026).
///
/// WHY THIS EXISTS, AND IT IS NOT ABOUT COST. The Fleet Manager offers the owner <em>Close</em> on a
/// stopped session only when the Gateway's report of that session's repository was collected AFTER the
/// session last did anything - otherwise it cannot tell whether the work is pushed and merged, and it
/// refuses rather than guess (<c>FleetManagerCloseRule</c>, which says so in those words). That report
/// arrived on one schedule only: once about two minutes after the Director starts, then every six hours.
/// So from the moment a session stops until the next six-hourly report, Close was refused for it -
/// however clean and merged its work was. The number nobody looks at was refreshed 288 times a day while
/// the one number a decision depends on was refreshed four.
///
/// This makes the rule true by construction rather than by luck: the inspection is triggered BY the last
/// activity, so it is always after it.
///
/// THE TRIGGER IS A TURN END, AND THERE IS NO TIMER. It is the same trigger
/// <see cref="PendingInteractionWatcher"/> and <see cref="Storage.ConversationIngestor"/> use - the
/// moment a session has stopped and is waiting - so this class adds no polling of its own and does
/// nothing at all on a machine whose sessions are working.
///
/// IT IS THROTTLED PER FOLDER, because a turn end is not rare. Two sessions in one tree, or one session
/// parking repeatedly while its owner answers it, ask for one inspection, not several: a second request
/// for the same folder within <see cref="MinimumGap"/> is dropped. The inspection it asks for is one
/// repository's, not the whole machine's.
///
/// It never throws onto the event thread, never blocks it, and swallows every fault: a failed inspection
/// leaves the Gateway holding the previous report, which is exactly the state this class improves and
/// never worsens.
/// </summary>
public sealed class IdleTreeInspectionWatcher : IDisposable
{
    /// <summary>The shortest gap between two inspections of the SAME folder asked for by a turn end.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(2);

    private readonly SessionManager _sessionManager;
    private readonly Func<string, CancellationToken, Task> _inspect;
    private readonly Action<Action> _dispatch;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _gap;

    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private readonly Dictionary<string, DateTime> _lastAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private bool _started;
    private int _disposed;

    /// <param name="inspect">What an inspection IS, as a delegate rather than a service: this class lives
    /// in the core and the reporting lives in the Director's host, and it is the seam a test drives, so
    /// the trigger and the throttle below are proven rather than described.</param>
    public IdleTreeInspectionWatcher(SessionManager sessionManager, Func<string, CancellationToken, Task> inspect)
        : this(sessionManager, inspect, work => Task.Run(work), () => DateTime.UtcNow, MinimumGap)
    {
    }

    internal IdleTreeInspectionWatcher(
        SessionManager sessionManager, Func<string, CancellationToken, Task> inspect,
        Action<Action> dispatch, Func<DateTime> utcNow, TimeSpan gap)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(utcNow);
        _sessionManager = sessionManager;
        _inspect = inspect;
        _dispatch = dispatch;
        _utcNow = utcNow;
        _gap = gap;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write($"[IdleTreeInspectionWatcher] Start: one inspection per folder at a turn end, at most one every {_gap.TotalMinutes:0} minutes");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var session in _sessionManager.ListSessions())
            WireSession(session);
    }

    /// <summary>
    /// Watch one session's turn ends. Internal rather than private so a test can drive the REAL handler
    /// through a REAL activity-state flip, instead of calling the ask directly and proving nothing about
    /// the trigger.
    /// </summary>
    internal void WireSession(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            _ = oldState; // only the state we moved INTO matters here
            if (!ActivityStateFacts.IsTurnEnd(newState)) return;
            Ask(session.RepoPath);
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var handler))
            session.OnActivityStateChanged -= handler;
    }

    /// <summary>
    /// Ask for one folder's inspection, unless that folder was asked for inside the gap. Internal so a
    /// test can prove the throttle without driving a session.
    /// </summary>
    internal void Ask(string? folder)
    {
        if (_disposed != 0) return;
        if (string.IsNullOrWhiteSpace(folder))
            return; // a session with no folder has no repository to inspect

        var now = _utcNow();
        lock (_gate)
        {
            if (_lastAsked.TryGetValue(folder, out var last) && now - last < _gap)
                return;
            _lastAsked[folder] = now;
        }

        FileLog.Write($"[IdleTreeInspectionWatcher] a session parked in {folder}; inspecting it");
        // Off the event thread: an inspection runs git and talks to the Gateway, and neither may stall
        // the detector or the interface.
        _dispatch(() => InspectAsync(folder));
    }

    /// <summary>
    /// A fire-and-forget boundary, so it owns its try/catch: a fault here must never escape onto a
    /// background thread, and the worst outcome of one is the report the Gateway already had.
    /// </summary>
    private void InspectAsync(string folder)
    {
        try
        {
            _inspect(folder, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[IdleTreeInspectionWatcher] inspecting {folder} FAILED: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var session in _sessionManager.ListSessions())
            UnwireSession(session);
        FileLog.Write("[IdleTreeInspectionWatcher] Dispose: stopped");
    }
}
