using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Storage;

/// <summary>
/// Phase 5: per-Director coordinator that creates a <see cref="SessionLogWriter"/>
/// for every session and tears it down when the session is gone. Mirrors the
/// pattern used by <c>SessionStatusWingman</c> and <c>TurnSummaryCache</c>:
/// subscribe to <c>SessionManager.OnSessionCreated</c>, own the per-session helper.
///
/// External consumers (the TurnSummaryCache, the wingman, future agent-view
/// pipeline) push their records through the writer via this manager:
///
///   manager.WriteTurnSummary(sessionId, summary);
///
/// We expose those forwarding methods rather than the underlying writer so
/// SessionManager-only code can never accidentally take a lifecycle dependency
/// on a writer instance.
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

    /// <summary>Begin watching sessions. Idempotent.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        FileLog.Write("[SessionLogManager] Start");

        _sessionManager.OnSessionCreated += OnSessionCreated;

        // Wire any sessions that already exist (restored on Director boot).
        foreach (var s in _sessionManager.ListSessions())
            EnsureWriter(s);
    }

    private void OnSessionCreated(Session session) => EnsureWriter(session);

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

        foreach (var w in _writers.Values)
        {
            try { w.Dispose(); }
            catch (Exception ex) { FileLog.Write($"[SessionLogManager] writer dispose failed: {ex.Message}"); }
        }
        _writers.Clear();
    }
}
