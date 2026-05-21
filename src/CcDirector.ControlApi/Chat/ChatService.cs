using System.Diagnostics;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Chat;

/// <summary>
/// Phase 1 of the Manager chat: relay one user message to a configured session,
/// wait for the agent to finish its turn, return the agent's reply.
///
/// The "configured session" is whichever session on this Director has a RepoPath
/// matching <see cref="AgentOptions.ChatSessionRepoPath"/>. If the caller passes
/// an explicit SessionId on the request, that overrides the configured default.
///
/// Phase 1 does NOT:
/// - Auto-create the session if it does not exist (returns "no_session_configured").
/// - Summarise the reply for TTS (Summary is empty; the chat layer or a later phase fills it).
/// - Cache anything across calls (each /chat call is independent).
/// </summary>
public sealed class ChatService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    private readonly SessionManager _sessionManager;
    private readonly AgentOptions _options;

    public ChatService(SessionManager sessionManager, AgentOptions options)
    {
        _sessionManager = sessionManager;
        _options = options;
    }

    /// <summary>True when a session matching <see cref="AgentOptions.ChatSessionRepoPath"/> is live.</summary>
    public bool IsAvailable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_options.ChatSessionRepoPath)) return false;
            return ResolveConfiguredSession() is not null;
        }
    }

    public ConfiguredSessionInfo? GetConfiguredSession()
    {
        var session = ResolveConfiguredSession();
        if (session is null) return null;
        return new ConfiguredSessionInfo(
            SessionId: session.Id.ToString(),
            SessionName: DisplayName(session),
            RepoPath: session.RepoPath,
            ActivityState: session.ActivityState.ToString());
    }

    public async Task<ChatResponse> HandleAsync(ChatRequest req, CancellationToken ct = default)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Text))
        {
            return new ChatResponse
            {
                Status = "send_failed",
                Error = "text is required",
            };
        }

        Session? session;
        if (!string.IsNullOrEmpty(req.SessionId))
        {
            if (!Guid.TryParse(req.SessionId, out var sid))
                return Bail("send_failed", "invalid SessionId format");
            session = _sessionManager.GetSession(sid);
            if (session is null)
                return Bail("session_not_found", $"no session with id {req.SessionId}");
        }
        else
        {
            session = ResolveConfiguredSession();
            if (session is null)
            {
                var hint = string.IsNullOrEmpty(_options.ChatSessionRepoPath)
                    ? "Set Chat.SessionRepoPath in appsettings.json to point at the repo you want the Manager to talk to."
                    : $"No live session has RepoPath matching '{_options.ChatSessionRepoPath}'. Start one in the Director app first.";
                return new ChatResponse
                {
                    Status = "no_session_configured",
                    Error = hint,
                };
            }
        }

        if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            return Bail("session_not_found", "session has exited");

        FileLog.Write($"[ChatService] HandleAsync: sid={session.Id}, len={req.Text.Length}");
        var sw = Stopwatch.StartNew();

        // Capture buffer position BEFORE sending — used only as a fallback when
        // the JSONL transcript is not available (brand-new session, not yet linked).
        var cursor = session.Buffer?.TotalBytesWritten ?? 0;

        try
        {
            await session.SendTextAsync(req.Text);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ChatService] SendTextAsync FAILED: {ex.Message}");
            return new ChatResponse
            {
                SessionId = session.Id.ToString(),
                SessionName = DisplayName(session),
                Status = "send_failed",
                Error = ex.Message,
                ElapsedMs = sw.ElapsedMilliseconds,
                ActivityState = session.ActivityState.ToString(),
            };
        }

        // Poll until the session returns to Idle / WaitingForInput (i.e. the agent finished its turn).
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, req.TimeoutMs));
        ActivityState finalState = session.ActivityState;
        bool timedOut = false;
        while (true)
        {
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { break; }
            finalState = session.ActivityState;
            if (finalState is ActivityState.Idle or ActivityState.WaitingForInput
                or ActivityState.WaitingForPerm or ActivityState.Exited)
                break;
            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            {
                finalState = ActivityState.Exited;
                break;
            }
            if (DateTime.UtcNow >= deadline)
            {
                timedOut = true;
                break;
            }
        }
        sw.Stop();

        var (reply, displayText) = ReadReply(session, cursor);

        var status = (timedOut, finalState) switch
        {
            (true, _) => "timeout",
            (_, ActivityState.Exited) => "session_not_found",
            _ => "ok",
        };

        return new ChatResponse
        {
            SessionId = session.Id.ToString(),
            SessionName = DisplayName(session),
            Reply = reply,
            DisplayText = displayText,
            Summary = "",
            ActivityState = finalState.ToString(),
            ElapsedMs = sw.ElapsedMilliseconds,
            Status = status,
            Error = timedOut ? $"Agent did not finish within {req.TimeoutMs} ms." : null,
        };
    }

    // ====== Internals =================================================================

    private Session? ResolveConfiguredSession()
    {
        if (string.IsNullOrWhiteSpace(_options.ChatSessionRepoPath)) return null;
        var target = NormalizePath(_options.ChatSessionRepoPath);
        foreach (var s in _sessionManager.ListSessions())
        {
            if (string.Equals(NormalizePath(s.RepoPath), target, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    private static (string raw, string display) ReadReply(Session session, long cursor)
    {
        // Primary path: read the last assistant text from the session's JSONL
        // transcript.  This is the agent's structured reply with no TUI noise
        // (no echoed prompts, no spinner frames, no status-bar text).
        var fromJsonl = TryReadLastAssistantFromJsonl(session);
        if (!string.IsNullOrWhiteSpace(fromJsonl))
        {
            var trimmed = TrimChatBubble(fromJsonl);
            return (trimmed, trimmed);
        }

        // Fallback: buffer-diff path.  Used only when the session has no linked
        // Claude session yet (brand-new, marker not detected) — in that case
        // there is no JSONL to read from.
        FileLog.Write($"[ChatService] ReadReply: JSONL empty/missing, falling back to buffer diff for {session.Id}");
        var buf = session.Buffer;
        if (buf is null) return (string.Empty, string.Empty);
        var (bytes, _) = buf.GetWrittenSince(cursor);
        if (bytes.Length == 0) return (string.Empty, string.Empty);

        var cleaned = AnsiCleaner.Clean(bytes);
        var display = TrimChatBubble(cleaned);
        return (cleaned, display);
    }

    /// <summary>
    /// Pull the most recent assistant text block from the session's JSONL file.
    /// Returns null/empty if the session is not yet linked to a JSONL file or
    /// the file has no assistant messages yet.
    /// </summary>
    private static string? TryReadLastAssistantFromJsonl(Session session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.ClaudeSessionId)) return null;
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return null;
            var messages = StreamMessageParser.ParseFile(jsonl);
            var summary = SummaryBuilder.Build(messages);
            return summary.LastAssistantText;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ChatService] TryReadLastAssistantFromJsonl FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Light trim for chat-bubble display: drop leading/trailing blank lines,
    /// collapse 3+ blank lines to 2.
    /// </summary>
    private static string TrimChatBubble(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new System.Text.StringBuilder(text.Length);
        int blanks = 0;
        bool started = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!started) continue;
                blanks++;
                continue;
            }
            if (blanks > 0)
            {
                sb.Append('\n');
                if (blanks > 1) sb.Append('\n');
            }
            else if (started)
            {
                sb.Append('\n');
            }
            sb.Append(line);
            started = true;
            blanks = 0;
        }
        return sb.ToString();
    }

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return string.Empty;
        return p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    private static string DisplayName(Session s)
    {
        if (!string.IsNullOrWhiteSpace(s.CustomName)) return s.CustomName!.Trim();
        var folder = Path.GetFileName(s.RepoPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(folder) ? s.Id.ToString()[..8] : folder;
    }

    private static ChatResponse Bail(string status, string error) => new()
    {
        Status = status,
        Error = error,
    };

    public sealed record ConfiguredSessionInfo(
        string SessionId,
        string SessionName,
        string RepoPath,
        string ActivityState);
}
