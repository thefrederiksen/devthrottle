using System.Collections.Concurrent;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Storage;

/// <summary>
/// Phase 5: per-Director coordinator that creates a <see cref="SessionLogWriter"/>
/// for every session and tears it down when the session is gone - WHEN session
/// logging is switched on. It is off by default (<see cref="SessionLogConfig"/>),
/// and off means this manager subscribes to nothing and writes nothing at all. Mirrors the
/// pattern used by <c>SessionStatusWingman</c> and <c>TurnSummaryCache</c>:
/// subscribe to <c>SessionManager.OnSessionCreated</c>, own the per-session helper.
///
/// The forwarding methods below exist so that a consumer could push a record
/// through this manager rather than hold a writer instance and take a lifecycle
/// dependency on it:
///
///   manager.WriteTurnSummary(sessionId, summary);
///
/// NOTHING CALLS THEM TODAY. This comment used to name the TurnSummaryCache, the
/// wingman and a future agent-view pipeline as consumers that push through here,
/// and none of them do - the only calls anywhere are tests invoking the writer
/// directly. So turns.jsonl and agent-view.jsonl are never written by the product
/// at all, which is part of why the whole capture is off by default.
/// </summary>
public sealed class SessionLogManager : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, SessionLogWriter> _writers = new();
    private bool _started;
    private bool _disposed;

    public SessionLogManager(SessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    /// <summary>
    /// Begin watching sessions. Idempotent.
    ///
    /// Writes nothing unless session logging is switched on - see <see cref="SessionLogConfig"/>.
    /// When it is off this manager attaches to no session and opens no file, so the four streams
    /// are not merely empty, they are absent.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        // OFF by default. The raw stream records every byte the terminal painted - spinner frames
        // included - base64-encoded, uncapped and unaged, and nothing in the product reads it. It is
        // switched on for a terminal investigation by session_logs.enabled in config.json or the
        // CC_DIRECTOR_SESSION_LOGS override, and switched off again afterwards.
        if (!SessionLogConfig.IsEnabled())
        {
            FileLog.Write(
                $"[SessionLogManager] Start: session logging is OFF, writing nothing "
                + $"(turn on with {SessionLogConfig.SectionName}.enabled in config.json)");
            return;
        }

        FileLog.Write($"[SessionLogManager] Start: session logging is ON ({SessionLogConfig.SectionName}.enabled)");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        _sessionManager.OnSessionRemoved += OnSessionRemoved;

        // Wire any sessions that already exist (restored on Director boot).
        foreach (var s in _sessionManager.ListSessions())
            EnsureWriter(s);
    }

    private void OnSessionCreated(Session session) => EnsureWriter(session);

    /// <summary>Close and release the session's log writer when the session is
    /// removed (the teardown this manager's doc comment always promised).</summary>
    private void OnSessionRemoved(Session session)
    {
        if (_writers.TryRemove(session.Id, out var writer))
        {
            try { writer.Dispose(); }
            catch (Exception ex) { FileLog.Write($"[SessionLogManager] writer dispose failed for {session.Id}: {ex.Message}"); }
            FileLog.Write($"[SessionLogManager] writer closed for {session.Id}");
        }
    }

    private void EnsureWriter(Session session)
    {
        if (_writers.ContainsKey(session.Id)) return;
        var writer = new SessionLogWriter(session);
        if (_writers.TryAdd(session.Id, writer))
        {
            writer.Start();
            FileLog.Write($"[SessionLogManager] writer started for {session.Id}");
        }
        else
        {
            // Lost the race; throw away the duplicate.
            writer.Dispose();
        }
    }

    /// <summary>
    /// Push a TurnSummary onto the session's persistent log. Called by
    /// <c>TurnSummaryCache</c> once Haiku finishes generating the summary.
    /// </summary>
    public void WriteTurnSummary(Guid sessionId, TurnSummary summary)
    {
        if (_writers.TryGetValue(sessionId, out var writer))
            writer.WriteTurnSummary(summary);
    }

    /// <summary>
    /// Push an agent-view widget record onto the session's persistent log.
    /// Hook for the agent-view layer (slot reserved for a follow-up slice).
    /// </summary>
    public void WriteAgentViewWidget(Guid sessionId, object widget)
    {
        if (_writers.TryGetValue(sessionId, out var writer))
            writer.WriteAgentViewWidget(widget);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;

        foreach (var w in _writers.Values)
        {
            try { w.Dispose(); }
            catch (Exception ex) { FileLog.Write($"[SessionLogManager] writer dispose failed: {ex.Message}"); }
        }
        _writers.Clear();
    }
}
