using System.Diagnostics;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the SESSION WRITE area of the tunnel command surface. It owns
/// the per-session and director-level write verbs. The ten verbs that already rode the tunnel before this
/// mission (prompt, interrupt, escape, hold, kill, patch, create, wingman-goal, set-role, attach-mission)
/// keep their extracted cores in <see cref="SessionCommandExecutor"/> - unchanged and already tested - and
/// this area simply routes to them, so there is ONE dispatch path with no legacy switch left behind. The
/// spine adds two new exemplar cores here to fix the pattern a worker copies: <c>resize</c> (a clean
/// representative write) and <c>terminal-input</c> (the unary keystroke write - NOT a stream verb, per
/// Architect ruling A: it needs neither the connection nor the stream registry). Worker W1 fills in the
/// remaining state writes; which of the ten legacy verbs later move their cores into this file is a refinement.
/// </summary>
internal sealed class SessionWriteExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        // The ten verbs that already rode the tunnel (cores in SessionCommandExecutor, routed here).
        "prompt", "interrupt", "escape", "hold", "kill", "patch", "create", "wingman-goal", "set-role", "attach-mission",
        // New spine exemplars owned here.
        "resize", "terminal-input",
        // Worker W1: the remaining Director session STATE writes, moved onto the tunnel dispatch. Each core
        // below reproduces its old REST lambda's guards and effect verbatim, so the REST path and the tunnel
        // verb share one core and cannot drift.
        "clear-context", "compact-context", "history-picker", "mobile-mode", "voice-mode", "wingman-enabled",
        "relink", "request-deletion", "cancel-deletion", "execute-action",
        // Gateway Cleanup Phase 0 (wave 3): the final deferred write verbs. handover-generate is
        // director-level (creates/targets a session from a source); wingman-ask and recap-generate are
        // per-session writes that call static services (no new dependency). Each core reproduces its old
        // REST lambda verbatim, so the REST route and the tunnel verb share one core and cannot drift.
        "handover-generate", "wingman-ask", "recap-generate",
        // Gateway Cleanup Phase 0 (Wave 4a): the remaining director-level orphan WRITE verbs. repo-delete
        // reads the live registry from the services (as repos-list does); the two interrupted-* verbs and
        // backfill-numbers touch static Director state. None targets a session (SessionId is ""), except that
        // interrupted-remove names a recoverable session inside a dead Director's journal via its payload.
        // Each core reproduces its old REST lambda verbatim, so the REST route and the tunnel verb share one
        // core and cannot drift.
        "repo-delete", "interrupted-dismiss", "interrupted-remove", "backfill-numbers",
        // Gateway Cleanup CUT RESTORATION (SB-4a): the director-level repository + saved-handover MANAGEMENT
        // writes migrated late (the cut deleted these un-migrated leftovers). repo-add/repo-rename touch the
        // live registry (as repo-delete does); handover-create/handover-delete write/remove a saved handover
        // document via HandoverScanner. None targets a session (SessionId is ""). Each core reproduces its old
        // REST lambda verbatim, so the (Phase-4-DEFERRED) REST path and the tunnel verb share one core and
        // cannot drift.
        "repo-add", "repo-rename", "handover-create", "handover-delete",
    };

    public async Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var sessionManager = context.SessionManager;
        return command.Verb switch
        {
            "prompt" => await SessionCommandExecutor.PromptAsync(sessionManager, command, context.Source),
            "interrupt" => await SessionCommandExecutor.InterruptAsync(sessionManager, command),
            "escape" => await SessionCommandExecutor.EscapeAsync(sessionManager, command),
            "hold" => SessionCommandExecutor.Hold(sessionManager, command),
            "kill" => await SessionCommandExecutor.KillAsync(sessionManager, command),
            "patch" => SessionCommandExecutor.Patch(sessionManager, context.DirectorId, command),
            "create" => SessionCommandExecutor.Create(sessionManager, context.DirectorId, command, context.Services),
            "wingman-goal" => SessionCommandExecutor.WingmanGoal(sessionManager, command, context.Services),
            "set-role" => SessionCommandExecutor.SetRole(sessionManager, context.DirectorId, command),
            "attach-mission" => SessionCommandExecutor.AttachMission(sessionManager, context.DirectorId, command, context.Services),
            "resize" => Resize(sessionManager, command),
            "terminal-input" => TerminalInput(sessionManager, command),
            "clear-context" => await ClearContextAsync(sessionManager, command, cancellationToken),
            "compact-context" => await CompactContextAsync(sessionManager, command, cancellationToken),
            "history-picker" => await HistoryPickerAsync(sessionManager, command),
            "mobile-mode" => MobileMode(sessionManager, command, context.Services),
            "voice-mode" => VoiceMode(sessionManager, command, context.Services),
            "wingman-enabled" => WingmanEnabled(sessionManager, command, context.Services),
            "relink" => Relink(sessionManager, command),
            "request-deletion" => RequestDeletion(sessionManager, command),
            "cancel-deletion" => CancelDeletion(sessionManager, command),
            "execute-action" => ExecuteAction(sessionManager, command),
            "handover-generate" => await HandoverGenerateAsync(sessionManager, context.DirectorId, command),
            "wingman-ask" => await WingmanAskAsync(context, command, cancellationToken),
            "recap-generate" => await RecapGenerateAsync(sessionManager, context.DirectorId, command, cancellationToken),
            "repo-delete" => RepoDelete(command, context.Services?.Repositories),
            "repo-add" => RepoAdd(command, context.Services?.Repositories),
            "repo-rename" => RepoRename(command, context.Services?.Repositories),
            "handover-create" => HandoverCreate(command),
            "handover-delete" => HandoverDelete(command),
            "interrupted-dismiss" => InterruptedDismiss(command),
            "interrupted-remove" => InterruptedRemove(command),
            "backfill-numbers" => BackfillNumbers(sessionManager),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the session write area"),
        };
    }

    /// <summary>
    /// The <c>resize</c> verb: set a session's PTY grid so a remote terminal can use the full window width.
    /// Mirrors the Director's <c>POST /sessions/{sid}/resize</c> lambda exactly - invalid id -&gt; BadRequest,
    /// non-positive cols/rows -&gt; BadRequest, missing session -&gt; NotFound - and returns the resulting grid.
    /// </summary>
    internal static DirectorCommandResult Resize(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<ResizeRequest>(command.PayloadJson);
        if (request is null || request.Cols <= 0 || request.Rows <= 0)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "cols and rows must be > 0");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.Resize((short)Math.Min(request.Cols, short.MaxValue), (short)Math.Min(request.Rows, short.MaxValue));
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new ResizeResponse
        {
            Accepted = true,
            Cols = session.CurrentCols,
            Rows = session.CurrentRows,
        }));
    }

    /// <summary>
    /// The <c>terminal-input</c> verb: forward a browser keystroke frame to the session's PTY, the same call
    /// the live terminal stream's input pump made (<see cref="Session.SendInput(byte[], InputOrigin?, SubmissionProvenance)"/>). The payload is a
    /// base64 byte blob so control bytes (arrows, Ctrl+C, Esc) survive the JSON envelope. Invalid id -&gt;
    /// BadRequest, missing/undecodable bytes -&gt; BadRequest, missing session -&gt; NotFound. This is a plain
    /// unary write; it is NOT a stream verb (Architect ruling A). The Gateway wires the browser's keystrokes
    /// to this verb in Phase 2; the spine adds the core and makes it dispatchable and testable now.
    /// </summary>
    internal static DirectorCommandResult TerminalInput(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<TerminalInputRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.Bytes))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "bytes are required");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Bytes);
        }
        catch (FormatException)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "bytes must be base64");
        }

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // Raw bytes from a browser terminal, relayed by the Gateway, which stamped the credential kind of the
        // person typing when their socket opened (source logging, 2026-09-05). A relay older than that field
        // sends none, and that is recorded as the unknown it is - never guessed here.
        session.SendInput(bytes, null, SubmissionProvenance.FromWire(request.Provenance, SubmissionRoutes.GatewayTerminal));
        return DirectorCommandResult.Success();
    }

    /// <summary>
    /// The <c>clear-context</c> verb: reset the conversation context in place (Claude's /clear, pi's /new) and,
    /// for transcript-capable drivers, re-link the Director to the NEW agent session id. Mirrors the Director's
    /// <c>POST /sessions/{sid}/clear-context</c> lambda exactly - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound, a driver with no reset (NotSupportedException) -&gt; Conflict - and returns the old and
    /// new agent session ids. The re-link runs only when the driver reported a new id.
    /// </summary>
    internal static async Task<DirectorCommandResult> ClearContextAsync(SessionManager sessionManager, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var oldId = session.ClaudeSessionId;
        string? newId;
        try
        {
            newId = await session.ClearContextAsync(cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }

        if (newId is not null)
            sessionManager.RelinkClaudeSession(guid, newId);

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new
        {
            accepted = true,
            oldAgentSessionId = oldId,
            newAgentSessionId = newId,
        }));
    }

    /// <summary>
    /// The <c>compact-context</c> verb (issue #2150): summarize a session's conversation IN PLACE and,
    /// when the payload carries one, send a follow-up prompt the moment the compaction finishes. This is
    /// the only way a full session gets moving again without a person at its keyboard.
    ///
    /// Unlike <see cref="ClearContextAsync"/> there is no re-link: compaction keeps the agent session id.
    /// Invalid id -&gt; BadRequest; missing session -&gt; NotFound; a driver that declares no compaction, or
    /// that cannot time a continuation (NotSupportedException) -&gt; Conflict; a compaction that never
    /// reports finishing (TimeoutException) -&gt; Timeout, naming what did not happen rather than letting
    /// the Gateway's outer wait mask it with "the Director did not answer".
    /// </summary>
    internal static async Task<DirectorCommandResult> CompactContextAsync(SessionManager sessionManager, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<CompactContextRequest>(command.PayloadJson);
        CompactContextOutcome outcome;
        try
        {
            outcome = await session.CompactContextAsync(request?.ContinuePrompt, cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        catch (TimeoutException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, ex.Message);
        }

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new CompactContextResponse
        {
            Submitted = outcome.Submitted,
            CompactionObserved = outcome.CompactionObserved,
            WaitedSeconds = Math.Round(outcome.WaitedSeconds, 1),
            Continued = outcome.Continued,
            Detail = outcome.Detail,
        }));
    }

    /// <summary>
    /// The <c>history-picker</c> verb: open the tool's in-terminal history picker (Claude's double-Esc). A
    /// visible-terminal feature. Mirrors the Director's <c>POST /sessions/{sid}/history-picker</c> lambda -
    /// invalid id -&gt; BadRequest, missing session -&gt; NotFound, a driver that has no picker
    /// (NotSupportedException) -&gt; Conflict - and returns <c>{ accepted = true }</c>.
    /// </summary>
    internal static async Task<DirectorCommandResult> HistoryPickerAsync(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        try
        {
            await session.ShowHistoryAsync();
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { accepted = true }));
    }

    /// <summary>
    /// The <c>mobile-mode</c> verb: toggle a session's mobile (text) view. Mirrors the Director's
    /// <c>POST /sessions/{sid}/mobile-mode</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - setting <see cref="Session.ViewMode"/> to Text (on) or Off, and, when turned on, warming the
    /// briefing cache via <see cref="ProactiveExplainService"/> (a side effect threaded through the services
    /// context, exactly as the wingman-goal core uses <c>TurnSummaryCache</c>). An empty/absent payload
    /// defaults to enabled (the common "watch this one" case), matching the REST endpoint. Returns the
    /// resulting mobile-mode flag.
    /// </summary>
    internal static DirectorCommandResult MobileMode(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<MobileModeRequest>(command.PayloadJson);
        var enabled = request?.Enabled ?? true;

        // Session (text) tab: Text when watching, Off when the phone navigates away. MobileMode is derived
        // from ViewMode, so proactive briefings behave identically; we only also distinguish Voice from Text.
        session.ViewMode = enabled ? MobileViewMode.Text : MobileViewMode.Off;
        FileLog.Write($"[SessionWriteExecutor] mobile-mode: session={guid} enabled={enabled} viewMode={session.ViewMode}");
        if (enabled) services?.ProactiveExplain?.TriggerBackgroundExplain(session);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { mobileMode = session.MobileMode }));
    }

    /// <summary>
    /// The <c>voice-mode</c> verb: toggle a session's in-car voice view. Mirrors the Director's
    /// <c>POST /sessions/{sid}/voice-mode</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - setting <see cref="Session.ViewMode"/> to Voice (on) or Text (off) and warming the briefing
    /// cache immediately (unconditionally, as the REST endpoint does) so the phone has something to speak.
    /// An empty/absent payload defaults to enabled. Returns the resulting voice-mode and mobile-mode flags.
    /// </summary>
    internal static DirectorCommandResult VoiceMode(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<VoiceModeRequest>(command.PayloadJson);
        var enabled = request?.Enabled ?? true;

        session.ViewMode = enabled ? MobileViewMode.Voice : MobileViewMode.Text;
        FileLog.Write($"[SessionWriteExecutor] voice-mode: session={guid} enabled={enabled} viewMode={session.ViewMode} (wingmanEnabled unchanged: {session.WingmanEnabled})");
        services?.ProactiveExplain?.TriggerBackgroundExplain(session);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { voiceMode = session.VoiceMode, mobileMode = session.MobileMode }));
    }

    /// <summary>
    /// The <c>wingman-enabled</c> verb: toggle the whole Wingman experience for a session. Mirrors the
    /// Director's <c>POST /sessions/{sid}/wingman-enabled</c> lambda - invalid id -&gt; BadRequest, missing
    /// session -&gt; NotFound - setting <see cref="Session.WingmanEnabled"/>. When flipped ON it warms the
    /// briefing cache; when flipped OFF it clears <see cref="Session.IsExplaining"/> so a yellow dot does not
    /// stick waiting on an in-flight briefing. An empty/absent payload defaults to enabled. Returns the flag.
    /// </summary>
    internal static DirectorCommandResult WingmanEnabled(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<WingmanEnabledRequest>(command.PayloadJson);
        var enabled = request?.Enabled ?? true;

        session.WingmanEnabled = enabled;
        FileLog.Write($"[SessionWriteExecutor] wingman-enabled: session={guid} enabled={enabled}");
        if (enabled)
            services?.ProactiveExplain?.TriggerBackgroundExplain(session);
        else
            session.IsExplaining = false;
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { wingmanEnabled = session.WingmanEnabled }));
    }

    /// <summary>
    /// The <c>relink</c> verb: re-point a Director session at a different Claude session id (recover continuity
    /// when the underlying id changed). Mirrors the Director's <c>POST /sessions/{sid}/relink</c> lambda -
    /// invalid id -&gt; BadRequest, absent/blank claudeSessionId -&gt; BadRequest, missing session -&gt;
    /// NotFound - and returns <c>{ accepted, claudeSessionId }</c>.
    /// </summary>
    internal static DirectorCommandResult Relink(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<RelinkRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrWhiteSpace(request.ClaudeSessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "claudeSessionId is required");

        if (sessionManager.GetSession(guid) is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        sessionManager.RelinkClaudeSession(guid, request.ClaudeSessionId);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { accepted = true, claudeSessionId = request.ClaudeSessionId }));
    }

    /// <summary>
    /// The <c>request-deletion</c> verb: flag a session for asynchronous removal by the owning Director's
    /// deletion reaper (the SAFE self-delete that does not kill the caller mid-request). Mirrors the Director's
    /// <c>POST /sessions/{sid}/request-deletion</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - and returns <c>{ pendingDeletion = true, requestedAt, reason }</c>. The body (a reason) is
    /// optional. The caller-identity boundary log stays on the REST endpoint (it reads the HTTP connection).
    /// </summary>
    internal static DirectorCommandResult RequestDeletion(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<SessionDeletionRequest>(command.PayloadJson);
        session.MarkForDeletion(request?.Reason);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new
        {
            pendingDeletion = true,
            requestedAt = session.DeletionRequestedAt,
            reason = session.DeletionReason,
        }));
    }

    /// <summary>
    /// The <c>cancel-deletion</c> verb: cancel a pending deletion during the grace window (operator changed
    /// their mind). Mirrors the Director's <c>DELETE /sessions/{sid}/request-deletion</c> lambda - invalid id
    /// -&gt; BadRequest, missing session -&gt; NotFound - and returns <c>{ pendingDeletion = false }</c>. As
    /// with request-deletion, the caller-identity boundary log stays on the REST endpoint.
    /// </summary>
    internal static DirectorCommandResult CancelDeletion(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.CancelDeletion();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { pendingDeletion = false }));
    }

    /// <summary>
    /// The <c>execute-action</c> verb (issue #327): the DUMB execute leg of the Wingman decide/execute split -
    /// carry out a fully-formed <see cref="WingmanAction"/> supplied by the caller, EXACTLY as passed, through
    /// the single write chokepoint <see cref="WingmanActionExecutor"/>, with zero decision logic and no LLM.
    /// Mirrors the Director's <c>POST /sessions/{sid}/execute-action</c> lambda - invalid id -&gt; BadRequest,
    /// missing session -&gt; NotFound, absent body -&gt; BadRequest. The executor's own outcome (ok / suppressed
    /// / session_gone / bad_request) rides back inside the serialized <see cref="WingmanActResult"/> so the
    /// REST layer maps it to the same HTTP codes it did before (session_gone -&gt; 410, bad_request -&gt; 400);
    /// the session_gone error text is filled here so both the REST and tunnel callers see it identically.
    /// </summary>
    internal static DirectorCommandResult ExecuteAction(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var action = SessionCommandExecutor.Deserialize<WingmanAction>(command.PayloadJson);
        if (action is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                "body is required: a WingmanAction JSON object (action none|type|send_keys|submit)");

        FileLog.Write($"[SessionWriteExecutor] execute-action: session={guid} action={action.Action}");
        var result = WingmanActionExecutor.Execute(session, action);
        FileLog.Write($"[SessionWriteExecutor] execute-action: session={guid} action={result.Action} performed={result.Performed} status={result.Status}");

        // A gone session gets its explanatory error filled here (the REST lambda used to set it just before
        // returning 410) so the executor outcome is complete regardless of which caller reads it.
        if (result.Status == WingmanActResult.StatusSessionGone)
            result.Error = $"session is {session.Status}; nothing was injected";

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(result));
    }

    /// <summary>
    /// The <c>handover-generate</c> verb (director-level, no target session on the command): the atomic
    /// "move the work" write. Mirrors the Director's <c>POST /handover</c> lambda verbatim - it reads the
    /// SOURCE session's context, delivers it to a TARGET (an existing session, or a brand-new one created in
    /// a repo), and optionally archives a markdown copy to the vault. The guards return the same statuses the
    /// route did: a bad/blank fromSessionId, the mutually-exclusive/exactly-one target rule, a bad
    /// toSessionId/toRepoPath, or an unknown agent -&gt; BadRequest; a missing source or target -&gt; NotFound;
    /// an Exited/Failed existing target -&gt; Conflict (the route's empty-body 409); a create fault -&gt; Error
    /// (the route's 500). On success it returns a <see cref="HandoverResponse"/> whose <c>TargetSession</c> is
    /// the plain <see cref="ControlEndpoints.Map"/> - the Director's own REST route re-maps it with its
    /// identity-stamped mapper for its 201, exactly as the create verb does. The new-session dispatch (wait for
    /// idle, then send) and the best-effort archive keep their fire-and-forget task and try/catch from the
    /// source, so behaviour is byte-identical.
    /// </summary>
    internal static async Task<DirectorCommandResult> HandoverGenerateAsync(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        var req = SessionCommandExecutor.Deserialize<HandoverRequest>(command.PayloadJson);
        FileLog.Write($"[SessionWriteExecutor] handover-generate: from={req?.FromSessionId} toSid={req?.ToSessionId} toRepo={req?.ToRepoPath}");

        if (req is null || string.IsNullOrEmpty(req.FromSessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "fromSessionId is required");
        if (string.IsNullOrEmpty(req.ToSessionId) && string.IsNullOrEmpty(req.ToRepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "exactly one of toSessionId or toRepoPath is required");
        if (!string.IsNullOrEmpty(req.ToSessionId) && !string.IsNullOrEmpty(req.ToRepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "toSessionId and toRepoPath are mutually exclusive");

        if (!Guid.TryParse(req.FromSessionId, out var fromGuid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid fromSessionId format");

        var source = sessionManager.GetSession(fromGuid);
        if (source is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "source session not found on this director");

        // 1) Build the context text
        SessionSummaryDto summary;
        if (string.IsNullOrEmpty(source.ClaudeSessionId))
        {
            summary = new SessionSummaryDto
            {
                SessionId = req.FromSessionId, DirectorId = directorId,
                Agent = source.AgentKind.ToString(),
                RepoPath = source.RepoPath,
                ActivityState = source.ActivityState.ToString(),
                CreatedAt = source.CreatedAt.UtcDateTime,
            };
        }
        else
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(source.ClaudeSessionId, source.RepoPath);
            summary = File.Exists(jsonl)
                ? SummaryBuilder.Build(StreamMessageParser.ParseFile(jsonl))
                : new SessionSummaryDto();
            summary.SessionId = req.FromSessionId;
            summary.DirectorId = directorId;
            summary.Agent = source.AgentKind.ToString();
            summary.RepoPath = source.RepoPath;
            summary.ActivityState = source.ActivityState.ToString();
            summary.CreatedAt = source.CreatedAt.UtcDateTime;
        }
        var contextText = SummaryBuilder.FormatAsHandoverPrompt(summary, req.ExtraContext);

        // 2) Find or create the target session
        Session target;
        if (!string.IsNullOrEmpty(req.ToSessionId))
        {
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid toSessionId format");
            var existing = sessionManager.GetSession(toGuid);
            if (existing is null)
                return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "target session not found on this director");
            if (existing.Status is SessionStatus.Exited or SessionStatus.Failed)
                return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "target session has exited");
            target = existing;
            await target.SendTextAsync(contextText, SubmissionProvenance.FrameworkText(), SendSource.Framework);
        }
        else
        {
            var repo = req.ToRepoPath!;
            if (!Directory.Exists(repo))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"toRepoPath does not exist: {repo}");
            if (!Enum.TryParse<AgentKind>(req.ToAgent, ignoreCase: true, out var kind))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown agent: {req.ToAgent}");

            if (!AgentPluginRegistry.Contains(kind))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"agent {kind} is not a built-in plugin target");
            // Issue #1050: launch the executable recorded on the configured agent entry, not the
            // per-type default in AgentOptions - on a machine whose agent was installed by the
            // onboarding wizard, only the entry knows where the binary is.
            var agent = AgentLaunchDefaults.CreateAgentForKind(kind, sessionManager.Options);

            try
            {
                // Session origin (devthrottle_internal issue #982): a handover's target. The SURFACE
                // is certain - this is the handover verb, not the command line and not a schedule - so
                // it is stated, and it matches what the Gateway's CROSS-Director handover records, so
                // the same act reads the same whichever leg it took. The KIND is the SOURCE's: the
                // target continues the source's work, so a session the owner started is still one he
                // started after it moves, and Your Throttle keeps counting it as his (2026-09-27).
                //
                // The SOURCE session is deliberately not recorded as the parent. ParentSessionId means
                // "the session that asked for this one"; in a handover the source is the session being
                // LEFT, and putting it here would make the lineage tree mean two things at once.
                // The FACTORY the Gateway settled for this handover (Factory Memory mission, phase 1), stamped in
                // the same pre-launch window as the origin. The Director does not decide it and does not check
                // it: whether the target inherits the source's membership depends on who asked, and only the
                // Gateway saw the credential. Null means none, which is what an unrelated session's handover
                // produces.
                target = sessionManager.CreateSession(repo, agent, userArgs: null, SessionBackendType.ConPty, resumeSessionId: null,
                    beforeLaunch: s =>
                    {
                        s.StampOrigin(new SessionOrigin(
                            SessionOriginKinds.Normalize(source.OriginKind) ?? SessionOriginKinds.Unknown,
                            SessionOriginSurfaces.Api));
                        s.StampFactory(req.Factory);
                    });
            }
            catch (Exception ex)
            {
                return DirectorCommandResult.Fail(DirectorCommandStatus.Error, "failed to create target session: " + ex.Message);
            }
            // SessionManager.CreateSession now fires OnSessionCreated itself, so no
            // explicit RaiseSessionCreated call is needed here.

            // Dispatch the context after the new session reaches Idle. Fire-and-forget;
            // we return the target DTO immediately so callers can navigate to it.
            var capturedTarget = target;
            var capturedText = contextText;
            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(30_000);
                while (DateTime.UtcNow < deadline)
                {
                    var st = capturedTarget.ActivityState;
                    if (st is ActivityState.Idle or ActivityState.WaitingForInput) break;
                    if (st is ActivityState.Exited) { FileLog.Write($"[SessionWriteExecutor] handover-generate target exited before idle, sid={capturedTarget.Id}"); return; }
                    await Task.Delay(500);
                }
                try { await capturedTarget.SendTextAsync(capturedText, SubmissionProvenance.FrameworkText(), SendSource.Framework); }
                catch (Exception ex) { FileLog.Write($"[SessionWriteExecutor] handover-generate dispatch FAILED: {ex.Message}"); }
            });
        }

        // 3) Optionally archive to vault
        string? archivedAt = null;
        if (req.ArchiveToVault)
        {
            try { archivedAt = HandoverArchive.Write(summary, contextText, target.Id.ToString()); }
            catch (Exception ex) { FileLog.Write($"[SessionWriteExecutor] handover-generate archive FAILED: {ex.Message}"); }
        }

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new HandoverResponse
        {
            Accepted = true,
            TargetSession = ControlEndpoints.Map(target, directorId),
            ContextSent = contextText,
            ArchivedAt = archivedAt,
        }));
    }

    /// <summary>
    /// The <c>wingman-ask</c> verb: the "Ask the Wingman" channel. Mirrors the Director's
    /// <c>POST /sessions/{sid}/wingman/ask</c> lambda - two behaviours, both on the strong model:
    /// <c>mode=explain</c> is a terse "what's happening" briefing over pre-built context; a free-text
    /// question opens a read-only full-power session over the whole terminal + repo that answers faithfully
    /// and reads content VERBATIM. Invalid id -&gt; BadRequest; a missing session -&gt; NotFound. The
    /// question-required guard is NOT an id/session error but the wingman's own <c>bad_request</c> outcome,
    /// so it rides back as a 200 <see cref="WingmanAskResult"/> (Status "bad_request"); the REST route maps
    /// that Status to its original 400 exactly as the execute-action verb maps its executor outcomes. The
    /// turn-summary cache (explain mode's context input) rides in the command services; no new dependency is
    /// introduced because the answer itself comes from the static <see cref="Core.Wingman.WingmanService"/>
    /// methods. Lifted as a plain unary verb (Architect option A); the slow-LLM invocation strategy is a
    /// Phase 2 decision, deliberately unchanged here.
    /// </summary>
    internal static async Task<DirectorCommandResult> WingmanAskAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var sessionManager = context.SessionManager;
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var req = SessionCommandExecutor.Deserialize<WingmanAskRequest>(command.PayloadJson);
        var explain = string.Equals(req?.Mode, "explain", StringComparison.OrdinalIgnoreCase);
        // Explain mode briefs the whole session and needs no user question; the free-text ask path still
        // requires one. This is the wingman's bad_request outcome, carried in the result (a 200 here, mapped
        // to a 400 at the REST boundary), NOT an id/session guard.
        if (req is null || (!explain && string.IsNullOrWhiteSpace(req.Question)))
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(
                new WingmanAskResult { Status = "bad_request", Error = "question is required" }));

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // Explain = the terse "what's happening" briefing (strong model over pre-built, length-capped
        // context). Unchanged.
        if (explain)
        {
            var explainCtx = await WingmanContextBuilder.BuildAsync(session, context.Services?.TurnSummaryCache, cancellationToken);
            var explainResult = await Core.Wingman.WingmanService.AskAboutSessionAsync(
                req.Question, explainCtx, sessionManager.Options.ClaudePath, cancellationToken, explain: true);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(explainResult));
        }

        // Any free-text question = the faithful "Ask the Wingman" channel: a read-only full-power session
        // over the WHOLE terminal + repo, on the strong model, that reads content VERBATIM instead of
        // summarizing.
        var fullTerminal = ControlEndpoints.ReadFullCleanedBuffer(session);
        var result = await Core.Wingman.WingmanService.AnswerViaSessionAsync(
            req.Question, fullTerminal, session.AgentKind.ToString(), session.RepoPath,
            sessionManager.Options.ClaudePath, cancellationToken);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(result));
    }

    /// <summary>
    /// The <c>recap-generate</c> verb: build (and cache) a fresh recap for a session by running the static
    /// <see cref="RecapGenerator.GenerateAsync"/> over a digest of the session's Claude transcript. Mirrors
    /// the Director's <c>POST /sessions/{sid}/recap</c> lambda - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound. The not-yet-linked, missing-transcript, and generation-failure branches are DOMAIN
    /// states the route returned as 200 <see cref="RecapResponse"/> bodies (status strings), so they stay
    /// <see cref="DirectorCommandStatus.Ok"/> here; the successful generation is also an Ok body whose
    /// <c>Status == "ok"</c>, which the REST route maps to its original 201 (a 200 for the domain-state
    /// bodies). The <c>model</c> query argument rides in the <see cref="RecapGenerateRequest"/> payload. A
    /// caller cancellation (the route's 499) is NOT caught here - it is a boundary concern that bubbles to
    /// the REST/stream boundary - while every other generation fault is preserved as the route's
    /// <c>generation_failed</c> 200, exactly as the source lambda's try/catch did.
    /// </summary>
    internal static async Task<DirectorCommandResult> RecapGenerateAsync(SessionManager sessionManager, string directorId, DirectorCommand command, CancellationToken cancellationToken)
    {
        FileLog.Write($"[SessionWriteExecutor] recap-generate: sid={command.SessionId}");
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<RecapGenerateRequest>(command.PayloadJson);
        var model = request?.Model;
        if (string.IsNullOrWhiteSpace(model))
            model = RecapGenerator.DefaultModel;

        if (string.IsNullOrEmpty(session.ClaudeSessionId))
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "no_session_id",
                Error = "Session has not been linked to a Claude session id yet.",
            }));

        var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
        if (!File.Exists(jsonl))
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "no_jsonl",
                Error = $"JSONL file not found at {jsonl}",
            }));

        SessionSummaryDto summary;
        string digest;
        int currentTurns;
        try
        {
            var messages = StreamMessageParser.ParseFile(jsonl);
            summary = SummaryBuilder.Build(messages);
            summary.SessionId = command.SessionId;
            summary.DirectorId = directorId;
            summary.Agent = session.AgentKind.ToString();
            summary.RepoPath = session.RepoPath;
            summary.ActivityState = session.ActivityState.ToString();
            summary.CreatedAt = session.CreatedAt.UtcDateTime;
            digest = SummaryBuilder.FormatAsHandoverPrompt(summary);
            currentTurns = summary.TurnCount;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionWriteExecutor] recap-generate digest build FAILED: {ex.Message}");
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "generation_failed",
                Error = "Failed to build session digest: " + ex.Message,
            }));
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var recapText = await RecapGenerator.GenerateAsync(
                digest, sessionManager.Options.ClaudePath, model, cancellationToken);
            sw.Stop();

            var entry = new RecapCache.Entry
            {
                Recap = recapText,
                GeneratedAt = DateTime.UtcNow,
                AtTurnCount = currentTurns,
                Model = model,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
            RecapCache.Set(guid, entry);

            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Recap = entry.Recap,
                GeneratedAt = entry.GeneratedAt,
                AtTurnCount = entry.AtTurnCount,
                CurrentTurnCount = currentTurns,
                IsStale = false,
                Model = entry.Model,
                ElapsedMs = entry.ElapsedMs,
                Status = "ok",
            }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[SessionWriteExecutor] recap-generate generation FAILED: {ex.Message}");
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "generation_failed",
                Error = ex.Message,
            }));
        }
    }

    /// <summary>
    /// The <c>repo-delete</c> verb (director-level, no session): remove a repository from the recent-repository
    /// registry (the New Session picker's list). Mirrors the Director's <c>DELETE /repos</c> lambda - a null /
    /// blank path -&gt; BadRequest, a null registry -&gt; a 200 <c>{ removed = false }</c> - and otherwise
    /// returns the registry's own <see cref="RepositoryRegistry.Remove"/> result. The live registry rides in
    /// the services, exactly as the <c>repos-list</c> read reads it; a null registry lists/removes nothing, as
    /// the REST route returned when no registry was wired.
    /// </summary>
    internal static DirectorCommandResult RepoDelete(DirectorCommand command, RepositoryRegistry? repositories)
    {
        var request = SessionCommandExecutor.Deserialize<RepoDeleteRequest>(command.PayloadJson);
        var path = request?.Path;
        FileLog.Write($"[SessionWriteExecutor] repo-delete: path={path}");
        if (string.IsNullOrWhiteSpace(path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path is required");
        if (repositories is null)
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RepoDeleteResponse { Removed = false }));

        var removed = repositories.Remove(path);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RepoDeleteResponse { Removed = removed }));
    }

    /// <summary>
    /// The <c>repo-add</c> verb (director-level, no session): register a repository explicitly in the
    /// recent-repository registry (no session needed). Mirrors the Director's <c>POST /repos</c> lambda - a
    /// blank path -&gt; BadRequest, a null registry -&gt; BadRequest, a directory that does not exist -&gt;
    /// BadRequest - otherwise adds the path (and applies the optional name) and returns
    /// <c>{ added, repo }</c> where <c>added</c> selects the route's 201 (newly added) vs 200 (already present).
    /// The live registry rides in the services, exactly as <c>repo-delete</c> reads it.
    /// </summary>
    internal static DirectorCommandResult RepoAdd(DirectorCommand command, RepositoryRegistry? repositories)
    {
        var request = SessionCommandExecutor.Deserialize<RepoAddRequest>(command.PayloadJson);
        FileLog.Write($"[SessionWriteExecutor] repo-add: path={request?.Path}, name=\"{request?.Name}\"");
        if (string.IsNullOrWhiteSpace(request?.Path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path is required");
        if (repositories is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repository registry not available");
        if (!Directory.Exists(request.Path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"directory not found: {request.Path}");

        var added = repositories.TryAdd(request.Path);
        if (!string.IsNullOrWhiteSpace(request.Name))
            repositories.Rename(request.Path, request.Name);

        var wanted = ControlEndpoints.NormalizeRepoPath(request.Path);
        var repo = repositories.Repositories.First(r => ControlEndpoints.NormalizeRepoPath(r.Path) == wanted);
        var dto = new RepositoryDto
        {
            Name = string.IsNullOrEmpty(repo.Name) ? Path.GetFileName(repo.Path.TrimEnd('\\', '/')) : repo.Name,
            Path = repo.Path,
            LastUsed = repo.LastUsed,
        };
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RepoAddResponse { Added = added, Repo = dto }));
    }

    /// <summary>
    /// The <c>repo-rename</c> verb (director-level, no session): rename a registered repository (the path is the
    /// identity). Mirrors the Director's <c>PATCH /repos</c> lambda - a blank path or blank name -&gt;
    /// BadRequest, a null registry -&gt; BadRequest, a path not in the registry -&gt; NotFound - otherwise
    /// renames and returns the resulting <see cref="RepositoryDto"/>.
    /// </summary>
    internal static DirectorCommandResult RepoRename(DirectorCommand command, RepositoryRegistry? repositories)
    {
        var request = SessionCommandExecutor.Deserialize<RepoRenameRequest>(command.PayloadJson);
        FileLog.Write($"[SessionWriteExecutor] repo-rename: path={request?.Path}, name=\"{request?.Name}\"");
        if (string.IsNullOrWhiteSpace(request?.Path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path is required");
        if (string.IsNullOrWhiteSpace(request.Name))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "name is required");
        if (repositories is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repository registry not available");

        if (!repositories.Rename(request.Path, request.Name))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "repository not registered");

        var wanted = ControlEndpoints.NormalizeRepoPath(request.Path);
        var repo = repositories.Repositories.First(r => ControlEndpoints.NormalizeRepoPath(r.Path) == wanted);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RepositoryDto
        {
            Name = repo.Name,
            Path = repo.Path,
            LastUsed = repo.LastUsed,
        }));
    }

    /// <summary>
    /// The <c>handover-create</c> verb (director-level, no session): write a standalone handover document to the
    /// vault handover folder so it shows up in the Handovers list (unlike <c>handover-generate</c>, no target
    /// session is involved). Mirrors the Director's <c>POST /handovers</c> lambda - a blank title or blank
    /// content -&gt; BadRequest - otherwise writes the document and returns the parsed <see cref="HandoverDto"/>
    /// (the route's 201; the Gateway route stamps the 201 on Ok).
    /// </summary>
    internal static DirectorCommandResult HandoverCreate(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<HandoverCreateRequest>(command.PayloadJson);
        FileLog.Write($"[SessionWriteExecutor] handover-create: title=\"{request?.Title}\"");
        if (string.IsNullOrWhiteSpace(request?.Title))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "title is required");
        if (string.IsNullOrWhiteSpace(request.Content))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "content is required");

        var path = HandoverScanner.WriteNew(request.Title, request.Content, request.RepoPaths, request.SessionName);
        var h = HandoverScanner.Parse(path);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new HandoverDto
        {
            Path = h.Path,
            Title = h.Title,
            DateDisplay = h.DateDisplay,
            DateUtc = h.DateUtc,
            RepoPath = h.RepoPath,
            RepoPaths = h.RepoPaths,
            SessionName = h.SessionName,
        }));
    }

    /// <summary>
    /// The <c>handover-delete</c> verb (director-level, no session): delete a saved handover document. Mirrors
    /// the Director's <c>DELETE /handovers</c> lambda - a blank path -&gt; BadRequest, a path outside the
    /// handover folder (<see cref="UnauthorizedAccessException"/>) -&gt; BadRequest, a missing file
    /// (<see cref="FileNotFoundException"/>) -&gt; NotFound - otherwise removes it and returns
    /// <c>{ removed = true }</c>.
    /// </summary>
    internal static DirectorCommandResult HandoverDelete(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<RepoDeleteRequest>(command.PayloadJson);
        var path = request?.Path;
        FileLog.Write($"[SessionWriteExecutor] handover-delete: path={path}");
        if (string.IsNullOrWhiteSpace(path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path is required");

        try
        {
            HandoverScanner.Delete(path);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new HandoverDeleteResponse { Removed = true }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, ex.Message);
        }
        catch (FileNotFoundException)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "handover not found");
        }
    }

    /// <summary>
    /// The <c>interrupted-dismiss</c> verb (director-level, no target session): dismiss one claimed dirty
    /// crash journal once its sessions are recovered or no longer wanted. Mirrors the Director's
    /// <c>DELETE /interrupted/{deadDirectorId}/{deadPid}</c> lambda - <see cref="DirectorCrashJournal.Dismiss"/>
    /// returning true -&gt; a 200 <c>{ dismissed = true }</c>, false -&gt; NotFound (the route's 404). The
    /// dead-Director id and process id ride in the payload. The caller-identity boundary log stays on the REST
    /// endpoint (it reads the HTTP connection).
    /// </summary>
    internal static DirectorCommandResult InterruptedDismiss(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<InterruptedDismissRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.DeadDirectorId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "deadDirectorId is required");

        FileLog.Write($"[SessionWriteExecutor] interrupted-dismiss: deadDirectorId={request.DeadDirectorId} deadPid={request.DeadPid}");
        var removed = DirectorCrashJournal.Dismiss(request.DeadDirectorId, request.DeadPid);
        return removed
            ? DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new InterruptedDismissResponse { Dismissed = true }))
            : DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such interrupted journal");
    }

    /// <summary>
    /// The <c>interrupted-remove</c> verb (director-level; names a recoverable session inside a dead Director's
    /// journal, issue #212 W4): remove ONE session from a claimed dirty journal after it has been restored, so
    /// the rest of the journal stays in the Interrupted sessions list. Mirrors the Director's
    /// <c>DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId}</c> lambda -
    /// <see cref="DirectorCrashJournal.RemoveSession"/> returning true -&gt; a 200 <c>{ removed = true }</c>,
    /// false -&gt; NotFound (the route's 404). The dead-Director id, process id, and the recoverable session id
    /// ride in the payload. The caller-identity boundary log stays on the REST endpoint.
    /// </summary>
    internal static DirectorCommandResult InterruptedRemove(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<InterruptedRemoveRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.DeadDirectorId) || string.IsNullOrEmpty(request.SessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "deadDirectorId and sessionId are required");

        FileLog.Write($"[SessionWriteExecutor] interrupted-remove: deadDirectorId={request.DeadDirectorId} deadPid={request.DeadPid} sessionId={request.SessionId}");
        var removed = DirectorCrashJournal.RemoveSession(request.DeadDirectorId, request.DeadPid, request.SessionId);
        return removed
            ? DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new InterruptedRemoveResponse { Removed = true }))
            : DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such interrupted session");
    }

    /// <summary>
    /// The <c>backfill-numbers</c> verb (director-level, no session; issue #846): trigger
    /// <see cref="SessionManager.BackfillNumbers"/> on a RUNNING Director - no restart - so an operator can
    /// number sessions that predate #820 (or were restored without a number). Mirrors the Director's
    /// <c>POST /admin/backfill-numbers</c> lambda - always a 200 - and returns the count newly numbered.
    /// Idempotent: a second call returns 0. The caller-identity boundary log stays on the REST endpoint.
    /// </summary>
    internal static DirectorCommandResult BackfillNumbers(SessionManager sessionManager)
    {
        FileLog.Write("[SessionWriteExecutor] backfill-numbers");
        var assigned = sessionManager.BackfillNumbers();
        FileLog.Write($"[SessionWriteExecutor] backfill-numbers assigned {assigned} number(s)");
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new BackfillNumbersResponse { Assigned = assigned }));
    }
}
