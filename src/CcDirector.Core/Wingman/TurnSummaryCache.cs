using System.Collections.Concurrent;
using System.Text;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Bridges Session.OnTurnCompleted -> WingmanService.SummarizeTurnAsync -> per-session cache.
///
/// One instance per Director.  On <see cref="Start"/> it subscribes to
/// <see cref="SessionManager.OnSessionCreated"/> so every new session gets a
/// turn-completion subscription.  Existing sessions at startup are wired up too.
///
/// The cache is in-memory (lost on Director restart, which is fine - the Agent
/// View will re-generate summaries lazily when the user clicks into a session).
/// </summary>
public sealed class TurnSummaryCache : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly AgentOptions _options;
    private readonly Core.Storage.SessionLogManager? _logManager;
    private readonly ConcurrentDictionary<Guid, List<TurnSummary>> _cache = new();
    private readonly ConcurrentDictionary<Guid, Action<Session, TurnData>> _handlers = new();
    // Per-session terminal-buffer read cursor. A turn summary covers the bytes this
    // session's PTY emitted since the previous summary. Keyed by Director session Guid
    // so one session can never read another's output.
    private readonly ConcurrentDictionary<Guid, long> _bufferCursors = new();
    private bool _started;
    private bool _disposed;

    /// <summary>Max summaries kept per session.  Older ones are evicted.</summary>
    public int MaxSummariesPerSession { get; set; } = 100;

    public TurnSummaryCache(SessionManager sessionManager, AgentOptions options, Core.Storage.SessionLogManager? logManager = null)
    {
        _sessionManager = sessionManager;
        _options = options;
        _logManager = logManager;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[TurnSummaryCache] Start");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        _sessionManager.OnSessionContextReset += OnSessionContextReset;
        _sessionManager.OnSessionRemoved += OnSessionRemoved;

        // Wire up existing sessions too (restored on startup).
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    /// <summary>Unhook the turn-completed handler and drop all per-session state
    /// when a session is removed, so closed sessions do not leak handlers, cached
    /// summaries, or buffer cursors.</summary>
    private void OnSessionRemoved(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnTurnCompleted -= h;
        _cache.TryRemove(session.Id, out _);
        _bufferCursors.TryRemove(session.Id, out _);
    }

    private void OnSessionContextReset(Session session)
    {
        ClearSession(session.Id);
        // Advance the read cursor past everything already on screen so the next summary
        // covers only post-/clear output, not the wiped conversation.
        _bufferCursors[session.Id] = session.Buffer?.TotalBytesWritten ?? 0;
    }

    /// <summary>
    /// Drop all cached turn summaries for a session. Called after Claude Code rotates
    /// its session id on <c>/clear</c> so the Wingman stops surfacing summaries of the
    /// pre-clear conversation. No-op when the session has no cached summaries.
    /// </summary>
    public void ClearSession(Guid sessionId)
    {
        if (_cache.TryRemove(sessionId, out _))
            FileLog.Write($"[TurnSummaryCache] cleared summaries for {sessionId} after /clear");
    }

    public IReadOnlyList<TurnSummary> GetForSession(Guid sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var list))
        {
            lock (list) return list.ToList();
        }
        return Array.Empty<TurnSummary>();
    }

    /// <summary>
    /// Synchronously generate a summary for the LATEST turn of a session and
    /// add it to the cache.  Used by the voice mode to grab a summary on demand
    /// when it cannot wait for the OnTurnCompleted background path.
    /// </summary>
    public async Task<TurnSummary?> GenerateForLatestTurnAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session is null) return null;

        // On-demand: summarize what is currently on THIS session's terminal (the tail
        // of its own PTY buffer). No JSONL, no shared folder - cannot pick up another
        // session's conversation.
        var transcript = SnapshotTerminalTail(session.Buffer);
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var summary = await WingmanService.SummarizeTurnAsync(
            transcript, DateTime.UtcNow, session.RepoPath, _options.ClaudePath, ct);

        AddToCache(sessionId, summary);
        return summary;
    }

    /// <summary>
    /// Run a goal assessment for a session right now (used when a goal is first set,
    /// so the user sees a verdict without waiting for the next turn to finish). Safe
    /// no-op when the session has no goal. Stores the verdict on the session.
    /// </summary>
    public async Task AssessGoalNowAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session is null) return;
        await AssessGoalIfSetAsync(session, ct);
    }

    private async Task AssessGoalIfSetAsync(Session session, CancellationToken ct = default)
    {
        var goal = session.WingmanGoal;
        if (string.IsNullOrWhiteSpace(goal)) return;

        var recent = GetForSession(session.Id);
        var assessment = await WingmanService.AssessGoalAsync(
            goal, recent, session.RepoPath, _options.ClaudePath, ct);
        session.SetWingmanGoalAssessment(assessment.State, assessment.Reason, assessment.EvaluatedAt);
        FileLog.Write($"[TurnSummaryCache] goal verdict for {session.Id}: {assessment.State} - {assessment.Reason}");
    }

    private void OnSessionCreated(Session session) => WireSession(session);

    private void WireSession(Session session)
    {
        if (_handlers.ContainsKey(session.Id)) return;
        FileLog.Write($"[TurnSummaryCache] wiring session {session.Id}");

        // Start the read cursor at the current end of this session's buffer, so the
        // first summary covers only output produced from here on.
        _bufferCursors[session.Id] = session.Buffer?.TotalBytesWritten ?? 0;

        Action<Session, TurnData> handler = (s, t) =>
        {
            // The turn-completed event is only a TIMING pulse (when a turn ends).
            // The summary CONTENT comes solely from this session's own terminal
            // buffer below - never from the event's hook-derived TurnData, and never
            // from Claude Code's shared .jsonl. So even a mis-delivered pulse can only
            // summarize the receiving session's own output.
            _ = Task.Run(async () =>
            {
                try
                {
                    var sinceCursor = _bufferCursors.TryGetValue(s.Id, out var cur) ? cur : 0;
                    var (transcript, newCursor) = CaptureTurnTranscript(s.Buffer, sinceCursor);

                    // No new terminal output since the last summary: a no-op or spurious
                    // pulse. Advance past it and don't summarize.
                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        _bufferCursors[s.Id] = newCursor;
                        return;
                    }

                    // Global 5s LLM floor (shared with the detector's classify). If we're
                    // inside the window, skip WITHOUT advancing the cursor so this turn's
                    // output rolls into the next summary rather than being dropped. This is
                    // what stops a flappy session from looping on the model and burning tokens.
                    if (!WingmanLlmThrottle.TryAcquire(s.Id))
                        return;

                    _bufferCursors[s.Id] = newCursor;
                    var summary = await WingmanService.SummarizeTurnAsync(
                        transcript, t.Timestamp.UtcDateTime, s.RepoPath, _options.ClaudePath);
                    AddToCache(s.Id, summary);
                    // The badge colour is owned solely by the TerminalStateDetector's
                    // state machine (bytes -> blue, silence -> red); the turn summary no
                    // longer votes on colour. It is still cached and persisted below for
                    // the Agent view, voice, and goal tracking.
                    // Phase 5: persist the summary to disk so the wingman's history
                    // survives Director restart and is replayable for "ask" queries.
                    _logManager?.WriteTurnSummary(s.Id, summary);
                    FileLog.Write($"[TurnSummaryCache] cached summary for {s.Id}: \"{(summary.Headline.Length > 80 ? summary.Headline[..80] + "..." : summary.Headline)}\"");

                    // Goal management: if this session has a stated goal, judge whether
                    // it is still on track. Observational only - we store the verdict on
                    // the session for the Wingman view; we do not change the status color.
                    await AssessGoalIfSetAsync(s);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[TurnSummaryCache] background summarise FAILED for {s.Id}: {ex.Message}");
                }
            });
        };
        _handlers[session.Id] = handler;
        session.OnTurnCompleted += handler;
    }

    private void AddToCache(Guid sessionId, TurnSummary summary)
    {
        var list = _cache.GetOrAdd(sessionId, _ => new List<TurnSummary>());
        lock (list)
        {
            list.Add(summary);
            while (list.Count > MaxSummariesPerSession) list.RemoveAt(0);
        }
    }

    /// <summary>
    /// Capture this session's terminal output produced since <paramref name="sinceCursor"/>,
    /// ANSI-stripped, ready to hand to the Wingman summariser. Returns the cleaned text
    /// plus the new cursor to store for the next turn. The text is the literal byte
    /// stream of THIS session's PTY - it cannot contain another session's output.
    ///
    /// When the delta is very large (a long turn) we keep the END, where the agent's
    /// most recent message and any trailing question live.
    /// </summary>
    internal static (string transcript, long newCursor) CaptureTurnTranscript(
        CircularTerminalBuffer? buffer, long sinceCursor, int maxChars = 16000)
    {
        if (buffer is null) return (string.Empty, sinceCursor);
        var (bytes, newCursor) = buffer.GetWrittenSince(sinceCursor);
        if (bytes.Length == 0) return (string.Empty, newCursor);
        var text = TerminalOutputParser.StripAnsi(Encoding.UTF8.GetString(bytes));
        if (text.Length > maxChars) text = text[^maxChars..];
        return (text, newCursor);
    }

    /// <summary>
    /// Snapshot the tail of a session's terminal buffer (what is currently on screen),
    /// ANSI-stripped. Used by the on-demand path where there is no per-turn cursor -
    /// we just summarise the most recent output. Returns empty when the buffer is empty.
    /// </summary>
    private static string SnapshotTerminalTail(CircularTerminalBuffer? buffer, int maxChars = 16000)
    {
        if (buffer is null) return string.Empty;
        var bytes = buffer.DumpAll();
        if (bytes.Length == 0) return string.Empty;
        var text = TerminalOutputParser.StripAnsi(Encoding.UTF8.GetString(bytes));
        if (text.Length > maxChars) text = text[^maxChars..];
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionContextReset -= OnSessionContextReset;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
        foreach (var s in _sessionManager.ListSessions())
        {
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnTurnCompleted -= h;
        }
    }
}
