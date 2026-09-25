using System.Linq;
using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Drivers;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the SESSION READ area of the tunnel command surface. It owns
/// the per-session read verbs (the reply body is the resource DTO). The spine seeded it with ONE exemplar -
/// <c>turns</c> - to fix the pattern; Worker R1 filled in the rest (snapshot, buffer, buffer-html, summary,
/// recap, turn-summaries, usage, context, history, github-urls, wingman-view, wingman-explain) by adding
/// each verb to <see cref="Verbs"/>, a case in <see cref="ExecuteAsync"/>, and a core method here.
///
/// Each core is extracted verbatim from that read's Director REST lambda. That REST route now calls this
/// SAME core, so the tunnel verb and the route cannot drift (the core is the single source of truth); Phase 1
/// deletes the routes and leaves the cores reached only over the tunnel. Every non-error branch a source
/// route returned as a 200 with a status string (a DOMAIN state, e.g. "no_session_id", "parse_error") stays
/// a <see cref="DirectorCommandStatus.Ok"/> here; only a bad id, a missing session, an unsupported capability,
/// or a genuine fault are error statuses. A preserved try/catch is kept ONLY where the source route had one,
/// so behaviour is byte-identical.
/// </summary>
internal sealed class SessionReadExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "turns",
        "snapshot",
        "buffer",
        "buffer-html",
        "screen-grid",
        "summary",
        "recap",
        "turn-summaries",
        "usage",
        "context",
        "history",
        "github-urls",
        "wingman-view",
        "wingman-explain",
        "handover",
        "handover-context",
        // Voice Delivery mission, phase 1: "what became of delivery id X?" - read from the Director's delivery record.
        DeliveryStateRequest.Verb,
    };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var sessionManager = context.SessionManager;
        var result = command.Verb switch
        {
            "turns" => Turns(sessionManager, command),
            "snapshot" => Snapshot(sessionManager, context.DirectorId, command),
            "buffer" => Buffer(sessionManager, command),
            "buffer-html" => BufferHtml(sessionManager, command),
            "screen-grid" => ScreenGrid(sessionManager, command),
            "summary" => Summary(sessionManager, context.DirectorId, command),
            "recap" => Recap(sessionManager, command),
            "turn-summaries" => TurnSummaries(context, command),
            "usage" => Usage(sessionManager, command),
            "context" => Context(sessionManager, command),
            "history" => History(sessionManager, command),
            "github-urls" => GithubUrls(sessionManager, command),
            "wingman-view" => WingmanView(context, command),
            "wingman-explain" => WingmanExplain(sessionManager, command),
            "handover" => Handover(sessionManager, context.DirectorId, context.Services?.DirectorVersion, command),
            "handover-context" => HandoverContext(sessionManager, context.DirectorId, command),
            DeliveryStateRequest.Verb => DeliveryStateOf(command, DeliveryRecord.Shared),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the session read area"),
        };
        return Task.FromResult(result);
    }

    /// <summary>
    /// The <c>turns</c> verb: the agent-agnostic conversation history widgets for a session. Mirrors the
    /// Director's <c>GET /sessions/{sid}/turns</c> lambda exactly - invalid id -&gt; BadRequest, missing
    /// session -&gt; NotFound - and returns a serialized <see cref="TurnsResponse"/> on success. Every
    /// non-error branch (unsupported agent, not-yet-linked, missing JSONL, a parse failure) is a 200 status
    /// with a <see cref="TurnsResponse.Status"/> string, exactly as the REST route returned; only a bad id
    /// or a missing session are true error statuses. The parse try/catch is preserved from the source
    /// because a parse failure is a DOMAIN state ("parse_error") the caller reads, not a fault to bubble.
    /// </summary>
    internal static DirectorCommandResult Turns(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var resp = new TurnsResponse
        {
            SessionId = command.SessionId,
            ClaudeSessionId = session.ClaudeSessionId,
        };

        if (session.AgentKind != AgentKind.ClaudeCode)
        {
            if (!SessionHistoryReader.IsSupported(session))
            {
                resp.Status = "unsupported";
                resp.Error = $"Agent {session.AgentKind} does not expose conversation history.";
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
            }

            // AN UNRESOLVED TRANSCRIPT IS A FAILED READ, NOT AN EMPTY CONVERSATION.
            //
            // This branch used to stamp "ok" unconditionally. But SessionHistoryReader.ReadAll returns
            // ConversationHistory.Empty whenever ResolveTranscriptPath returns null - which is exactly what
            // the per-agent locators (PiSessionLocator, CodexRolloutLocator, GrokSessionLocator) do when they
            // cannot find the session's transcript. So a Codex / Pi / Grok session whose transcript had not
            // been located reported a SUCCESSFUL read of an EMPTY conversation, and the one field that could
            // have told the caller otherwise said "ok".
            //
            // What that cost: voice narration reads this verb, sees no text widget, and records "there is
            // nothing to read aloud" - a NON-failure that is never retried. A Pi session on worktrees/oc-8403
            // sat silent for 48 minutes on 12 August with no error raised anywhere, and the roster showed it
            // "Preparing voice" the whole time. See issue #2561.
            //
            // Gemini is the deliberate exception and must NOT be caught by this: it persists no transcript at
            // all, so a null path is its normal state and its conversation is read from the session's own
            // terminal buffer (see SessionHistoryReader.ReadAll). A null path for Gemini is honest; for every
            // other agent here it means the transcript has not been found yet.
            var transcriptPath = SessionHistoryReader.ResolveTranscriptPath(session);
            if (transcriptPath is null && session.AgentKind != AgentKind.Gemini)
            {
                resp.Status = "no_transcript";
                resp.Error = $"No transcript has been located yet for this {session.AgentKind} session.";
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
            }

            var history = SessionHistoryReader.Read(session);
            resp.JsonlPath = transcriptPath;
            resp.LineCount = history.Messages.Count;
            resp.Widgets = ControlEndpoints.BuildTurnWidgetsFromHistory(history);

            // A RESOLVED PATH IS NOT A PROVEN READ EITHER, and this is the other half of the same defect
            // (found in review). Having a path only says where to look.
            //
            // It is at its sharpest for Copilot and OpenCode, whose "path" is a GLOBAL SQLite store rather
            // than a per-session file: ResolveTranscriptPath hands back the store as soon as it exists,
            // which says nothing about whether it holds a row for THIS session - and both readers answer an
            // empty history for a store with no repository match AND for a database error they caught. A Pi,
            // Codex or Grok transcript that resolves but holds no readable message lands in the same place.
            //
            // Stamping "ok" on any of those recreates exactly the false success this change exists to
            // remove: the voice service would see no text, record the never-retried "nothing to read aloud",
            // and the session would go silent with nothing raised. So an empty conversation gets its OWN
            // status. It is deliberately NOT an error - a session that has genuinely not spoken yet is the
            // ordinary case, and this status is retryable rather than terminal, because the very next turn
            // makes it non-empty.
            if (history.Messages.Count == 0)
            {
                resp.Status = "empty_history";
                resp.Error = $"No conversation was read for this {session.AgentKind} session"
                             + (transcriptPath is null ? "." : $" from {transcriptPath}.");
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
            }

            resp.Status = "ok";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }

        if (string.IsNullOrEmpty(session.ClaudeSessionId))
        {
            resp.Status = "no_session_id";
            resp.Error = "Session has not been linked to a Claude session id yet.";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }

        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            resp.JsonlPath = jsonl;

            if (!File.Exists(jsonl))
            {
                resp.Status = "no_jsonl";
                resp.Error = $"JSONL file not found at {jsonl}";
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
            }

            var messages = StreamMessageParser.ParseFile(jsonl);
            resp.LineCount = messages.Count;
            resp.Widgets = WidgetBuilder.BuildFromMessages(messages);

            // The SAME rule as the branch above, and it belongs here for the same reason (found in review:
            // the first version applied it only to the non-Claude branch, leaving Claude Code - the agent
            // this runs for most often - with exactly the false-success shape the change exists to remove).
            // A transcript that exists but parses to nothing is not a conversation that was read; it is a
            // read that produced none. Retryable, not terminal: the next turn writes into this same file.
            if (messages.Count == 0)
            {
                resp.Status = "empty_history";
                resp.Error = $"No conversation was read for this session from {jsonl}.";
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
            }

            resp.Status = "ok";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionReadExecutor] turns FAILED: {ex.Message}");
            resp.Status = "parse_error";
            resp.Error = ex.Message;
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }
    }

    /// <summary>
    /// The <c>snapshot</c> verb: the full session record. Mirrors the Director's <c>GET /sessions/{sid}</c>
    /// lambda - invalid id -&gt; BadRequest, missing session -&gt; NotFound - and returns the session mapped
    /// through the SAME plain <see cref="ControlEndpoints.Map"/> the stream snapshot uses (the Gateway stamps
    /// machine/user/tailnet identity onto pushed rows during aggregation). The Director's own REST endpoint
    /// re-maps with its identity-stamped mapper for its local response, keeping that path byte-identical -
    /// exactly as the <c>patch</c> verb already does.
    /// </summary>
    internal static DirectorCommandResult Snapshot(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>
    /// The <c>buffer</c> verb: the session's terminal buffer as text. Mirrors the Director's
    /// <c>GET /sessions/{sid}/buffer</c> lambda - invalid id -&gt; BadRequest, missing session -&gt; NotFound.
    /// The route's three query-string arguments (<c>lines</c>, <c>raw</c>, <c>since</c>) ride in the command
    /// payload as a <see cref="BufferRequest"/>. A session with no buffer returns an empty
    /// <see cref="BufferResponse"/> (a 200), exactly as before.
    /// </summary>
    internal static DirectorCommandResult Buffer(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<BufferRequest>(command.PayloadJson);
        var lines = request?.Lines;
        var raw = request?.Raw;
        var since = request?.Since;

        var buffer = session.Buffer;
        if (buffer is null)
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new BufferResponse { SessionId = command.SessionId }));

        byte[] bytes;
        long newCursor;
        if (since is long pos && pos >= 0)
        {
            var (data, np) = buffer.GetWrittenSince(pos);
            bytes = data;
            newCursor = np;
        }
        else
        {
            bytes = buffer.DumpAll();
            newCursor = buffer.TotalBytesWritten;
        }

        string text;
        if (raw == true)
            text = Encoding.UTF8.GetString(bytes);
        else
        {
            text = AnsiCleaner.Clean(bytes);
            if (lines is int n && n > 0)
                text = AnsiCleaner.LastLines(text, n);
        }

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new BufferResponse
        {
            SessionId = command.SessionId,
            TotalBytes = buffer.TotalBytesWritten,
            NewCursor = newCursor,
            Text = text,
        }));
    }

    /// <summary>
    /// The <c>buffer-html</c> verb: the styled HTML terminal snapshot (scrollback + live grid). Mirrors the
    /// Director's <c>GET /sessions/{sid}/buffer/html</c> lambda - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound - and returns a <see cref="BufferHtmlResponse"/> naming the exact anonymous object the
    /// route returned (including the concatenated <c>html</c> back-compat field).
    /// </summary>
    internal static DirectorCommandResult BufferHtml(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var (scrollbackHtml, gridHtml, scrollbackCount) = session.GetHtmlSnapshotSplit();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new BufferHtmlResponse
        {
            SessionId = command.SessionId,
            TotalBytes = session.Buffer?.TotalBytesWritten ?? 0,
            ScrollbackHtml = scrollbackHtml,
            GridHtml = gridHtml,
            ScrollbackCount = scrollbackCount,
            // Backward-compat: existing callers read .html as the concatenated stream.
            Html = scrollbackHtml + gridHtml,
        }));
    }

    /// <summary>
    /// The <c>screen-grid</c> verb (issue #1777): the RESOLVED live terminal screen grid for a session -
    /// the on-screen rows exactly as the emulator resolves them right now, the live cursor cell, and whether
    /// the agent has the terminal in the alternate screen buffer. Invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound. Additive (it does not touch the existing <c>buffer</c> verb); backed by
    /// <see cref="Session.SnapshotScreenRowsWithCursor"/>, which reads the parser's ACTIVE grid, so it is
    /// alternate-screen-correct - it sees a full-screen picker the scrollback-based <c>buffer</c> verb cannot.
    /// An Embedded session with no server-side parser returns an empty grid with <c>HasGrid = false</c>, which
    /// tells the caller the screen is unreadable (fail closed) rather than "not a menu".
    /// </summary>
    internal static DirectorCommandResult ScreenGrid(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // Rows + cursor + cursor VISIBILITY + alternate-screen flag from ONE coherent snapshot (issue #1777,
        // finding 8): the classifier relies on the flags describing the same frame as the rows. Cursor
        // visibility is the discriminator between a composer (visible) and a drawn Ink menu (hidden).
        var (rows, cursorRow, cursorCol, cursorVisible, isAlternateScreen) = session.SnapshotLiveScreen();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new ScreenGridResponse
        {
            SessionId = command.SessionId,
            Rows = rows.ToList(),
            CursorRow = cursorRow,
            CursorCol = cursorCol,
            CursorVisible = cursorVisible,
            IsAlternateScreen = isAlternateScreen,
            // A session with a real terminal parser yields a fixed-height grid (rows.Length > 0) even when the
            // screen is blank; only an Embedded session (no parser) yields an empty array - that is unreadable.
            HasGrid = rows.Length > 0,
        }));
    }

    /// <summary>
    /// The <c>summary</c> verb: the session's structured Claude-transcript summary. Mirrors the Director's
    /// <c>GET /sessions/{sid}/summary</c> lambda - invalid id -&gt; BadRequest, missing session -&gt; NotFound
    /// - and returns a serialized <see cref="SessionSummaryDto"/>. The not-yet-linked and parse-failure
    /// branches are 200 statuses with a status string (DOMAIN states), and the parse try/catch is preserved
    /// from the source for the same reason as <c>turns</c>.
    /// </summary>
    internal static DirectorCommandResult Summary(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var dto = new SessionSummaryDto
        {
            SessionId = command.SessionId,
            DirectorId = directorId,
            Agent = session.AgentKind.ToString(),
            RepoPath = session.RepoPath,
            ActivityState = session.ActivityState.ToString(),
            CreatedAt = session.CreatedAt.UtcDateTime,
        };

        if (string.IsNullOrEmpty(session.ClaudeSessionId))
        {
            dto.Status = "no_session_id";
            dto.Error = "Session has not been linked to a Claude session id yet.";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(dto));
        }

        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl))
            {
                dto.Status = "no_jsonl";
                dto.Error = $"JSONL file not found at {jsonl}";
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(dto));
            }

            var messages = StreamMessageParser.ParseFile(jsonl);
            var summary = SummaryBuilder.Build(messages);
            // Fold the structural fields we already filled into the freshly built one.
            summary.SessionId = command.SessionId;
            summary.DirectorId = directorId;
            summary.Agent = dto.Agent;
            summary.RepoPath = dto.RepoPath;
            summary.ActivityState = dto.ActivityState;
            summary.CreatedAt = dto.CreatedAt;
            summary.Status = "ok";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(summary));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionReadExecutor] summary FAILED: {ex.Message}");
            dto.Status = "parse_error";
            dto.Error = ex.Message;
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(dto));
        }
    }

    /// <summary>
    /// The <c>recap</c> verb: whatever recap is currently cached (never a generation - GET stays cheap and
    /// never spends). Mirrors the Director's <c>GET /sessions/{sid}/recap</c> lambda - invalid id -&gt;
    /// BadRequest, missing session -&gt; NotFound - and always returns a 200 <see cref="RecapResponse"/>
    /// (status "not_cached" when nothing is cached yet).
    /// </summary>
    internal static DirectorCommandResult Recap(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var cached = RecapCache.TryGet(guid);
        var currentTurns = ControlEndpoints.ComputeTurnCount(session);

        if (cached is null)
        {
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                CurrentTurnCount = currentTurns,
                Status = "not_cached",
                Error = "No recap has been generated yet. POST to /sessions/{sid}/recap to create one.",
            }));
        }

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
        {
            SessionId = command.SessionId,
            Recap = cached.Recap,
            GeneratedAt = cached.GeneratedAt,
            AtTurnCount = cached.AtTurnCount,
            CurrentTurnCount = currentTurns,
            IsStale = currentTurns > cached.AtTurnCount,
            Model = cached.Model,
            ElapsedMs = cached.ElapsedMs,
            Status = "ok",
        }));
    }

    /// <summary>
    /// The <c>turn-summaries</c> verb: the per-completed-turn structured summaries the wingman produced.
    /// Mirrors the Director's <c>GET /sessions/{sid}/turn-summaries</c> lambda - invalid id -&gt; BadRequest,
    /// missing session -&gt; NotFound - and returns a <see cref="TurnSummariesResponse"/>. The summaries come
    /// from the Director-local <see cref="TurnSummaryCache"/> via the command services; an absent cache
    /// yields an empty list, exactly as the route's null-conditional did.
    /// </summary>
    internal static DirectorCommandResult TurnSummaries(SessionCommandContext context, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        if (context.SessionManager.GetSession(guid) is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var list = context.Services?.TurnSummaryCache?.GetForSession(guid).ToList() ?? new List<TurnSummary>();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new TurnSummariesResponse
        {
            SessionId = command.SessionId,
            Summaries = list,
        }));
    }

    /// <summary>
    /// The <c>usage</c> verb: the session's token usage, computed from its Claude JSONL transcript. Mirrors
    /// the Director's <c>GET /sessions/{sid}/usage</c> lambda - invalid id -&gt; BadRequest; missing session,
    /// no linked Claude session id, or no transcript file -&gt; NotFound; a compute fault -&gt; Error (the
    /// route's 500). The transcript-path 404 folds the path into the error message (the route surfaced it as
    /// a separate <c>path</c> field). The compute try/catch is preserved from the source.
    /// </summary>
    internal static DirectorCommandResult Usage(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");
        if (string.IsNullOrEmpty(session.ClaudeSessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session has no claude session id yet");

        var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
        if (!File.Exists(jsonl))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, $"transcript not found: {jsonl}");

        try
        {
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(SessionTokenUsage.ComputeFromFile(jsonl, command.SessionId)));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionReadExecutor] usage FAILED: sid={command.SessionId} {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, $"usage computation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>context</c> verb: how full the session's context window is right now. Mirrors the Director's
    /// <c>GET /sessions/{sid}/context</c> lambda - invalid id -&gt; BadRequest; missing session, an agent
    /// without the ContextUsage capability, or no completed turn yet -&gt; NotFound; a read fault -&gt; Error
    /// (the route's 500). The read try/catch is preserved from the source.
    /// </summary>
    internal static DirectorCommandResult Context(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var driver = session.Driver;
        if (!driver.Capabilities.HasFlag(DriverCapabilities.ContextUsage))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, $"agent {driver.Kind} does not report context usage");

        // No agent-session-id guard here: only Claude carries a preassigned ClaudeSessionId. Codex and pi
        // have none - they locate their rollout/session by repo path - so requiring it would make the gauge
        // permanently dark for them. A driver that cannot resolve a transcript yet returns null (handled as
        // NotFound below).
        try
        {
            // EffectiveLaunchArgs (the merged launch line) carries the launched --model even when it came
            // from the configured default; ClaudeArgs alone is null in that case (#803).
            var launchArgs = session.EffectiveLaunchArgs ?? session.ClaudeArgs;
            var context = driver.ReadContextUsage(session.ClaudeSessionId ?? "", session.RepoPath, launchArgs);
            if (context is null)
                return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no context usage yet (no completed turn)");
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(context));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionReadExecutor] context FAILED: sid={command.SessionId} {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, $"context usage read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>history</c> verb: the parsed, agent-agnostic conversation history for a session. Mirrors the
    /// Director's <c>GET /sessions/{sid}/history</c> lambda - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound, a read fault -&gt; Error (the route's 500) - and returns a serialized
    /// <see cref="SessionHistoryDto"/> built by the SAME <see cref="SessionHistoryEndpoint.BuildHistory"/>
    /// pure mapper. The read try/catch is preserved from the source.
    /// </summary>
    internal static DirectorCommandResult History(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        try
        {
            var dto = SessionHistoryEndpoint.BuildHistory(session, command.SessionId);
            FileLog.Write($"[SessionReadExecutor] history: sid={command.SessionId} agent={dto.Agent} status={dto.Status} messages={dto.Messages.Count} state={dto.HistoryState ?? "(none)"}");
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(dto));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionReadExecutor] history FAILED: sid={command.SessionId} {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, $"history read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>github-urls</c> verb: the repo's GitHub "new issue" URL, resolved from its origin remote.
    /// Mirrors the Director's <c>GET /sessions/{sid}/github-urls</c> lambda - invalid id -&gt; BadRequest,
    /// missing session -&gt; NotFound - and returns a <see cref="GithubUrlsResponse"/>. A repo with no GitHub
    /// origin raises <see cref="InvalidOperationException"/>, surfaced as a Conflict (the route's 409); this
    /// catch is preserved from the source because it is a DOMAIN state (no origin), not a fault to bubble.
    /// </summary>
    internal static DirectorCommandResult GithubUrls(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        FileLog.Write($"[SessionReadExecutor] github-urls: sid={guid} repo={session.RepoPath}");
        try
        {
            var url = GitHubUrls.BuildNewIssueUrl(session.RepoPath);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new GithubUrlsResponse { NewIssueUrl = url }));
        }
        catch (InvalidOperationException ex)
        {
            FileLog.Write($"[SessionReadExecutor] github-urls FAILED: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
    }

    /// <summary>
    /// The <c>wingman-view</c> verb: observability into the wingman - current color and reason, a timestamped
    /// log of recent decisions and actions, the latest turn summary, and the goal state. Mirrors the
    /// Director's <c>GET /sessions/{sid}/wingman</c> lambda - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound - and returns a <see cref="WingmanViewDto"/>. The latest turn summary comes from the
    /// Director-local <see cref="TurnSummaryCache"/> via the command services (null-safe, as the route was).
    /// </summary>
    internal static DirectorCommandResult WingmanView(SessionCommandContext context, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = context.SessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var events = session.RecentWingmanEvents;
        var latestSummary = context.Services?.TurnSummaryCache?.GetForSession(session.Id).LastOrDefault();

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new WingmanViewDto
        {
            SessionId = session.Id.ToString(),
            Name = session.CustomName,
            CurrentColor = session.StatusColor,
            CurrentReason = session.LastStatusReason,
            Since = events.Count > 0 ? events[0].At : (DateTime?)null,
            Events = events.Select(e => new WingmanEventDto
            {
                At = e.At,
                OldColor = e.OldColor,
                NewColor = e.NewColor,
                Reason = e.Reason,
                Llm = e.Llm,
            }).ToList(),
            Actions = session.RecentWingmanActions.Select(a => new WingmanActionDto
            {
                At = a.At,
                Action = a.Action,
                Detail = a.Detail,
                Reason = a.Reason,
            }).ToList(),
            LatestTurnSummary = latestSummary,
            Goal = session.WingmanGoal,
            GoalSetAt = session.WingmanGoalSetAt,
            GoalState = session.WingmanGoalState,
            GoalReason = session.WingmanGoalReason,
            GoalEvaluatedAt = session.WingmanGoalEvaluatedAt,
            LastUserPrompt = session.LastUserPrompt,
            LastUserPromptAt = session.LastUserPromptAt,
        }));
    }

    /// <summary>
    /// The <c>wingman-explain</c> verb: the proactively-cached mobile briefing for a session (no LLM call, so
    /// a phone renders it instantly). Mirrors the Director's <c>GET /sessions/{sid}/wingman/explain</c> lambda
    /// - invalid id -&gt; BadRequest, missing session -&gt; NotFound - and returns a
    /// <see cref="WingmanExplainResponse"/> naming the exact anonymous object the route returned. Every field
    /// is served from the session's cached explain state.
    /// </summary>
    internal static DirectorCommandResult WingmanExplain(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new WingmanExplainResponse
        {
            MobileMode = session.MobileMode,
            Text = session.CachedExplainText,
            At = session.CachedExplainAt,
            Model = session.CachedExplainModel,
            QuickReplies = session.CachedQuickReplies,
            Headline = session.CachedExplainHeadline,
            WhatHappened = session.CachedExplainWhatHappened,
            LongDescription = session.CachedExplainLongDescription,
            WhatClaudeWants = session.CachedExplainWhatClaudeWants,
            ClaudeVerbatim = session.CachedExplainClaudeVerbatim,
            Say = session.CachedExplainSay,
        }));
    }

    /// <summary>
    /// The <c>handover</c> verb (issue #1214): the small identity/locate block the desktop app's "Copy
    /// Handover Info" menu item shows for a session (name, session id, repo, director id, machine, version).
    /// Mirrors the Director's <c>GET /sessions/{sid}/handover</c> lambda - invalid id -&gt; BadRequest,
    /// missing session -&gt; NotFound - and returns a serialized <see cref="HandoverInfoDto"/>. It is a pure
    /// read of the live session record: no transcript parsing, no I/O. The Director version - the one
    /// dependency the tunnel command surface did not carry - rides in
    /// <see cref="SessionCommandServices.DirectorVersion"/> and is stamped exactly as the REST route stamped
    /// <c>ControlApiHost._version</c>, so both paths serve an identical block. Deliberately does NOT include
    /// this Director's Control API endpoint (issue #1214: the browser must never learn a Director address).
    /// </summary>
    internal static DirectorCommandResult Handover(SessionManager sessionManager, string directorId, string? version, DirectorCommand command)
    {
        FileLog.Write($"[SessionReadExecutor] handover: sid={command.SessionId}");
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
        {
            FileLog.Write($"[SessionReadExecutor] handover: session not found sid={command.SessionId}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");
        }

        // Issue #800: the display name goes through the single composer so it is never the bare
        // folder name (legacy sessions with no CustomName get folder + type + disambiguator).
        var name = SessionName.DisplayName(session.CustomName,
            SessionName.FolderName(session.RepoPath),
            SessionName.Disambiguator(session.Id));

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new HandoverInfoDto
        {
            SessionId = command.SessionId,
            DisplayName = name,
            RepoPath = session.RepoPath,
            DirectorId = directorId,
            MachineName = Environment.MachineName,
            Version = version ?? string.Empty,
        }));
    }

    /// <summary>
    /// The <c>handover-context</c> verb: the plain-text prompt that would be sent to a target session on a
    /// handover. Mirrors the Director's <c>GET /sessions/{sid}/handover-context</c> lambda verbatim - invalid id
    /// -&gt; BadRequest, missing session -&gt; NotFound - building the same <see cref="SessionSummaryDto"/> and
    /// running it through <see cref="SummaryBuilder.FormatAsHandoverPrompt"/> with the optional extra context.
    /// The text is wrapped in a <see cref="HandoverContextResponse"/> so the tunnel body is valid JSON; the
    /// re-pointed REST route unwraps it back to <c>text/plain</c>. Gateway Cleanup mission: the cross-Director
    /// handover reads this over the tunnel before spawning the continuation on the target Director.
    /// </summary>
    internal static DirectorCommandResult HandoverContext(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        FileLog.Write($"[SessionReadExecutor] handover-context: sid={command.SessionId}");
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var extraContext = SessionCommandExecutor.Deserialize<HandoverContextRequest>(command.PayloadJson)?.ExtraContext;

        SessionSummaryDto summary;
        if (string.IsNullOrEmpty(session.ClaudeSessionId))
        {
            summary = new SessionSummaryDto
            {
                SessionId = command.SessionId, DirectorId = directorId,
                Agent = session.AgentKind.ToString(),
                RepoPath = session.RepoPath,
                ActivityState = session.ActivityState.ToString(),
                CreatedAt = session.CreatedAt.UtcDateTime,
            };
        }
        else
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            summary = File.Exists(jsonl)
                ? SummaryBuilder.Build(StreamMessageParser.ParseFile(jsonl))
                : new SessionSummaryDto();
            summary.SessionId = command.SessionId;
            summary.DirectorId = directorId;
            summary.Agent = session.AgentKind.ToString();
            summary.RepoPath = session.RepoPath;
            summary.ActivityState = session.ActivityState.ToString();
            summary.CreatedAt = session.CreatedAt.UtcDateTime;
        }

        var text = SummaryBuilder.FormatAsHandoverPrompt(summary, extraContext);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new HandoverContextResponse { Text = text }));
    }
    /// <summary>
    /// The <c>delivery-state</c> verb (Voice Delivery mission, phase 1): what became of one delivery id in the session
    /// named by the command. Answers from the Director's durable delivery record, so it holds across a restart:
    /// never seen -&gt; <see cref="DeliveryState.Unknown"/>. A record that cannot be read is a failure naming the file -
    /// never <see cref="DeliveryState.Unknown"/>, which would tell the Gateway a retry is safe when it may not be. The
    /// live session is not required: the record outlives it, and the question is about the past.
    /// </summary>
    internal static DirectorCommandResult DeliveryStateOf(DirectorCommand command, DeliveryRecord deliveries)
    {
        if (!Guid.TryParse(command.SessionId, out var sessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<DeliveryStateRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrWhiteSpace(request.DeliveryId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "deliveryId is required");

        DeliveryLookup lookup;
        try
        {
            lookup = deliveries.Read(sessionId, request.DeliveryId);
        }
        catch (DeliveryRecordUnreadableException ex)
        {
            FileLog.Write($"[SessionReadExecutor] DeliveryStateOf FAILED: session={sessionId}, deliveryId={request.DeliveryId}: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, ex.Message);
        }

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new DeliveryStateResponse
        {
            DeliveryId = request.DeliveryId,
            State = lookup.State,
            Reason = lookup.Reason,
            At = lookup.At,
        }));
    }
}
