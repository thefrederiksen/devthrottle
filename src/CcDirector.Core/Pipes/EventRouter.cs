using System.Collections.Concurrent;
using CcDirector.Core.Sessions;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Routes pipe messages to the correct Session by session_id.
/// Session ID discovery is handled by terminal content matching;
/// this router only delivers events to already-linked sessions.
/// Includes deduplication to handle the same event arriving via multiple transports
/// (named pipe + file watcher).
/// </summary>
public sealed class EventRouter : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly Action<string>? _log;

    // Deduplication: track recently seen messages to avoid processing the same event twice.
    // Key = "sessionId:hookEvent:toolUseId" or "sessionId:hookEvent:prompt_hash"
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentMessages = new();
    private Timer? _dedupeCleanupTimer;

    /// <summary>Raised for every message regardless of routing, for UI display.</summary>
    public event Action<PipeMessage>? OnRawMessage;

    public EventRouter(SessionManager sessionManager, Action<string>? log = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _log = log;

        // Clean up old dedup entries every 30 seconds
        _dedupeCleanupTimer = new Timer(CleanupDedupeCache, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Route a pipe message to its target session.</summary>
    public void Route(PipeMessage msg)
    {
        // Deduplicate: if we've seen this exact message recently, skip it
        var dedupeKey = BuildDedupeKey(msg);
        if (dedupeKey != null)
        {
            if (!_recentMessages.TryAdd(dedupeKey, DateTimeOffset.UtcNow))
            {
                // Already seen this message - skip silently
                return;
            }
        }

        OnRawMessage?.Invoke(msg);

        if (string.IsNullOrEmpty(msg.SessionId))
        {
            _log?.Invoke($"Received {msg.HookEventName} with no session_id, skipping.");
            return;
        }

        var session = _sessionManager.GetSessionByClaudeId(msg.SessionId);

        // Claude Code rotates its session id on /clear and /compact: SessionEnd for
        // the OLD id, then SessionStart(source=clear|compact) for the NEW id. The
        // NEW id isn't in our map, so without intervention every event for the new
        // conversation gets dropped. Relink the existing Director session here.
        //
        // We intentionally do NOT branch on source="resume" (the Claude id is
        // pre-assigned at launch and is already in the map) or source="startup"
        // (a genuinely new session that should not adopt anyone else's identity).
        if (session == null
            && msg.HookEventName == "SessionStart"
            && (msg.Source == "clear" || msg.Source == "compact")
            && !string.IsNullOrEmpty(msg.Cwd))
        {
            var orphan = _sessionManager.FindOrphanForReclaim(msg.Cwd);
            if (orphan != null)
            {
                _sessionManager.RelinkClaudeSession(orphan.Id, msg.SessionId);
                session = orphan;
                _log?.Invoke($"Relinked {orphan.Id} after /{msg.Source} -> Claude session {msg.SessionId[..Math.Min(8, msg.SessionId.Length)]}...");

                // /clear wipes the conversation: the pre-clear Wingman context (status
                // events, replay buffer, turn summaries) now describes a conversation
                // that no longer exists, so drop it. /compact keeps the conversation
                // going, so its context must be preserved -- reset on "clear" only.
                if (msg.Source == "clear")
                    _sessionManager.ResetSessionContextAfterClear(orphan.Id);
            }
        }

        if (session == null)
        {
            _log?.Invoke($"No linked session for Claude session {msg.SessionId[..Math.Min(8, msg.SessionId.Length)]}... (event={msg.HookEventName}), skipping.");
            return;
        }

        _log?.Invoke($"Routing {msg.HookEventName} to session {session.Id} (claude={msg.SessionId[..Math.Min(8, msg.SessionId.Length)]}...)");
        session.HandlePipeEvent(msg);
    }

    /// <summary>
    /// Build a deduplication key for a message.
    /// Uses session_id + event_name + a distinguishing field (tool_use_id, prompt hash, etc.)
    /// Returns null if the message can't be deduplicated (will always be processed).
    /// </summary>
    private static string? BuildDedupeKey(PipeMessage msg)
    {
        if (string.IsNullOrEmpty(msg.SessionId) || string.IsNullOrEmpty(msg.HookEventName))
            return null;

        var prefix = $"{msg.SessionId}:{msg.HookEventName}";

        // Use tool_use_id for tool events (most specific)
        if (!string.IsNullOrEmpty(msg.ToolUseId))
            return $"{prefix}:{msg.ToolUseId}";

        // Use agent_id for subagent events
        if (!string.IsNullOrEmpty(msg.AgentId))
            return $"{prefix}:{msg.AgentId}";

        // For Stop/SessionStart/SessionEnd/UserPromptSubmit - use prompt hash or just the event
        // These events don't have a unique ID, so we use a time-based key to prevent exact duplicates
        // arriving within a short window (same relay, different transport)
        if (!string.IsNullOrEmpty(msg.Prompt))
            return $"{prefix}:{msg.Prompt.GetHashCode()}";

        // For events without unique identifiers (Stop, SessionStart, SessionEnd, Notification),
        // use a coarse timestamp (1-second resolution) to deduplicate rapid arrivals
        var coarseTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{prefix}:{coarseTime}";
    }

    public void Dispose()
    {
        _dedupeCleanupTimer?.Dispose();
        _dedupeCleanupTimer = null;
        _recentMessages.Clear();
    }

    private void CleanupDedupeCache(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
        foreach (var kvp in _recentMessages)
        {
            if (kvp.Value < cutoff)
            {
                _recentMessages.TryRemove(kvp.Key, out _);
            }
        }
    }
}
