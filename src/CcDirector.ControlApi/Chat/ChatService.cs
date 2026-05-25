using System.Diagnostics;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;
using CcDirector.Core.Voice.Services;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Chat;

/// <summary>
/// Phase 1 of the Director chat: relay one user message to a configured session,
/// wait for the agent to finish its turn, return the agent's reply.
///
/// The "configured session" is whichever session on this Director has a RepoPath
/// matching <see cref="AgentOptions.ChatSessionRepoPath"/>. If the caller passes
/// an explicit SessionId on the request, that overrides the configured default.
///
/// When the request sets Voice=true (the reply will be read aloud), the agent's
/// reply is also rewritten into an ear-friendly spoken form in ChatResponse.Summary.
/// Typed chat leaves Summary empty.
///
/// This service does NOT:
/// - Auto-create the session if it does not exist (returns "no_session_configured").
/// - Cache anything across calls (each /chat call is independent).
/// </summary>
public sealed class ChatService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    private readonly SessionManager _sessionManager;
    private readonly AgentOptions _options;

    // Lazily created the first time a voice request needs an ear-friendly rewrite,
    // so typed-chat calls never pay the CLI-availability check. Tests may inject one.
    private IResponseSummarizer? _summarizer;

    public ChatService(SessionManager sessionManager, AgentOptions options, IResponseSummarizer? summarizer = null)
    {
        _sessionManager = sessionManager;
        _options = options;
        _summarizer = summarizer;
    }

    public async Task<ChatResponse> HandleAsync(ChatRequest req, CancellationToken ct = default)
    {
        // A poll request carries no new message — it only inspects the session.
        // Only a real (sending) request requires Text.
        if (req is null || (!req.PollOnly && string.IsNullOrWhiteSpace(req.Text)))
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
                    ? "Set Chat.SessionRepoPath in appsettings.json to point at the repo you want the Director to talk to."
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

        // Poll request: read the session's current state + latest reply and
        // return immediately. The client drives the cadence with repeated polls,
        // so we never hold a request open for the length of the agent's turn.
        if (req.PollOnly)
            return await BuildPollResponseAsync(session, req.Voice, req.WantProgress, ct);

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

        // Ear-friendly spoken version, only when this reply will be read aloud
        // and the turn actually finished (a timeout reply is partial; the voice
        // client follows it via polling instead, so we don't summarise it here).
        var summary = (req.Voice && status == "ok")
            ? await BuildSpokenSummaryAsync(displayText, ct)
            : "";

        return new ChatResponse
        {
            SessionId = session.Id.ToString(),
            SessionName = DisplayName(session),
            Reply = reply,
            DisplayText = displayText,
            Summary = summary,
            ActivityState = finalState.ToString(),
            ElapsedMs = sw.ElapsedMilliseconds,
            Status = status,
            Error = timedOut ? $"Agent did not finish within {req.TimeoutMs} ms." : null,
        };
    }

    // ====== Internals =================================================================

    /// <summary>
    /// Snapshot the session's current activity state and latest assistant reply
    /// for a poll request. The turn is considered finished (status "ok") once the
    /// session is back at a stopping point (Idle / WaitingForInput / WaitingForPerm);
    /// otherwise it is still "working". These are the same stopping points the
    /// blocking send path waits for, so the two paths agree on "done".
    /// </summary>
    private async Task<ChatResponse> BuildPollResponseAsync(Session session, bool voice, bool wantProgress, CancellationToken ct)
    {
        var state = session.ActivityState;

        // Poll reads the clean JSONL transcript ONLY. A poll request has no
        // per-call cursor, so the buffer-diff fallback would have to dump the
        // whole terminal screen (TUI chrome, spinner frames, footer) as the
        // "reply" — never acceptable. When the session is not yet linked to a
        // JSONL file we return an empty reply truthfully rather than scrape.
        var fromJsonl = TryReadLastAssistantFromJsonl(session);
        var displayText = string.IsNullOrWhiteSpace(fromJsonl) ? "" : TrimChatBubble(fromJsonl);
        var reply = displayText;

        string status;
        if (state is ActivityState.Exited || session.Status is SessionStatus.Exited or SessionStatus.Failed)
            status = "session_not_found";
        else if (state is ActivityState.Idle or ActivityState.WaitingForInput or ActivityState.WaitingForPerm)
            status = "ok";
        else
            status = "working";

        // Only the terminal "ok" poll carries the final reply the client will
        // speak, so we summarise just that one (the intermediate "working" polls
        // skip it). Matches the blocking path's behaviour.
        var summary = (voice && status == "ok")
            ? await BuildSpokenSummaryAsync(displayText, ct)
            : "";

        // A progress note is only meaningful while the turn is still running and
        // only when the client explicitly asked for one (it does so about every
        // two minutes, not on every cheap poll, because it costs a Haiku call).
        var progressNote = (wantProgress && status == "working")
            ? await BuildProgressNoteAsync(session, ct)
            : "";

        return new ChatResponse
        {
            SessionId = session.Id.ToString(),
            SessionName = DisplayName(session),
            Reply = reply,
            DisplayText = displayText,
            Summary = summary,
            ProgressNote = progressNote,
            ActivityState = state.ToString(),
            ElapsedMs = 0,
            Status = status,
        };
    }

    /// <summary>
    /// Build a short spoken note of what the agent is doing right now from the
    /// tail of its terminal output. Returns empty string when there is nothing to
    /// read or the summarizer is unavailable — the client then stays silent for
    /// that window rather than reading raw terminal text aloud.
    /// </summary>
    private async Task<string> BuildProgressNoteAsync(Session session, CancellationToken ct)
    {
        var tail = ReadBufferTail(session, 4000);
        if (string.IsNullOrWhiteSpace(tail))
            return "";

        _summarizer ??= new ClaudeSummarizer();
        if (!_summarizer.IsAvailable)
        {
            FileLog.Write($"[ChatService] Progress note skipped: summarizer unavailable: {_summarizer.UnavailableReason}");
            return "";
        }

        var note = (await _summarizer.SummarizeProgressAsync(tail, ct)).Trim();
        FileLog.Write($"[ChatService] Progress note: tailLen={tail.Length}, noteLen={note.Length}");
        return note;
    }

    /// <summary>
    /// Read the cleaned tail (last <paramref name="maxBytes"/> bytes) of the
    /// session's terminal buffer. This is a read-only inspection — it never writes
    /// to or resizes the PTY — so it respects the wingman read-only invariant.
    /// </summary>
    private static string ReadBufferTail(Session session, int maxBytes)
    {
        var buf = session.Buffer;
        if (buf is null) return "";
        var total = buf.TotalBytesWritten;
        if (total <= 0) return "";
        var (bytes, _) = buf.GetWrittenSince(Math.Max(0, total - maxBytes));
        if (bytes.Length == 0) return "";
        return AnsiCleaner.Clean(bytes);
    }

    /// <summary>
    /// Produce the ear-friendly spoken version of a reply via the haiku-backed
    /// summarizer. Returns empty string when there is nothing to say or the
    /// summarizer is unavailable; the caller decides what to speak in that case.
    /// </summary>
    private async Task<string> BuildSpokenSummaryAsync(string replyText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(replyText))
            return "";

        _summarizer ??= new ClaudeSummarizer();
        if (!_summarizer.IsAvailable)
        {
            FileLog.Write($"[ChatService] Spoken summary skipped: summarizer unavailable: {_summarizer.UnavailableReason}");
            return "";
        }

        var spoken = (await _summarizer.SummarizeAsync(replyText, ct)).Trim();
        FileLog.Write($"[ChatService] Spoken summary: replyLen={replyText.Length}, spokenLen={spoken.Length}");
        return spoken;
    }

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
}
