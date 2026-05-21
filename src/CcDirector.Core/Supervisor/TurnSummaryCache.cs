using System.Collections.Concurrent;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Supervisor;

/// <summary>
/// Bridges Session.OnTurnCompleted -> SupervisorService.SummarizeTurnAsync -> per-session cache.
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
    private readonly SessionStatusSupervisor? _statusSupervisor;
    private readonly Core.Storage.SessionLogManager? _logManager;
    private readonly ConcurrentDictionary<Guid, List<TurnSummary>> _cache = new();
    private readonly ConcurrentDictionary<Guid, Action<Session, TurnData>> _handlers = new();
    private bool _started;
    private bool _disposed;

    /// <summary>Max summaries kept per session.  Older ones are evicted.</summary>
    public int MaxSummariesPerSession { get; set; } = 100;

    public TurnSummaryCache(SessionManager sessionManager, AgentOptions options, SessionStatusSupervisor? statusSupervisor = null, Core.Storage.SessionLogManager? logManager = null)
    {
        _sessionManager = sessionManager;
        _options = options;
        _statusSupervisor = statusSupervisor;
        _logManager = logManager;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[TurnSummaryCache] Start");

        _sessionManager.OnSessionCreated += OnSessionCreated;

        // Wire up existing sessions too (restored on startup).
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
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

        var summary = await SupervisorService.SummarizeTurnAsync(
            turn, lastAssistantText, session.RepoPath, _options.ClaudePath, ct);

        AddToCache(sessionId, summary);
        return summary;
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
                    var summary = await SupervisorService.SummarizeTurnAsync(
                        t, lastAssistantText, s.RepoPath, _options.ClaudePath);
                    AddToCache(s.Id, summary);
                    // Hand the fresh summary to the status supervisor (slow path).
                    _statusSupervisor?.ApplyTurnSummary(s, summary);
                    // Phase 5: persist the summary to disk so the supervisor's history
                    // survives Director restart and is replayable for "ask" queries.
                    _logManager?.WriteTurnSummary(s.Id, summary);
                    FileLog.Write($"[TurnSummaryCache] cached summary for {s.Id}: \"{(summary.Headline.Length > 80 ? summary.Headline[..80] + "..." : summary.Headline)}\"");
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

    private static string? TryReadLastAssistantText(Session session)
    {
        // Best-effort: pull from the JSONL via SummaryBuilder.  When the session
        // is brand new and the JSONL has not been linked yet, return null.
        try
        {
            if (string.IsNullOrEmpty(session.ClaudeSessionId)) return null;
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return null;
            var messages = StreamMessageParser.ParseFile(jsonl);
            var summary = SummaryBuilder.Build(messages);
            return summary.LastAssistantText;
        }
        catch
        {
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
        foreach (var s in _sessionManager.ListSessions())
        {
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnTurnCompleted -= h;
        }
    }
}
