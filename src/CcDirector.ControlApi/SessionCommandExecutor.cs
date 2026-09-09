using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #1177 (Phase 1, increment 6): the Director-LOCAL services some command verbs must fire as a side
/// effect (a cache warm-up), so the stream path runs them exactly as the REST path does. Additive: verbs
/// that need no service ignore it, and a null field simply skips that side effect (as the REST endpoints
/// already do when the service is absent). Both call sites - <c>ControlEndpoints.Map</c> and
/// <c>ControlApiHost.BuildStreamClient</c> - have these in scope and pass the same instances.
/// </summary>
internal sealed class SessionCommandServices
{
    /// <summary>The auto-explain / background-briefing service (mobile/voice/wingman toggles warm it).</summary>
    public ProactiveExplainService? ProactiveExplain { get; init; }

    /// <summary>The turn-summary + goal-assessment cache (setting a wingman goal kicks an assessment).</summary>
    public TurnSummaryCache? TurnSummaryCache { get; init; }

    // There is deliberately NO mission store here. Missions are a fleet-level record owned by the Gateway,
    // which resolves one in the caller's own tenant and sends the create/attach verb its NAME alongside its
    // id; the Director stamps what it was handed. The Director-local store this field used to point at was
    // a different, per-machine set that nothing wrote any more, and consulting it made a real mission look
    // unknown (issue #2629). Do not reintroduce it.

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (wave 3): this Director's build version string, so a director-level
    /// read that stamps it into its response (the <c>facts</c> and <c>handover</c> verbs) can serve the same
    /// value over the tunnel that the REST route stamped from <c>ControlApiHost._version</c>. The producing
    /// Director always stamps its own version, so the value is identical on both paths.
    /// </summary>
    public string? DirectorVersion { get; init; }

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (wave 3): the live per-host repository registry, so the <c>repos-list</c>
    /// verb reads the same instance the REST route read at Map time. Null lists nothing (as the REST route
    /// returned when no registry was wired).
    /// </summary>
    public RepositoryRegistry? Repositories { get; init; }

    /// <summary>
    /// Gateway Cleanup CUT RESTORATION: the live "re-apply the Gateway settings now" hook, so the
    /// <c>settings-put</c> verb makes a gateway change take effect immediately exactly as the Director's own
    /// <c>PUT /settings</c> route does. This is the one service that is NOT an optional side effect: the REST
    /// route always holds one, so a null here means the host wired the stream client wrong, and the verb
    /// refuses a gateway patch outright rather than writing settings that never take effect.
    /// </summary>
    public Func<Task>? ReapplyGatewayAsync { get; init; }
}

