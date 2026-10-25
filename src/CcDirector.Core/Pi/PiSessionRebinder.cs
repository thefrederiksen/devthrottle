using System.Collections.Concurrent;
using CcDirector.Core.Agents;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Pi;

/// <summary>
/// Follows a Pi session across pi's own <c>/new</c>. The Director names every Pi session's transcript by
/// launching pi with <c>--session-id</c>, so the file is known from birth (issue #2670) - except after a
/// context clear: <c>/new</c> starts a fresh session file under an id pi chose, and pi writes that file
/// only when the next message is sent, so the id cannot be learned at clear time (the synchronous wait in
/// <see cref="Session.ClearContextAsync"/> would sit out its whole minute and then fail a clear that had
/// worked). Instead <see cref="Session.ClearContextAsync"/> stamps <see cref="Session.ContextClearedUtc"/>,
/// and this watcher, at the session's next turn end - the first moment the new file can exist - looks for
/// a file in the session's repo CREATED after the clear under an id that is not the one just left, and
/// relinks the session to it through <see cref="SessionManager.RelinkClaudeSession"/>. From then on every
/// reader (the turn push, the context gauge, the model report, a later reopen) resolves the new file by
/// its id, and the turn push starts a new generation for it.
///
/// Turn-end-driven for the same reason <see cref="SessionRecordsWatcher"/> is: the scan reads pi's session
/// headers on disk, and a turn end is the one moment the answer can have changed. One instance per
/// Director, wired beside that watcher.
/// </summary>
public sealed class PiSessionRebinder : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private bool _started;
    private int _disposed;

    public PiSessionRebinder(SessionManager sessionManager)
        => _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[PiSessionRebinder] Start");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void WireSession(Session session)
    {
        if (session.AgentKind != AgentKind.Pi) return;
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            _ = oldState;
            if (!ActivityStateFacts.IsTurnEnd(newState)) return;
            Task.Run(() => Rebind(session));
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnActivityStateChanged -= h;
    }

    /// <summary>
    /// If a clear is outstanding on this session, find the file pi started for it and relink. A boundary
    /// (fire-and-forget target): it owns its try/catch so a disk fault never escapes onto a background
    /// thread. Nothing found means pi has not written the post-clear file yet; the next turn end looks again.
    /// </summary>
    internal void Rebind(Session session)
    {
        try
        {
            if (session.ContextClearedUtc is not DateTime cleared) return;
            var found = PiSessionLocator.FindCreatedAfter(session.RepoPath, cleared, session.ClaudeSessionId);
            if (found is null) return;

            FileLog.Write($"[PiSessionRebinder] session={session.Id}: the clear at {cleared:O} started pi session {found.Id}; relinking from {session.ClaudeSessionId ?? "(none)"}");
            _sessionManager.RelinkClaudeSession(session.Id, found.Id);
            session.ClearContextClearedStamp();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PiSessionRebinder] rebind FAILED: session={session.Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
    }
}
