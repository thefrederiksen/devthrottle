using System.Collections.Concurrent;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Per-session cache of Wingman turn summaries (WingmanService.SummarizeTurnAsync).
///
/// One instance per Director. Summaries are generated ON DEMAND via
/// <see cref="GenerateForLatestTurnAsync"/> (the voice/mobile views call it when they open
/// a session); the Director no longer auto-summarizes on a turn-boundary event. The cache is
/// in-memory (lost on Director restart, which is fine - the views re-generate lazily).
/// </summary>
public sealed class TurnSummaryCache : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly AgentOptions _options;
    private readonly ConcurrentDictionary<Guid, List<TurnSummary>> _cache = new();
    private bool _started;
    private bool _disposed;

    /// <summary>Max summaries kept per session.  Older ones are evicted.</summary>
    public int MaxSummariesPerSession { get; set; } = 100;

    public TurnSummaryCache(SessionManager sessionManager, AgentOptions options)
    {
        _sessionManager = sessionManager;
        _options = options;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[TurnSummaryCache] Start");

        _sessionManager.OnSessionContextReset += OnSessionContextReset;
        _sessionManager.OnSessionRemoved += OnSessionRemoved;
    }

    /// <summary>Drop all per-session state when a session is removed.</summary>
    private void OnSessionRemoved(Session session) => _cache.TryRemove(session.Id, out _);

    private void OnSessionContextReset(Session session) => ClearSession(session.Id);

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
    /// Synchronously generate a summary for the LATEST turn of a session and add it to the
    /// cache. Summarizes what is currently on THIS session's terminal (the tail of its own
    /// PTY buffer) - no shared folder, so it cannot pick up another session's conversation.
    /// </summary>
    public async Task<TurnSummary?> GenerateForLatestTurnAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session is null) return null;

        var transcript = SnapshotTerminalTail(session.Buffer);
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var summary = await WingmanService.SummarizeTurnAsync(
            transcript, DateTime.UtcNow, session.RepoPath, _options.ClaudePath, ct);

        AddToCache(sessionId, summary);
        return summary;
    }

    /// <summary>
    /// Run a goal assessment for a session right now (used when a goal is first set,
    /// so the user sees a verdict without waiting). Safe no-op when the session has no
    /// goal. Stores the verdict on the session.
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
        _sessionManager.OnSessionContextReset -= OnSessionContextReset;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
    }
}