/// <summary>
/// Issue #1177 (Phase 1): the single command core shared by the Director's REST endpoints and its
/// Gateway stream down-channel. Each verb reproduces the exact guards and underlying
/// <see cref="Session"/>/<see cref="SessionManager"/> calls the REST lambda made before this refactor,
/// returning a <see cref="DirectorCommandResult"/> so both callers execute identical logic and cannot
/// drift (the same reason Phase 1a extracted the shared <c>ControlEndpoints.Map</c> session mapper).
///
/// The REST layer maps the returned <see cref="DirectorCommandStatus"/> back to <c>Results.*</c>; the
/// stream layer ships the result down the wire verbatim. These methods are NOT boundaries, so they never
/// catch - they validate and fail explicitly, and let real faults bubble to the calling boundary (the
/// endpoint lambda or the SignalR handler), per the coding standard.
/// </summary>
internal static class SessionCommandExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (spine): every command AREA, pre-created ONCE. The verb-to-area map
    /// below is built from these. A worker fills its own area class; this list changes only when a whole new
    /// area is introduced (never for adding a verb), so it is not a merge chokepoint.
    /// </summary>
    private static readonly ISessionCommandArea[] Areas =
    {
        new SessionReadExecutor(),
        new CatalogReadExecutor(),
        new SessionWriteExecutor(),
        new QueueGitExecutor(),
        new SessionByteExecutor(),
        new DirectorConfigExecutor(),
        // Defect 5: the Gateway stamping a session's resolved role down onto this Director.
        new FleetRoleExecutor(),
        // The Gateway stamping a session's FOLDED display state down onto this Director, so the desktop
        // rail renders the Gateway's answer instead of re-folding from local facts it cannot see.
        new FleetDisplayStateExecutor(),
        // Remove-the-network-port mission, phase 2: the automation browsers. Machine-local by construction -
        // a loopback debug port and a profile directory on this disk - so the Gateway never drives one; it
        // carries the command to the Director that does.
        new BrowserExecutor(),
    };

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (spine): the single verb-to-area dictionary, the one source of truth
    /// for which area owns which verb. Built ONCE at type initialization from the areas' declared verb lists;
    /// a duplicate verb across two areas throws immediately (fail loud, no silent shadow).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ISessionCommandArea> VerbMap = BuildVerbMap(Areas);

    internal static IReadOnlyDictionary<string, ISessionCommandArea> BuildVerbMap(IReadOnlyList<ISessionCommandArea> areas)
    {
        var map = new Dictionary<string, ISessionCommandArea>(StringComparer.Ordinal);
        foreach (var area in areas)
        {
            foreach (var verb in area.Verbs)
            {
                if (map.TryGetValue(verb, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate command verb '{verb}' declared by both {existing.GetType().Name} and {area.GetType().Name}. Each verb must be owned by exactly one command area.");
                map[verb] = area;
            }
        }
        return map;
    }

    /// <summary>
    /// Execute a command by verb. Looks the verb up in the single verb-to-area map and routes it to the
    /// owning area, which resolves the payload and target session with the shared guards and executes it.
    /// An unknown verb is a fail-loud <see cref="DirectorCommandStatus.BadRequest"/> naming the verb. The
    /// four connection-bound stream verbs are NOT dispatched here - they branch earlier, in the connection
    /// layer (Architect ruling A) - so this map is exactly the unary read and write surface.
    /// </summary>
    public static async Task<DirectorCommandResult> DispatchAsync(SessionManager sessionManager, string directorId, DirectorCommand command, SessionCommandServices? services = null, SendSource source = SendSource.UserInput, CancellationToken cancellationToken = default)
    {
        if (sessionManager is null) throw new ArgumentNullException(nameof(sessionManager));
        if (command is null) throw new ArgumentNullException(nameof(command));

        FileLog.Write($"[SessionCommandExecutor] DispatchAsync: verb={command.Verb}, sid={command.SessionId}, cmdId={command.CommandId}, source={source}, director={directorId}");

        if (!VerbMap.TryGetValue(command.Verb, out var area))
        {
            FileLog.Write($"[SessionCommandExecutor] DispatchAsync: unknown verb '{command.Verb}'");
            return new DirectorCommandResult
            {
                CommandId = command.CommandId,
                Status = DirectorCommandStatus.BadRequest,
                Error = $"unknown verb '{command.Verb}'",
            };
        }

        var context = new SessionCommandContext(sessionManager, directorId, services, source);
        var result = await area.ExecuteAsync(context, command, cancellationToken);

        result.CommandId = command.CommandId;
        FileLog.Write($"[SessionCommandExecutor] DispatchAsync result: verb={command.Verb}, sid={command.SessionId}, status={result.Status}");
        return result;
    }

    /// <summary>
    /// The <c>prompt</c> verb: send text (with or without Enter) to a session. Mirrors the Director's
    /// <c>POST /sessions/{sid}/prompt</c> lambda exactly - invalid id -&gt; BadRequest, empty text -&gt;
    /// BadRequest, missing session -&gt; NotFound, Exited/Failed -&gt; Conflict - and returns a serialized
    /// <see cref="PromptResponse"/> on success.
    /// </summary>
    internal static async Task<DirectorCommandResult> PromptAsync(SessionManager sessionManager, DirectorCommand command, SendSource source = SendSource.UserInput)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<PromptRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.Text))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "text is required");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // Gateway Cleanup mission, Phase 2: a dictation delivery marks itself in the request DTO
        // (DeliveryUploadId), so the tunnel prompt verb carries the Delivery source with no HTTP header.
        // The REST path still sets `source` from the X-Dictation-Delivery header (back-compat); either
        // signal makes this a Delivery.
        var effectiveSource = !string.IsNullOrWhiteSpace(request.DeliveryUploadId) ? SendSource.Delivery : source;

        return await SendPromptAsync(session, request, effectiveSource);
    }

    /// <summary>
    /// The prompt core, past the id/session guards: reject an exited session, capture the pre-send
    /// buffer cursor, then deliver the text. Shared by the verb handler and directly testable against
    /// a session. <paramref name="source"/> names who is sending - <see cref="SendSource.UserInput"/>
    /// (the default), <see cref="SendSource.Delivery"/> (a dictation's own arrival),
    /// <see cref="SendSource.Agent"/> (another agent) or <see cref="SendSource.Framework"/> - for
    /// diagnostics and the tally; no source is ever refused. The old
    /// dictation-lock refusal was removed deliberately (single-operator tool; the operator may inject
    /// into their own sessions whenever they like).
    /// </summary>
    internal static async Task<DirectorCommandResult> SendPromptAsync(Session session, PromptRequest request, SendSource source = SendSource.UserInput)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (request is null) throw new ArgumentNullException(nameof(request));

        if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "session has exited");

        var bufferCursor = session.Buffer?.TotalBytesWritten ?? 0;

        // DevThrottle Stats: build the origin for the Director's choke-point tally. Modality is voice for a
        // dictation delivery (SendSource.Delivery, set from the X-Dictation-Delivery marker) and typed
        // otherwise. Surface comes from the Gateway-authoritative request.Surface. Count when this is an
        // operator prompt - marked by a NON-NULL request.Surface, which the operator front doors (the
        // Gateway prompt handler and the dictation delivery) always set, stamping "unknown" when the device
        // key did not resolve. A phone/cockpit key maps to that surface; "unknown" maps to the Unknown
        // bucket the dashboard shows (decision 9: excluded volume is surfaced, never silently dropped).
        // Issue #1636: a fleet message addressed to a session on ANOTHER Director arrives here as an
        // ordinary prompt, so the relay marks it in the DTO - the same trick DeliveryUploadId uses for a
        // dictation. Without it, the identical fleet message would be counted as agent-driven or not
        // depending only on whether the two sessions happened to share a Director.
        var effectiveSource = request.AgentDriven ? SendSource.Agent : source;

        // This origin is the HUMAN tally only. Neither a framework send nor an agent prompting another
        // agent is a person driving, so both are excluded here by name rather than by relying on their
        // Surface being null (agent-driven turns are counted on their own separate lane, and must never
        // leak into the voice-versus-typed numbers).
        var human = effectiveSource is SendSource.UserInput or SendSource.Delivery;
        InputOrigin? origin = (human && request.Surface is not null)
            ? new InputOrigin(
                effectiveSource == SendSource.Delivery ? InputModality.Voice : InputModality.Typed,
                InputOrigin.RemoteSurfaceFromDeviceType(request.Surface))
            : null;

        // WHAT THE DOOR KNEW (source logging, 2026-09-05): the Gateway's route built it from what it verified and
        // sent it on the wire; it is recorded as it arrived. A Gateway older than the field sends none, and that
        // is recorded as the unknown it is - never guessed from the surface or the send source.
        var provenance = SubmissionProvenance.FromWire(request.Provenance,
            request.AgentDriven ? SubmissionRoutes.FleetMessage
            : !string.IsNullOrWhiteSpace(request.DeliveryUploadId) ? SubmissionRoutes.GatewayDictation
            : SubmissionRoutes.GatewayPrompt);
        if (request.AppendEnter)
            await session.SendTextAsync(request.Text, provenance, effectiveSource, origin);
        else
            session.SendInput(Encoding.UTF8.GetBytes(request.Text), origin, provenance);

        var response = new PromptResponse
        {
            Accepted = true,
            SentAt = DateTime.UtcNow,
            BufferCursor = bufferCursor,
            ActivityState = session.ActivityState.ToString(),
        };
        return DirectorCommandResult.Success(Serialize(response));
    }

    /// <summary>
    /// The <c>interrupt</c> verb: hard-interrupt a session's current turn (Ctrl+C for Claude). Mirrors the
    /// Director's <c>POST /sessions/{sid}/interrupt</c> lambda - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound - and a driver that refuses (e.g. pi, whose double-Ctrl+C quits it) -&gt; Conflict.
    /// </summary>
    internal static async Task<DirectorCommandResult> InterruptAsync(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        return await InterruptCoreAsync(session);
    }

    /// <summary>The interrupt core, past the guards. A driver's <see cref="NotSupportedException"/> is the
    /// expected "this CLI has no safe hard interrupt" signal, surfaced as a typed Conflict result.</summary>
    internal static async Task<DirectorCommandResult> InterruptCoreAsync(Session session)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        try
        {
            await session.InterruptAsync();
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        return DirectorCommandResult.Success();
    }

    /// <summary>
    /// The <c>escape</c> verb: soft-stop a session's current turn (Esc). Mirrors the Director's
    /// <c>POST /sessions/{sid}/escape</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - and a driver that refuses -&gt; Conflict.
    /// </summary>
    internal static async Task<DirectorCommandResult> EscapeAsync(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        return await EscapeCoreAsync(session);
    }

    /// <summary>The escape core, past the guards. A driver's <see cref="NotSupportedException"/> is the
    /// expected "this CLI has no soft cancel" signal, surfaced as a typed Conflict result.</summary>
    internal static async Task<DirectorCommandResult> EscapeCoreAsync(Session session)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        try
        {
            await session.CancelTurnAsync();
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        return DirectorCommandResult.Success();
    }

    /// <summary>
    /// The <c>hold</c> verb: write down the hold the GATEWAY has decided, so the desktop can render it.
    ///
    /// This verb no longer decides anything. It used to call <c>Session.RequestHold</c>, which ran the
    /// whole hold machine here on the Director - reading whether the agent was working to choose between
    /// an immediate and a deferred hold. The Gateway owns hold now: it holds the state, it owns the clock,
    /// and it makes both rulings. This is the push seam that carries its answer down to the one reader
    /// that cannot ask for itself, the local desktop rail.
    ///
    /// The response reports the mirror back for callers that still read it. <c>Pending</c> is simply
    /// "the Gateway says this is a deferral", not an outcome this Director computed.
    /// </summary>
    internal static DirectorCommandResult Hold(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<HoldRequest>(command.PayloadJson);

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // The Gateway's ruling, or - for a caller that sends only the boolean - the best that boolean can
        // say. Either way this Director reads no state and applies no rule.
        var decided = HoldStates.Normalize(request?.HoldState) switch
        {
            HoldStates.Held => HoldState.Held,
            HoldStates.DeferredHold => HoldState.DeferredHold,
            HoldStates.None => HoldState.None,
            _ => (request?.OnHold ?? true) ? HoldState.Held : HoldState.None,
        };

        session.ApplyGatewayHold(decided);
        FileLog.Write($"[SessionCommandExecutor] hold: session={guid} gateway-decided={decided} (mirror written, nothing decided here)");
        return DirectorCommandResult.Success(Serialize(new HoldResponse
        {
            OnHold = session.OnHold,
            Pending = decided == HoldState.DeferredHold,
        }));
    }

    /// <summary>
    /// The <c>kill</c> verb: end the agent process on this machine, reconcile it against the row, and say
    /// which of those two things it actually had to do. This is the Director half of the stop (mission
    /// "Stop a session", Seat 1) and it answers <see cref="DirectorStopResult"/>.
    ///
    /// It used to answer <c>{ killed = true, removed = true }</c> and nothing else, which could not tell
    /// "there was a live process and I ended it" from "there was nothing running" - the kill is
    /// best-effort and swallowed the difference. That is the defect this verb exists to fix: the Director
    /// is the ONE machine that holds both the row and the process, so it is the only place they can be
    /// compared (Ruling 3).
    ///
    /// What changed in behaviour, deliberately: a session with NO ROW on this Director is no longer
    /// <see cref="DirectorCommandStatus.NotFound"/>. Ruling 3 says the owning Director is asked to stop
    /// the session whether or not it still has a row for it, and a stop must never fail because there is
    /// nothing left to stop, so that case is an <c>alreadyStopped</c> SUCCESS.
    ///
    /// COULD NOT BE DETERMINED IS NEVER GONE, AND NEVER ENDED EITHER (the Architect's ruling on
    /// inspection 1). Liveness has three answers, not two - see <see cref="ProcessLiveness"/> - and where
    /// this verb cannot read whether a process was alive it still carries the stop out and then answers
    /// <see cref="SessionStopVerdict.StoppedNotDescribed"/>, naming in words what could not be read. It
    /// never answers <c>alreadyStopped</c> on an unread check, and it never sets
    /// <see cref="DirectorStopResult.ProcessEnded"/> from one. A session that carries no process
    /// identifier at all - the remote workflow backend, and the pipe and studio backends, all of which
    /// report zero - is the same case: nothing was checked, so nothing may be claimed.
    ///
    /// The kill itself is untouched: same call, same <see cref="SessionManager.FleetKillGraceMs"/> window,
    /// same best-effort catch (issue #212 L3). Only the answer got honest.
    /// </summary>
    /// <param name="processLiveness">Alive, gone, or could not be read - what asking this machine about
    /// that process identifier established. Null uses <see cref="DefaultProcessLiveness"/>. A TEST SEAM,
    /// so the tests can drive every branch without starting real processes; the tests that protect the
    /// production method itself deliberately pass nothing here.</param>
    /// <param name="worktreeProbe">The uncommitted-changes probe; null uses a shared
    /// <see cref="GitStatusProvider"/>. A TEST SEAM, the same one <c>SessionGitStatusMonitor</c> takes.</param>
    internal static async Task<DirectorCommandResult> KillAsync(
        SessionManager sessionManager,
        DirectorCommand command,
        Func<int, ProcessLivenessReading>? processLiveness = null,
        Func<string, CancellationToken, Task<GitCountResult>>? worktreeProbe = null)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var liveness = processLiveness ?? DefaultProcessLiveness;
        FileLog.Write($"[SessionCommandExecutor] kill: session={guid} looking for a row and a live process");

        var session = sessionManager.GetSession(guid);

        // NO ROW ON THIS DIRECTOR. Not an error - Ruling 3. Killed/Removed are the COMPATIBILITY pair (see
        // DirectorStopResult): they are best-effort and do NOT distinguish these states, which is exactly
        // why the honest fields sit beside them. They read true here because the only thing the old
        // two-field answer could ever mean is "the session is not there any more", and it is not.
        if (session is null)
        {
            var missing = new DirectorStopResult
            {
                Killed = true,
                Removed = true,
                ProcessId = null,
                ProcessEnded = false,
                RowRemoved = false,
                WorktreePath = null,
                WorktreeHadUncommittedChanges = null,
                Verdict = SessionStopVerdict.AlreadyStopped,
            };
            FileLog.Write($"[SessionCommandExecutor] kill: session={guid} verdict={missing.Verdict}, pid=none, "
                + "rowRemoved=false, worktreeProbe=not-run (no row on this Director - Ruling 3, not an error)");
            return DirectorCommandResult.Success(Serialize(missing));
        }

        // ---- the facts, captured BEFORE the stop, because after it there is nothing left to read ----

        // A session that never held a process (ProcessId 0 from a buffer-only backend, or a row whose
        // process is long gone) reports null rather than 0: 0 is not a process id, and printing it would
        // invite a reader to go looking for it.
        int? processId = session.ProcessId > 0 ? session.ProcessId : null;

        // LIVENESS IS ASKED OF THE OPERATING SYSTEM, NOT OF THE BACKEND. ISessionBackend.HasExited is
        // documented as "the process has exited", but only ConPtyBackend and UnixPtyBackend implement it
        // that way - PipeBackend, StudioBackend and GitHubActionsBackend all return _disposed, which is a
        // different fact entirely, so on three of the five backends it would answer "has exited" purely on
        // whether somebody had disposed the object. Pattern and rule from LauncherDiscovery.IsRunning.
        //
        // AND WHERE THERE IS NO IDENTIFIER, NOTHING WAS CHECKED. The sentence this replaces said the process
        // identifier gives a backend-independent answer; inspection 1 (finding I3) showed that is false for
        // the backends which report zero - the remote workflow backend reports zero while a remote run is
        // actively going, and the pipe and studio backends report zero always. Skipping the check and then
        // answering "no process was running" is the forbidden inference with no check at all in front of it.
        ProcessLivenessReading before;
        string? notDescribed = null;
        if (processId is int pid)
        {
            before = liveness(pid);
            if (before.State == ProcessLiveness.Unreadable)
                notDescribed = $"whether process {pid} was running could not be read before the stop - "
                    + before.WhatCouldNotBeRead;
        }
        else
        {
            before = ProcessLivenessReading.CouldNotRead("this session carried no process identifier");
            notDescribed = "this session carried no process identifier, so no process could be checked - "
                + "whether anything was running, and whether anything has ended, are not known";
        }

        // PID REUSE, NAMED RATHER THAN PRETENDED CLOSED: between this check and the re-check after the kill,
        // the operating system could in principle hand that number to an unrelated process, and the re-check
        // would then read "still alive" and report a process that would not die. The window is milliseconds
        // wide and the number space is large, so this is vanishingly unlikely - but it is not impossible, and
        // an honest answer says so. Closing it needs a process handle held across the kill, which is a change
        // to the backends, not to this verb.

        // The WORKING DIRECTORY, not RepoPath. They are the same string on every path that creates a session
        // today (SessionManager passes repoPath for both), so this choice is invisible in practice; it
        // matters only for a restored session that persisted a different one. Where they differ, the working
        // directory is the tree the agent process was actually running in - which is the tree whose
        // uncommitted changes the operator is about to walk away from, and the whole point of Ruling 2.
        string? worktreePath = string.IsNullOrWhiteSpace(session.WorkingDirectory) ? null : session.WorkingDirectory;
        bool? worktreeDirty = worktreePath is null
            ? null
            : await ProbeWorktreeUncommittedAsync(guid, worktreePath, worktreeProbe);

        // ---- the stop itself, exactly as it ran before ----

        try
        {
            // Faster STOP: this is the FLEET/remote stop path (Gateway stop route / stream "kill" verb), so
            // it escalates to force after the shorter FleetKillGraceMs window instead of the full desktop
            // GracefulShutdownTimeoutSeconds. Graceful-first is preserved (Ctrl+C then wait), just quicker.
            // When FleetKillGraceMs is disabled (null/non-positive) this resolves to the standard window.
            await sessionManager.KillSessionAsync(guid, sessionManager.FleetKillGraceMs);
        }
        catch (KeyNotFoundException)
        {
            // The row was there a moment ago and is not now - another caller removed it while this stop was
            // in flight. That is "there is nothing left to stop", which Ruling 3 makes a SUCCESS, so it falls
            // through with the rest of the best-effort handling. This used to return NotFound; the ONE place
            // a missing row is now decided is the null-session check above, before any of the facts are read.
            FileLog.Write($"[SessionCommandExecutor] kill: session={guid} the row vanished mid-stop (raced with another remover)");
        }
        catch (Exception killEx)
        {
            // The process may have already exited; that is not a reason to leave a zombie row, so log and
            // fall through to removal. A stop always means gone (matches the desktop close flow).
            FileLog.Write($"[SessionCommandExecutor] kill: session={guid} kill raised (process likely already gone): {killEx.Message}");
        }

        // ---- did the BACKEND itself say the shutdown failed? ----

        // Ruling 3's third failure - "the process would not die" - reached by a different road, and found by
        // inspection 1 (finding I3). The remote workflow backend catches a refused CancelRunAsync, writes the
        // words into its own terminal buffer and returns normally; before this, that swallowed failure was
        // folded into "no process was running" and the row was cleared, leaving a remote run going with
        // nothing on the fleet able to see or stop it. Every other backend reports null here and is
        // unaffected. The row is left in place for the same reason it is left below.
        var backendFailure = session.Backend.LastShutdownFailure;
        if (!string.IsNullOrWhiteSpace(backendFailure))
        {
            var refused = $"the stop was refused by the session's own backend: {backendFailure}";
            FileLog.Write($"[SessionCommandExecutor] kill: session={guid} FAILED: {refused} "
                + $"(row left in place on purpose, worktreeProbe={ProbeOutcome(worktreeDirty)})");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, refused);
        }

        // ---- did it actually die? ----

        // Only a process that was ESTABLISHED alive can be established to have ended. Where the check before
        // the stop could not be read, re-reading it afterwards cannot repair that: "stopped" claims a process
        // was running, and nothing established one. So that case skips the re-check and keeps its sentence.
        bool processEnded = false;
        if (before.State == ProcessLiveness.Alive && processId is int livePid)
        {
            var after = await WaitForProcessToGoAsync(livePid, liveness);
            switch (after.State)
            {
                case ProcessLiveness.Gone:
                    processEnded = true;
                    break;

                case ProcessLiveness.Alive:
                    // Ruling 3's third and only process-level failure: "the process would not die".
                    // Reporting this as a success is the worse of the two mistakes the ruling exists to
                    // prevent - the operator would read "stopped" and stop looking.
                    //
                    // The row is deliberately NOT removed here. A row removed while its process is still
                    // running is a live agent nothing on the fleet can see or stop again; leaving it is what
                    // lets the operator see the session and try once more.
                    var stuck = $"the process would not die: process {livePid} is still running after the stop";
                    FileLog.Write($"[SessionCommandExecutor] kill: session={guid} FAILED: {stuck} "
                        + $"(row left in place on purpose, worktreeProbe={ProbeOutcome(worktreeDirty)})");
                    return DirectorCommandResult.Fail(DirectorCommandStatus.Error, stuck);

                default:
                    // It WAS running when the stop began, and this machine can no longer read it. That is
                    // neither "ended" nor "would not die", and guessing either way is the whole finding.
                    notDescribed = $"process {livePid} was running when the stop began, and whether it has "
                        + $"ended could not be read afterwards - {after.WhatCouldNotBeRead}";
                    break;
            }
        }

        sessionManager.RemoveSession(guid);

        var result = new DirectorStopResult
        {
            // The compatibility pair, with the values they have always had on this path. They do NOT
            // distinguish "ended a live process" from "there was nothing running"; the honest fields below
            // are why they no longer have to.
            Killed = true,
            Removed = true,
            ProcessId = processId,
            ProcessEnded = processEnded,
            RowRemoved = true,
            WorktreePath = worktreePath,
            WorktreeHadUncommittedChanges = worktreeDirty,
            // WHAT WAS ESTABLISHED IS STILL REPORTED. Under stoppedNotDescribed the process identifier read
            // off the row, the fact that the row was removed and the worktree sentence Ruling 2 requires are
            // all things THIS stop genuinely established, and blanking them would throw away what the
            // operator needs - the dirty-tree sentence most of all. Only the process facts nobody could read
            // are left empty: ProcessEnded stays false because nothing established it, not because it was
            // established to be false.
            NotDescribedReason = notDescribed,
            // "stopped" ONLY when a live process was found and this stop ended it. "alreadyStopped" ONLY
            // when this machine successfully looked and found nothing. Anything it could not read is
            // stoppedNotDescribed - reused, never a fifth word. The Director never returns notOnFleet: it
            // can see one machine, and that verdict is a statement about the whole account.
            Verdict = notDescribed is not null
                ? SessionStopVerdict.StoppedNotDescribed
                : processEnded ? SessionStopVerdict.Stopped : SessionStopVerdict.AlreadyStopped,
        };

        FileLog.Write($"[SessionCommandExecutor] kill: session={guid} verdict={result.Verdict}, "
            + $"pid={(result.ProcessId?.ToString() ?? "none")}, processEnded={result.ProcessEnded}, "
            + $"rowRemoved={result.RowRemoved}, worktree={worktreePath ?? "none"}, "
            + $"worktreeProbe={ProbeOutcome(worktreeDirty)}"
            + (notDescribed is null ? "" : $", notDescribed={notDescribed}"));
        return DirectorCommandResult.Success(Serialize(result));
    }

    /// <summary>
    /// How long the re-check will wait for a killed process id to disappear before calling it "would not die".
    ///
    /// This window is NOT a second grace period and it does not soften the escalation: by the time it is
    /// reached the force-kill has already been issued. It exists because that force-kill is ASYNCHRONOUS -
    /// <c>Process.Kill(entireProcessTree: true)</c> is TerminateProcess, which returns before the process is
    /// gone - so an immediate single re-check can read "still alive" milliseconds after a perfectly
    /// successful kill and report a failure that did not happen. One second is far longer than that gap and
    /// far shorter than a human waits; after it, the verb fails loudly rather than quietly retrying.
    /// </summary>
    private static readonly TimeSpan ProcessExitSettleWindow = TimeSpan.FromSeconds(1);

    /// <summary>Poll interval inside <see cref="ProcessExitSettleWindow"/>.</summary>
    private static readonly TimeSpan ProcessExitPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The probe budget for the worktree question. Short on purpose: a stop is the sharpest thing
    /// the product does and a git call must never be able to hold it up. A timeout is UNKNOWN, exactly like
    /// a failure.</summary>
    private static readonly TimeSpan WorktreeProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The production liveness check: does a process with this identifier exist on this machine right now?
    ///
    /// IT HAS THREE ANSWERS, AND THAT IS THE WHOLE POINT (inspection 1, finding I2). It used to return a
    /// plain <c>bool</c> and collapse every exception into <c>false</c>, so a LIVE process whose
    /// <c>HasExited</c> threw a <c>Win32Exception</c> - which a process this machine may not open does -
    /// was reported as a process that had gone. That false then did two separate pieces of damage: before
    /// the stop it produced the verdict "already stopped", and after the stop it certified that the process
    /// had ended. An access failure establishes neither fact.
    ///
    /// <c>ArgumentException</c> from <c>Process.GetProcessById</c> is the one real absence: the operating
    /// system enumerated its process identifiers and that one is not among them. EVERYTHING ELSE IS
    /// UNREADABLE, and carries the machine's own words forward so the answer can name what failed.
    ///
    /// A non-positive identifier is <see cref="ProcessLiveness.Gone"/> rather than unreadable, because it is
    /// not a failed read at all: zero is what a backend reports when it holds no process, and the caller
    /// never asks about one - it takes the no-identifier case before it gets here.
    /// </summary>
    private static ProcessLivenessReading DefaultProcessLiveness(int processId)
    {
        if (processId <= 0) return ProcessLivenessReading.IsGone;
        try
        {
            using var p = Process.GetProcessById(processId);
            return p.HasExited ? ProcessLivenessReading.IsGone : ProcessLivenessReading.IsAlive;
        }
        catch (ArgumentException)
        {
            return ProcessLivenessReading.IsGone;   // no such process: the operating system looked and said so
        }
        catch (Exception ex)
        {
            var words = $"could not read process {processId} on this machine ({ex.GetType().Name}: {ex.Message})";
            FileLog.Write($"[SessionCommandExecutor] kill: {words} - reporting UNREADABLE, not gone");
            return ProcessLivenessReading.CouldNotRead(words);
        }
    }

    /// <summary>
    /// Has this process identifier gone, allowing <see cref="ProcessExitSettleWindow"/> for an asynchronous
    /// force-kill to land? Returns as soon as it has, so the ordinary case costs nothing.
    ///
    /// It answers with the reading itself, not a boolean, because the three answers stay apart all the way
    /// out: a check that could not be read after the stop must not become "still running" any more than it
    /// may become "ended". An unreadable answer is returned at once rather than polled on - the failure that
    /// produces it (a process this machine may not open) does not clear inside a one-second window, and
    /// waiting on it would only delay the stop.
    /// </summary>
    private static async Task<ProcessLivenessReading> WaitForProcessToGoAsync(
        int processId, Func<int, ProcessLivenessReading> liveness)
    {
        var deadline = DateTime.UtcNow + ProcessExitSettleWindow;
        while (true)
        {
            var reading = liveness(processId);
            if (reading.State != ProcessLiveness.Alive) return reading;
            if (DateTime.UtcNow >= deadline) return reading;
            await Task.Delay(ProcessExitPollInterval);
        }
    }

    /// <summary>
    /// Did that worktree have uncommitted changes in it? True, false, or NULL FOR "COULD NOT TELL".
    ///
    /// A failed or timed-out probe is null and NEVER false: reporting "clean" is the one thing a probe that
    /// did not run does not know, and every reader downstream would take it as verified (issue 516, and the
    /// same rule written into SessionGitStatusMonitor). Every failure is caught here for the same reason the
    /// kill is best-effort: a git probe must never be able to fail or hold up a stop.
    ///
    /// NOT PROVEN, AND A REAL GAP: GitStatusProvider caches for ten seconds, keyed by path and shared across
    /// instances, so this can report a state up to ten seconds old - and the Director's own
    /// SessionGitStatusMonitor is filling that cache every fifteen seconds. It is accepted rather than
    /// invalidated, because this sentence is ADVISORY: Ruling 2 says the stop never refuses, so nothing is
    /// gated on the answer, and the service that later removes a worktree re-checks for itself and fails
    /// closed. The cost of being wrong is that the operator is pointed at the wrong tree for ten seconds;
    /// the cost of invalidating would be throwing away a cache entry the monitor owns, on every stop.
    /// </summary>
    private static async Task<bool?> ProbeWorktreeUncommittedAsync(
        Guid sessionId,
        string worktreePath,
        Func<string, CancellationToken, Task<GitCountResult>>? worktreeProbe)
    {
        var probe = worktreeProbe ?? new GitStatusProvider().GetCountAsync;
        using var cts = new CancellationTokenSource(WorktreeProbeTimeout);
        try
        {
            var count = await probe(worktreePath, cts.Token);
            return count.Success ? count.Count > 0 : null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionCommandExecutor] kill: session={sessionId} worktree probe on {worktreePath} "
                + $"did not answer ({ex.GetType().Name}: {ex.Message}) - reporting UNKNOWN, not clean");
            return null;
        }
    }

    /// <summary>The worktree probe outcome as one word, for the log line.</summary>
    private static string ProbeOutcome(bool? worktreeDirty) => worktreeDirty switch
    {
        true => "dirty",
        false => "clean",
        null => "unknown",
    };

    /// <summary>
    /// The <c>patch</c> verb: rename a session (the only PATCH field today). Mirrors the Director's
    /// <c>PATCH /sessions/{sid}</c> lambda - invalid id -&gt; BadRequest, unknown session -&gt; NotFound -
    /// and returns the updated session mapped through the SAME <see cref="ControlEndpoints.Map"/> the
    /// stream snapshot uses (the Gateway stamps machine/user/tailnet identity onto pushed rows during
    /// aggregation, exactly as it does for every other streamed row). The Director's own REST endpoint
    /// re-maps with its identity-stamped mapper for its local response, keeping that path byte-identical.
    /// </summary>
    internal static DirectorCommandResult Patch(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<SessionUpdateRequest>(command.PayloadJson);

        if (!sessionManager.RenameSession(guid, request?.Name))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        FileLog.Write($"[SessionCommandExecutor] patch: session={guid} name=\"{request?.Name}\"");
        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>
    /// The <c>create</c> verb (director-level: no target session id): create a new session. This is the
    /// heaviest verb and keeps ALL the inline pre-work the REST <c>POST /sessions</c> lambda did, so REST
    /// and stream create identically: agent-kind parse, RawCli command validation, agent construction
    /// (<see cref="RawCliAgent"/> vs <see cref="AgentPluginRegistry.CreateAgent"/>), default-args
    /// resolution (issue #1017, <see cref="AgentLaunchDefaults.ResolveDefaultArgs"/> when no Args given),
    /// name-at-birth validation (issue #800), the controlled-sub-agent controller id (issue #815), the
    /// <see cref="SessionManager.CreateSession"/> call, the per-session Wingman opt-in, and the
    /// fire-and-forget PrePrompt dispatch that waits for the TUI to settle (issue #212). Returns the new
    /// session mapped through the plain <see cref="ControlEndpoints.Map"/> (the Director's own REST
    /// endpoint re-maps with its identity-stamped mapper for its local 201 response).
    /// </summary>
    internal static DirectorCommandResult Create(SessionManager sessionManager, string directorId, DirectorCommand command, SessionCommandServices? services = null)
    {
        var req = Deserialize<NewSessionRequest>(command.PayloadJson);

        if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repoPath is required");

        if (!Directory.Exists(req.RepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"repoPath does not exist: {req.RepoPath}");

        if (!Enum.TryParse<AgentKind>(req.Agent, ignoreCase: true, out var kind))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown agent: {req.Agent}. Valid: ClaudeCode, Pi, Codex, Gemini, OpenCode, Grok, Copilot, RawCli");

        // Automatic session roles (chunk 2.5): reject an unknown explicit role BEFORE creating the session,
        // so a mistyped --role never silently drops (the exact --type situation we removed).
        if (req.Role is not null && !SessionRoles.IsValid(req.Role))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown role '{req.Role}'. Valid: {string.Join(", ", SessionRoles.All)}");

        // Session origin and lineage (devthrottle_internal issue #982), validated BEFORE creating the
        // session on exactly the role check's terms. A mistyped origin is REJECTED rather than recorded
        // as "unknown", because unknown is also what an honest older caller sends: if a typo landed
        // there too, the two would be indistinguishable and the field's whole purpose - counting how
        // many sessions agents start - would quietly absorb every caller's mistakes.
        if (req.Origin is not null && !SessionOriginKinds.IsValid(req.Origin))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"unknown origin '{req.Origin}'. Valid: {string.Join(", ", SessionOriginKinds.All)}");
        if (req.OriginSurface is not null && !SessionOriginSurfaces.IsValid(req.OriginSurface))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"unknown origin surface '{req.OriginSurface}'. Valid: {string.Join(", ", SessionOriginSurfaces.All)}");
        Guid? parentSessionId = null;
        if (!string.IsNullOrWhiteSpace(req.ParentSessionId))
        {
            if (!Guid.TryParse(req.ParentSessionId, out var parsedParentId))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"parentSessionId '{req.ParentSessionId}' is not a session id.");
            parentSessionId = parsedParentId;
        }
        // Compose now so the create log records what will actually be stamped, not what was asked for -
        // the composer drops a parent id that arrived on a non-agent origin.
        var origin = SessionOrigin.Compose(req.Origin, req.OriginSurface, parentSessionId);

        // Workflow seat at spawn (Workflows mission, phase 5b): validated BEFORE creating the session,
        // like the role above. The Director stamps the seat ONLY when the run id arrives with its
        // Gateway-resolved workflow id and pinned version - the Gateway is the source of truth for
        // runs and the Director never resolves one itself; a run id arriving alone (a caller that
        // skipped the Gateway resolution) seats nothing rather than guessing. A workflow id that is
        // not a catalog slug, or a non-positive version, is a forged or corrupted seat and is
        // REFUSED - never launched and never rendered into a preamble.
        var seatRequested = req.WorkflowRunId is Guid &&
            !string.IsNullOrWhiteSpace(req.WorkflowId) && req.WorkflowVersion is int;
        if (seatRequested)
        {
            if (WorkflowSeatParagraph.Build(req.WorkflowRunId, req.WorkflowId, req.WorkflowVersion, req.Role) is null)
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"invalid workflow seat: workflow id '{req.WorkflowId}' is not a catalog slug.");
            if (req.WorkflowVersion is int badVersion && badVersion < 1)
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"invalid workflow seat: version {badVersion} is not a published version number.");
        }

        // Mission attach at spawn. Missions are a FLEET-level concept and live at the Gateway (the source of
        // truth), so the mission that binds the session into a pod arrives ON the create request, ID AND
        // NAME TOGETHER: the Gateway resolved and validated it against its own tenant-scoped store before
        // dispatching, and that resolution IS the authorization. The Director stamps what it was handed -
        // no local store, no lookup, no second opinion. No MissionId means no attach.
        //
        // AN ID WITH NO NAME IS REFUSED, and the message says why (issue #2629). The Director used to
        // resolve a bare id against its OWN missions.json - a different, per-machine, single-tenant set
        // that nothing writes any more. So a caller that skipped the Gateway's resolution got a mission
        // that was real, active and listed by `cc-devthrottle mission list` reported as UNKNOWN, with
        // advice to create something that already existed. That bridge was documented as temporary from
        // the day it was written and is now gone: a bare id means the caller bypassed the one place that
        // can answer, and saying so plainly beats consulting a stale store and guessing.
        //
        // Resolved BEFORE creating the session (mirroring the explicit-role check) so a refused mission
        // never leaves a started session attached to nothing. attachMissionId / attachMissionName below
        // carry the values stamped after creation.
        Guid? attachMissionId = null;
        string? attachMissionName = null;
        if (req.MissionId is Guid createMissionId)
        {
            if (string.IsNullOrWhiteSpace(req.MissionName))
            {
                FileLog.Write($"[SessionCommandExecutor] create REFUSED: mission {createMissionId} arrived " +
                              "with no name, so it was never resolved by the Gateway");
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"mission '{createMissionId}' arrived without its name, so it was not resolved by the " +
                    "Gateway. Missions live at the Gateway, not on this machine - spawn through the Gateway " +
                    "(cc-devthrottle session spawn), which resolves the mission and sends its name.");
            }
            // The Gateway resolved this mission inside the caller's own tenant; stamp it.
            attachMissionId = createMissionId;
            attachMissionName = req.MissionName;
        }

        // RawCli requires a Command; validate before constructing the agent.
        if (kind == AgentKind.RawCli && string.IsNullOrWhiteSpace(req.Command))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "command is required when agent is RawCli");

        IAgent agent;
        if (kind == AgentKind.RawCli)
        {
            // Non-null here: the RawCli guard above already rejected a blank command.
            var rawCommand = req.Command ?? throw new InvalidOperationException("RawCli command missing after validation");
            agent = new RawCliAgent(rawCommand, req.CommandArgs);
        }
        else
        {
            // Issue #1050: the EXECUTABLE comes from the configured agent entry, exactly like the
            // arguments resolved just below. Building the agent from AgentOptions alone launched the
            // per-type default - a bare "claude" - and ignored the absolute path the onboarding
            // wizard had just recorded for the binary it installed, so a clean machine could not
            // start a session with the agent its own wizard reported as ready.
            agent = AgentLaunchDefaults.CreateAgentForKind(kind, sessionManager.Options);
        }

        // Issue #1017: with no explicit Args, inherit the configured default launch line for this kind
        // (most importantly the permission-mode preset), exactly as the desktop New Session dialog does.
        // An explicitly supplied Args (even empty) is honored verbatim; RawCli carries its whole command
        // line, so it has nothing to inherit. Empty normalizes back to null so Claude's legacy
        // DefaultClaudeArgs fallback still applies.
        string? effectiveArgs = req.Args;
        if (req.Args is null && kind != AgentKind.RawCli)
        {
            // Issue #1497: honor the caller's Bypass-permissions choice (the desktop dialog's checkbox,
            // default ON). Null keeps the historic default of true, so callers that omit it are unchanged.
            var bypassPermissions = req.BypassPermissions ?? true;
            var resolvedDefault = AgentLaunchDefaults.ResolveDefaultArgs(kind, sessionManager.Options, bypassPermissions);
            effectiveArgs = string.IsNullOrWhiteSpace(resolvedDefault) ? null : resolvedDefault;
            FileLog.Write($"[SessionCommandExecutor] create: no args supplied; applied default agent settings for {kind} (bypassPermissions={bypassPermissions}): \"{effectiveArgs ?? "(empty)"}\"");
        }

        // Issue #800: enforce a meaningful name at birth. An EXPLICIT name that is blank or equal to the
        // bare repository folder name is rejected; an ABSENT name is auto-composed by the name factory.
        var repoFolderName = SessionName.FolderName(req.RepoPath);
        if (req.Name is not null && SessionName.IsWeakExplicitName(req.Name, repoFolderName))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"Provide a meaningful session name or a purpose: a blank name or the bare repository folder name (\"{repoFolderName}\") is not allowed.");

        var explicitName = req.Name;
        var purpose = req.Purpose;

        // Issue #815: a controlled "Supporting" sub-agent carries the spawning session's id (set only at
        // birth). An absent/unparseable value leaves it a normal (uncontrolled) session.
        Guid? controllerSessionId = null;
        if (!string.IsNullOrWhiteSpace(req.ControllerSessionId)
            && Guid.TryParse(req.ControllerSessionId, out var parsedControllerId))
            controllerSessionId = parsedControllerId;

        Session session;
        try
        {
            session = sessionManager.CreateSession(
                req.RepoPath,
                agent,
                effectiveArgs,
                SessionBackendType.ConPty,
                resumeSessionId: string.IsNullOrWhiteSpace(req.ResumeSessionId) ? null : req.ResumeSessionId,
                // Automatic session roles (chunk 3): a controlled-at-birth session is a Worker, so its
                // auto-composed name is task-flavored; others get the repo default.
                nameFactory: id => SessionName.Compose(
                    repoFolderName, explicitName, purpose, SessionName.Disambiguator(id),
                    isWorker: controllerSessionId is not null),
                controllerSessionId: controllerSessionId,
                // Pre-launch stamps (Workflows mission, phase 5b): the role and the workflow seat are
                // applied BEFORE any launch-time channel reads the session - Pi's preamble file is
                // written from it during create, and a startup hook can fetch the preamble the
                // instant the agent boots. Stamping after create raced the earliest readers and
                // missed Pi entirely. Both values were validated above.
                beforeLaunch: s =>
                {
                    var preLaunchRole = SessionRoles.Normalize(req.Role);
                    if (preLaunchRole is not null)
                        s.SetExplicitRole(preLaunchRole);
                    if (seatRequested)
                        s.SeatOnWorkflow(req.WorkflowRunId, req.WorkflowId, req.WorkflowVersion);
                    // Birth facts (issue #982), stamped in the same pre-launch window as the role and
                    // the seat. Pre-launch is not cosmetic here: the session's FIRST roster push can
                    // leave for the Gateway the moment the process starts, and the history recorder
                    // writes its row from that first sight. A stamp after create returns would race it,
                    // and the row that lost the race would record "unknown" for a session whose origin
                    // was known all along - permanently, since first sight only happens once.
                    s.StampOrigin(origin);
                });
        }
        catch (Exception ex)
        {
            // Creation genuinely can fail (spawn error, bad path); the documented contract is to surface
            // the message as an error the caller maps to 500 - the same behaviour the REST lambda had.
            FileLog.Write($"[SessionCommandExecutor] create FAILED: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, ex.Message);
        }

        // Apply the per-session Wingman opt-in (contract default true, matching Session.WingmanEnabled).
        session.WingmanEnabled = req.WingmanEnabled;
        FileLog.Write($"[SessionCommandExecutor] create: sid={session.Id} wingmanEnabled={session.WingmanEnabled}");

        // Scheduled-run auto-dismiss (issue #1200): a cron seed marks the session auto-dismiss so it closes
        // itself when it finishes with nothing needing a human. Only then attach the verdict watcher, which
        // parses the agent's CC-DISMISS sentinel off the transcript at each turn-end and stamps the verdict
        // (which flows up to the Gateway's auto-dismiss sweep). A human-started session leaves this false and
        // is never watched or auto-closed.
        session.AutoDismiss = req.AutoDismiss;
        if (session.AutoDismiss)
        {
            new AutoDismissVerdictWatcher().Attach(session);
            FileLog.Write($"[SessionCommandExecutor] create: sid={session.Id} autoDismiss=true (verdict watcher attached)");
        }

        // Automatic session roles (chunk 3): the name was AUTO-composed unless the caller gave an explicit
        // --name. Marking it lets a later explicit rename win and never be re-auto-named.
        session.IsAutoNamed = string.IsNullOrWhiteSpace(explicitName);

        // Automatic session roles (chunk 2.5): the spawn-time explicit role is stamped PRE-LAUNCH in
        // the beforeLaunch hook above (the workflow-seat paragraph names it, and Pi's preamble file
        // is written during create), sticky and winning over auto-derivation exactly as before.

        // Mission attach at spawn: stamp the session's MissionId and cache the display name (resolved above,
        // either carried by the Gateway or resolved via the transitional local-store bridge). This is the
        // attachment that binds the new session into a pod.
        if (attachMissionId is Guid stampMissionId)
            session.AttachToMission(stampMissionId, attachMissionName);


        // Issue #212: dispatch a supplied PrePrompt once the agent is actually READY, fire-and-forget so
        // create returns immediately. Readiness = a substantial startup burst followed by a quiet poll;
        // ActivityState alone is not a gate (a fresh session reads WaitingForInput from t=0, and seeding
        // into a still-booting agent drops the Enter keypresses).
        var seedText = req.PrePrompt;
        if (!string.IsNullOrWhiteSpace(seedText))
        {
            var prePrompt = seedText;
            var waitMs = Math.Max(1000, req.PrePromptWaitMs);
            var capturedSession = session;
            _ = Task.Run(async () =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                    long lastBytes = -1;
                    while (DateTime.UtcNow < deadline)
                    {
                        var st = capturedSession.ActivityState;
                        if (st is ActivityState.Exited) { FileLog.Write($"[SessionCommandExecutor] PrePrompt: session exited before ready, sid={capturedSession.Id}"); return; }
                        var bytes = capturedSession.Buffer?.TotalBytesWritten ?? 0;
                        var settled = bytes > 1500 && bytes == lastBytes
                            && st is ActivityState.Idle or ActivityState.WaitingForInput;
                        if (settled)
                        {
                            FileLog.Write($"[SessionCommandExecutor] PrePrompt: agent ready (TUI rendered {bytes} bytes, then settled), sid={capturedSession.Id}");
                            break;
                        }
                        lastBytes = bytes;
                        await Task.Delay(750);
                    }
                    FileLog.Write($"[SessionCommandExecutor] PrePrompt: dispatching to sid={capturedSession.Id}, len={prePrompt.Length}");
                    // Framework pre-prompt (not a human racing the dictation): exempt (issue #1181, Task 3b).
                    await capturedSession.SendTextAsync(prePrompt, SubmissionProvenance.FrameworkText(), SendSource.Framework);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[SessionCommandExecutor] PrePrompt FAILED: {ex.Message}");
                }
            });
        }

        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>
    /// The <c>wingman-goal</c> verb: set (or clear) the session's wingman goal. Mirrors the Director's
    /// <c>POST /sessions/{sid}/wingman/goal</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - sets <c>Session.WingmanGoal</c>, and (as a side effect, when a non-blank goal is set and
    /// the cache is available) kicks an immediate goal assessment so the verdict is warm. Returns the
    /// resulting goal / goalSetAt / goalState.
    /// </summary>
    internal static DirectorCommandResult WingmanGoal(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<WingmanGoalRequest>(command.PayloadJson);

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.SetWingmanGoal(request?.Goal);
        FileLog.Write($"[SessionCommandExecutor] wingman-goal: session={guid} goal=\"{request?.Goal}\"");

        // Side effect (identical to the REST endpoint): warm the goal assessment now. Fire-and-forget so
        // the command returns immediately; skipped when no goal is set or the cache is absent.
        if (!string.IsNullOrWhiteSpace(request?.Goal) && services?.TurnSummaryCache is not null)
            _ = services.TurnSummaryCache.AssessGoalNowAsync(guid);

        return DirectorCommandResult.Success(Serialize(new
        {
            goal = session.WingmanGoal,
            goalSetAt = session.WingmanGoalSetAt,
            goalState = session.WingmanGoalState,
        }));
    }

    /// <summary>
    /// The <c>set-role</c> verb: (re)declare a session's sticky explicit role after birth (automatic session
    /// roles). Invalid id -&gt; BadRequest, missing session -&gt; NotFound, unknown role -&gt; BadRequest. A
    /// blank/absent role CLEARS the explicit role (reverting to auto-derivation). Returns the updated session
    /// mapped through the SAME <see cref="ControlEndpoints.Map"/> the stream snapshot uses.
    /// </summary>
    internal static DirectorCommandResult SetRole(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<SetRoleRequest>(command.PayloadJson);

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        string? normalized;
        if (string.IsNullOrWhiteSpace(request?.Role))
        {
            normalized = null; // clear -> revert to auto-derivation
        }
        else
        {
            normalized = SessionRoles.Normalize(request.Role);
            if (normalized is null)
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"unknown role '{request.Role}'. Valid: {string.Join(", ", SessionRoles.All)}");
        }

        session.SetExplicitRole(normalized);
        FileLog.Write($"[SessionCommandExecutor] set-role: session={guid} role={normalized ?? "(cleared)"}");
        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>
    /// The <c>attach-mission</c> verb: attach a session to a Mission (or DETACH it on a blank/absent
    /// MissionId). Invalid id -&gt; BadRequest, missing session -&gt; NotFound, unknown Mission -&gt;
    /// BadRequest. Returns the updated session mapped through the SAME <see cref="ControlEndpoints.Map"/> the
    /// stream snapshot uses. Mirrors <see cref="SetRole"/>.
    ///
    /// The Mission ID and NAME arrive TOGETHER, exactly as they do on the create path (see the mission
    /// block in <see cref="Create"/>), because a Mission is a FLEET-level record whose source of truth is
    /// the Gateway and not this machine. The Gateway resolved the mission against its own TENANT-SCOPED
    /// store before sending the verb, and that resolution is the authorization; the Director stamps the
    /// attachment directly - no local lookup, no second opinion. An id with no name is refused, because it
    /// was never resolved by the only store that can answer.
    ///
    /// The Director deliberately does NOT re-validate a Gateway-supplied mission locally, and no longer has
    /// anything to re-validate it against. It could not: the old local store was a different (single-tenant,
    /// per-machine) set, so a mission that is real and owned was rejected for being absent from the wrong
    /// store - the failure issue #1548 fixed on the spawn path and issue #2629 hit again through a second
    /// spawn door.
    /// </summary>
    internal static DirectorCommandResult AttachMission(SessionManager sessionManager, string directorId, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<SetMissionRequest>(command.PayloadJson);

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // THE SEAT MOVES WITH THE MISSION, when the Gateway says it should (issue #2387 review).
        //
        // A Mission is also a RUN of the built-in "mission" workflow, and a mission-scoped spawn seats the
        // session on that run - which is what pins the conduct its preamble told it to follow. Changing the
        // mission link alone would leave a session DISPLAYED under one mission and GOVERNED by the one it
        // left. The seat therefore rides this same verb, so the two land together and a session is never
        // momentarily one and not the other.
        //
        // The DECISION is the Gateway's, not this Director's: whether the current seat belongs to the
        // mission being left is a fact about the run store, which lives at the Gateway. This applies what it
        // was told. MoveSeat=false means "the seat is not the mission's to take" and the seat is untouched.
        void ApplySeat()
        {
            if (request?.MoveSeat == true)
                session.SeatOnWorkflow(request.WorkflowRunId, request.WorkflowId, request.WorkflowVersion);
        }

        if (request?.MissionId is not Guid missionId)
        {
            // Blank/absent -> detach (mirrors set-role clearing the explicit role).
            session.AttachToMission(null, null);
            ApplySeat();
            FileLog.Write($"[SessionCommandExecutor] attach-mission: session={guid} detached " +
                          $"(seat {(request?.MoveSeat == true ? "cleared with it" : "left as it was")})");
            return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
        }

        if (string.IsNullOrWhiteSpace(request.MissionName))
        {
            FileLog.Write($"[SessionCommandExecutor] attach-mission REFUSED: session={guid} mission={missionId} " +
                          "arrived with no name, so it was never resolved by the Gateway");
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"mission '{missionId}' arrived without its name, so it was not resolved by the Gateway. " +
                "Missions live at the Gateway, not on this machine - attach through the Gateway " +
                "(cc-devthrottle mission attach), which resolves the mission and sends its name.");
        }

        // The Gateway resolved this mission inside the caller's own tenant; stamp it.
        session.AttachToMission(missionId, request.MissionName);
        ApplySeat();
        FileLog.Write($"[SessionCommandExecutor] attach-mission: session={guid} mission={missionId} (resolved by the Gateway)");
        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>Serialize a verb response DTO for <see cref="DirectorCommandResult.BodyJson"/>.</summary>
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Deserialize a verb request DTO from <see cref="DirectorCommand.PayloadJson"/> ("" =&gt; null).</summary>
    internal static T? Deserialize<T>(string? payloadJson) where T : class =>
        string.IsNullOrEmpty(payloadJson) ? null : JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
}
