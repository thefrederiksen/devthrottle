using System.Collections.Concurrent;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
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
    private readonly SessionStatusWingman? _statusWingman;
    private readonly Core.Storage.SessionLogManager? _logManager;
    private readonly ConcurrentDictionary<Guid, List<TurnSummary>> _cache = new();
    private readonly ConcurrentDictionary<Guid, Action<Session, TurnData>> _handlers = new();
    private bool _started;
    private bool _disposed;

    /// <summary>Max summaries kept per session.  Older ones are evicted.</summary>
    public int MaxSummariesPerSession { get; set; } = 100;

    public TurnSummaryCache(SessionManager sessionManager, AgentOptions options, SessionStatusWingman? statusWingman = null, Core.Storage.SessionLogManager? logManager = null)
    {
        _sessionManager = sessionManager;
        _options = options;
        _statusWingman = statusWingman;
        _logManager = logManager;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[TurnSummaryCache] Start");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        _sessionManager.OnSessionContextReset += OnSessionContextReset;

        // Wire up existing sessions too (restored on startup).
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

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
    /// Synchronously generate a summary for the LATEST turn of a session and
    /// add it to the cache.  Used by the voice mode to grab a summary on demand
    /// when it cannot wait for the OnTurnCompleted background path.
    /// </summary>
    public async Task<TurnSummary?> GenerateForLatestTurnAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = _sessionManager.GetSession(sessionId);
        if (session is null) return null;

        // Build a synthetic TurnData from whatever is currently in the JSONL.
        var turn = SnapshotLatestTurnFromJsonl(session);
        if (turn is null) return null;

        var lastAssistantText = TryReadLastAssistantText(session);

        var summary = await WingmanService.SummarizeTurnAsync(
            turn, lastAssistantText, session.RepoPath, _options.ClaudePath, ct);

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

        Action<Session, TurnData> handler = (s, t) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var lastAssistantText = TryReadLastAssistantText(s);
                    var summary = await WingmanService.SummarizeTurnAsync(
                        t, lastAssistantText, s.RepoPath, _options.ClaudePath);
                    AddToCache(s.Id, summary);
                    // Hand the fresh summary to the status wingman (slow path).
                    _statusWingman?.ApplyTurnSummary(s, summary);
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
    /// Read the FULL last assistant text widget out of the session's JSONL, with no
    /// front-truncation. The caller is responsible for fitting the result into its
    /// own prompt budget; question detection needs the END of the response (where
    /// trailing "?" lives), so we never lop the tail off here. Returns null when
    /// the JSONL is missing or has no assistant text yet (brand-new session, or
    /// link not yet recorded).
    /// </summary>
    /// <remarks>
    /// Bypasses <see cref="SummaryBuilder.Build"/> on purpose: that path runs the
    /// content through <c>Truncate(s, 2000)</c> which keeps the FIRST 2000 chars,
    /// reliably cutting off any trailing question on a long response.
    /// </remarks>
    internal static string? TryReadLastAssistantText(Session session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.ClaudeSessionId)) return null;
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return null;
            var messages = StreamMessageParser.ParseFile(jsonl);
            var widgets = WidgetBuilder.BuildFromMessages(messages);
            for (int i = widgets.Count - 1; i >= 0; i--)
            {
                if (widgets[i].Kind == "Text" && !string.IsNullOrEmpty(widgets[i].Content))
                    return widgets[i].Content;
            }
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnSummaryCache] TryReadLastAssistantText FAILED for {session.Id}: {ex.Message}");
            return null;
        }
    }

    private static TurnData? SnapshotLatestTurnFromJsonl(Session session)
    {
        // For on-demand summary requests we synthesise a TurnData from the JSONL
        // structured summary; this is enough for the Haiku prompt.
        try
        {
            if (string.IsNullOrEmpty(session.ClaudeSessionId)) return null;
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return null;
            var messages = StreamMessageParser.ParseFile(jsonl);
            var s = SummaryBuilder.Build(messages);
            return new TurnData(
                UserPrompt: s.LastUserPrompt ?? "",
                ToolsUsed: new List<string>(),
                FilesTouched: s.FilesTouched.Select(f => f.Path).Take(10).ToList(),
                BashCommands: s.RecentCommands.Take(10).ToList(),
                Timestamp: DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionContextReset -= OnSessionContextReset;
        foreach (var s in _sessionManager.ListSessions())
        {
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnTurnCompleted -= h;
        }
    }
}
