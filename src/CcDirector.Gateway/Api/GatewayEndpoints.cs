using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Network;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Diagnostics;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Mobile;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

internal static class GatewayEndpoints
{
    /// <summary>
    /// Web-shaped options for the few places this file has to READ a JSON body a Director produced. The
    /// Director serializes verb results web-shaped (camelCase), so a default-cased reader would silently
    /// deserialize every property to null - a body that parsed and said nothing, which is worse than one
    /// that failed.
    /// </summary>
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How the audit trail opens the detail of a stop the Gateway SENT but never learned the outcome of
    /// (mission "Stop a session", inspection 1 finding I1). Recording nothing for such a stop was the
    /// defect; recording a plain stop for it would be a different untruth, because the Gateway did not
    /// establish that anything was stopped. The row says which it is, in words, in the operator's language.
    /// </summary>
    private const string StopOutcomeUnknownPrefix =
        "the stop was sent to the Director and what came of it is not known";

    /// <summary>
    /// The cause recorded when the caller hung up before the Director's answer reached the Gateway - the
    /// exact road inspection 1 reproduced: closing the desktop stop dialog cancels its request, and its
    /// client gives up after ten seconds. The stop is not undone by the caller leaving.
    /// </summary>
    private const string CallerLeftBeforeTheAnswer =
        "the caller went away before the answer arrived";

    /// <param name="onSessionState">Issue #186: receives every session-state observation
    /// (doorbell ping or heartbeat snapshot entry) as (directorId, sessionId, newState).
    /// The host feeds these to the turn-end watcher (voice auto-refresh, issue #549).</param>
    /// <param name="voiceAudioReadyFor">Issue #553: whether the Gateway has fetchable, playable
    /// cached audio for a session id (<c>WingmanVoiceService.HasVoice</c>), stamped onto
    /// <see cref="SessionDto.VoiceAudioReady"/>. Null leaves the field false.</param>
    /// <param name="needsYouStampFor">Issue #218 (MTR-10 Gap C: tenant-partitioned): given
    /// (tenant, sessionId, isRed) where tenant is the OWNING tenant of the row and isRed is the
    /// session's final EffectiveColor=="red" this refresh, returns the Gateway-owned UTC
    /// timestamp the session entered red (held while red, null when not red), stamped onto
    /// <see cref="SessionDto.NeedsYouSince"/>. Null (old callers) leaves
    /// the field null.</param>
    /// <param name="interruptedBriefFor">Issue #212 W3: the Gateway's last-known rail line +
    /// headline for a session id, used to enrich the Interrupted sessions list so a dead
    /// session is triageable. Reads the durable brief store, so it works even for a session
    /// whose Director has died.</param>
    /// <param name="briefHistoryFor">Issue #212 W4: the full turn-brief history for a session
    /// id, oldest first - the raw material the restore endpoint builds its continuation
    /// context from. Reads the durable brief store, so it serves dead sessions too.</param>
    /// <param name="directorEvents">Issue #330: the per-director event ring recording the
    /// doorbell event vocabulary (session-created/session-exited/prompt-detected) so the
    /// events are observable at GET /directors/{id}/events. Null (old callers, tests that
    /// don't care) records nothing and the events route serves empty lists.</param>
    /// <param name="turnJobs">Issue #376: the async voice-turn job store (singleton owned by
    /// <see cref="GatewayHost"/>). When present, the submit/poll routes are mapped via
    /// <see cref="GatewayVoiceTurnEndpoint"/>; null (old callers) maps nothing.</param>
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, string version, string token,
        // Hosted Multi-Tenancy (session-serving PR1): the auth-boundary tenant binder. On the hosted Gateway
        // the request-scoped reads resolve the caller's tenant from its authenticated device key and DENY
        // (403) when it has none - never falling back to Local. REQUIRED AND NON-NULLABLE (tenant-boundary
        // hardening, release 2026-07-31, findings CR-7 and I1-01): the boundary is a security argument, and
        // when it was optional one forgotten argument silently collapsed every hosted tenant into the Local
        // partition. Under nullable-warnings-as-errors, passing a possibly-null value here is a compile
        // error too. A self-host process constructs the boundary over the SingleTenantContext, which always
        // resolves Local - so there is no legitimate caller with nothing to pass.
        Tenancy.HostedTenantBoundary tenantBoundary,
        bool authEnabled = false, Func<bool>? requestShutdown = null,
        Action<string, string, string>? onSessionState = null,
        Func<TenantId, string, bool>? voiceGeneratingFor = null,
        Func<TenantId, string, bool>? voiceAudioReadyFor = null,
        Func<TenantId, string, Core.HostedAi.HostedAiState?>? voiceUnavailableFor = null,
        Func<TenantId, string, bool>? nothingToNarrateFor = null,
        Func<TenantId, string, bool>? directorCannotSendConversationFor = null,
        /// <summary>Issue #2676: this turn's narration was abandoned - the model did not answer and the voice
        /// path's bounded re-attempts are spent, so nothing further is scheduled. Feeds the folded
        /// VoiceDisplay so the screen stops promising audio nobody is making.</summary>
        Func<TenantId, string, bool>? narrationAbandonedFor = null,
        Func<TenantId, string, bool>? servedViaFallbackFor = null,
        /// <summary>Issue #2576: stamps and returns SessionDto.VoiceWaitingSince - when this session's wait
        /// for voice began, or null when it is not waiting. Null delegate leaves the field unset, so a caller
        /// that has no voice state (a test, a diagnostic route) is unchanged.</summary>
        Func<TenantId, string, bool, DateTime?>? voiceWaitingStampFor = null,
        Func<TenantId, string, bool, DateTime?>? needsYouStampFor = null,
        Func<TenantId, string, bool>? transcribingFor = null,
        Func<TenantId, string, string?>? dictationStatusFor = null,
        Transcription.TranscribingSessions? transcribingSessions = null,
        Func<string, (string? RailLine, string? Headline)>? interruptedBriefFor = null,
        Func<string, List<TurnBriefDto>>? briefHistoryFor = null,
        SessionOwnerCache? owners = null,
        Gateway.Events.DirectorEventLog? directorEvents = null,
        Voice.GatewayTurnJobStore? turnJobs = null,
        Pairing.DeviceRegistry? devices = null,
        // Network Diagnostics mission (P1): the shared hourly quality rollup that POST /diag/result folds
        // client speed-test results into (home/away split on the measured path). The monitor folds into the
        // same instance. Null in tests / when diagnostics are off.
        NetDiagRollupStore? netDiagRollup = null,
        // Issue #1176 (Phase 1a): the roster source. Epic #1159 step A: /sessions serves a Director's LAST
        // KNOWN sessions from this store unconditionally - age no longer decides whether a machine is served,
        // only what the roster says about it. streamStaleAfter still decides which serves count as confirmed
        // live, which is what the destructive consumers are gated on. Null leaves the endpoint with no roster
        // source at all, and every registered Director surfaces as a machine error with nothing served.
        Streaming.PushedSessionStore? pushedSessions = null,
        Streaming.PushedRepositoryStore? pushedRepositories = null,
        Streaming.RepoHistoryStore? repoHistory = null,
        TimeSpan? streamStaleAfter = null,
        // Issue #1177 (Phase 1): when non-null, per-session commands are first tried DOWN the Director's
        // stream via this hook (GatewayHost.SendCommandAsync); a null return means the Director is not
        // stream-connected, which the endpoint surfaces as a 502 - there is no HTTP call to fall back to.
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = null,
        // Issue #1292: the fleet-wide session-number authority. When non-null, the Director-facing
        // /session-numbers/* endpoints are mapped and the /sessions aggregation adopts every observed
        // number so the in-use set survives a Gateway restart. Null (old callers, tests) maps nothing
        // and leaves each Director to number locally.
        Discovery.FleetSessionNumberAllocator? sessionNumbers = null,
        // DevThrottle Stats: the always-available input-tally aggregator. Folded from the assembled
        // /sessions roster (the path that carries SessionDto.InputStats whether stream mode is on or off),
        // so "Your Throttle" is fed by the same roster the fleet already reads, not only by the SignalR
        // push path (which is unmapped when stream mode is off). Null (old callers, tests) folds nothing.
        // A RESOLVER, not an instance (round-two finding 1). On hosted the aggregator arrives when the
        // statistics store publishes its factory, which may be AFTER these routes are mapped. Capturing the
        // instance froze the answer for the roster while the stats feed resolved correctly per request - so
        // a store that published late served a tenant a working /stats/data over a roster that recorded
        // nothing into it. Statistics served and statistics never written is the worst of both.
        Func<Stats.GatewayInputStatsAggregator?>? inputStats = null,
        // DevThrottle Stats: the durable fleet concurrency record. Observed from the same assembled roster
        // (live count + actively-working count), so the peak is captured fleet-wide whether stream mode is
        // on or off. Null (old callers, tests) records nothing.
        Func<Stats.ISessionConcurrencyRecorder?>? concurrency = null,
        // Snooze Length mission: the Gateway-owned snooze registry. POST /sessions/{sid}/hold REQUIRES it -
        // it records/clears a snooze-until here (the authoritative hold) and the /sessions fold reads it to
        // return an EXPIRED snooze to "needs you" (OnHold=false) on its own even if its Director has died.
        // When null the hold endpoint returns 503: there is no plain-forward fallback - the Gateway owns hold.
        Snooze.SnoozeRegistry? snoozeRegistry = null,
        // Issue #2662: the Gateway-owned hand-raise registry - a supervised session's "I need my supervisor".
        // POST /sessions/{sid}/needs-manager REQUIRES it (503 when null, no fallback: the Gateway owns this
        // the same way it owns hold), and the /sessions fold reads it to stamp NeedsManager + the reason.
        // Null (old callers, tests) leaves every hand down, which is the honest answer when nothing records
        // them - never a silently-inert flag that reads as "nobody needs anything".
        Fleet.HandRaiseRegistry? handRaises = null,
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store. When non-null, the
        // POST/GET /missions routes are mapped and a mission-scoped spawn validates against it. Missions are
        // a fleet-level concept, so the source of truth lives here at the Gateway. Null (old callers, tests)
        // maps nothing, leaving missions to the Director's own /missions routes (unchanged this phase).
        Core.Sessions.MissionStore? missions = null,
        // Workflows mission (phase 4, issue #1771): when non-null, creating a mission also opens a
        // workflow RUN of the built-in "mission" workflow, pinned to its published version, and the
        // created mission's DTO carries the additive workflowRunId. Null (old callers, tests) leaves
        // mission creation byte-identical to before.
        Workflows.WorkflowRunStore? workflowRuns = null,
        // Store injection points: the host owns a single key vault, transcription history, and audio
        // archive and passes them here so the phone-recorder ingest transcriber (RecordingEndpoints) uses
        // the host's instances rather than newing its own. Null (old callers, tests) leaves RecordingEndpoints
        // to build its own defaults, byte-identical to before.
        Core.KeyVault? recordingKeyVault = null,
        Transcription.TranscriptionHistoryLog? transcriptionHistory = null,
        Transcription.TranscriptionAudioArchive? transcriptionAudioArchive = null,
        // Round 4 finding 1: the reliable display-state channel, so the hold endpoint can TRIGGER a prompt
        // push of the folded HoldState after a snooze / unsnooze instead of sending its own second hold
        // command. This makes FleetDisplayStateObserver the single writer of the Director's raw hold. Null
        // (old callers, tests) leaves the endpoint to record the registry only, and the periodic sweep
        // reconciles the desktop.
        Fleet.FleetDisplayStateObserver? fleetDisplayState = null,
        // Production-readiness B2 (process-control): the seam the DELETE /directors/{id} FORCE-KILL branch
        // calls to kill a Director's process tree by pid. Null (production) uses the real
        // Process.GetProcessById(pid).Kill(entireProcessTree:true). A test injects a recorder that observes
        // the kill WITHOUT actually killing anything - so "did the force-kill reach the process by that pid"
        // is a DIRECT assertion, exactly as OnShutdownRequested lets the shutdown proof observe the handler.
        Func<int, bool>? forceKillDirectorTree = null,
        Func<TailscaleDiagnostics.NetworkDiag>? collectNetworkDiagnostic = null,
        // Issue #2017: the per-tenant settings resolver. This branch owns the snooze-default consumer at
        // POST /sessions/{sid}/hold - when non-null it reads the CALLER's tenant default via
        // SnoozeDefaultMinutes(tenant) instead of the process-global config. Null (older callers, tests that
        // do not exercise the default) keeps the global read, matching every other optional store here.
        Settings.TenantSettingsResolver? tenantSettings = null,
        // Issue #2022: the live process diagnostics the About page shows read-only on both surfaces, after the
        // machine settings left the Cockpit Settings page. Supplied by the host as its own StartedAtUtc and
        // Port. Null (older callers, bare test hosts) means "not started". The run-mode label went with the
        // Director facts when About became a server-versions page (owner ruling 2026-07-26): the served
        // bundles' build stamps say which build this is far more precisely than "managed" versus "dev" did.
        DateTime? gatewayStartedAtUtc = null,
        // Issue #2161: a delegate, resolved per request - Map runs before the listener binds, and on an
        // operating-system-assigned port the number does not exist yet.
        Func<int>? gatewayPort = null,
        // devthrottle #2075: the dictionary-suggestions engine and the dismissal store, threaded into the
        // /ingest/dictionary/suggestions routes. Null (older callers, recording-only test harnesses) simply
        // does not expose the suggestion routes; production wires both.
        Transcription.DictionarySuggestionService? dictionarySuggestions = null,
        Transcription.DictionarySuggestionDismissalStore? dictionaryDismissals = null,
        // Composes the daily email's suggestions block for the caller's tenant. Null (older callers,
        // recording-only test harnesses) simply does not expose the email-block route.
        Transcription.SuggestionEmailComposer? suggestionEmailComposer = null,
        // Issue devthrottle_internal#1195: the wingman brain, the JUDGE the menu guard consults before it
        // refuses a prompt - the pure classifier only trips the question, it never convicts on its own.
        // Null (older callers, tests without a brain) makes a tripwire-positive screen refuse outright:
        // fail closed, because the guard exists to keep an Enter out of a real picker.
        Wingman.WingmanTranslator? wingmanTranslator = null,
        // Remove-the-network-port mission, phase 2: the fleet-message steward (dedupe plus a per-sender rate
        // limit on outgoing messages), consulted by POST /sessions/{sid}/message. It used to sit on the
        // Director, because the command line reached the fleet through its own Director's loopback port; with
        // that port going away the check has to move to the end still in the path. Null (older callers,
        // tests, a Gateway with the steward switched off) ALLOWS every message, byte-identical to today.
        Core.Fleet.MessageSteward? messageSteward = null,
        // Whether the database is connected yet (issue #2383's real fix). A delegate, not a bool: Map
        // runs before the listener binds and the database is opened AFTER it, so the value is not known
        // here. Null means "assume ready", which is what every self-host and test caller wants.
        Func<bool>? databaseReady = null,
        History.KnownRepositoryStore? knownRepositories = null,
        // Per-subsystem readiness for /healthz: the parts of the Gateway that can be down on their own
        // while the process serves normally. A delegate, not a snapshot, for the same reason databaseReady
        // above is one - the statistics store is allowed to come up (and now to come BACK) long after Map
        // ran, and a value read here would freeze the answer at startup. Null computes none, which OMITS
        // the block; that is what every self-host and test caller gets, and it is honest: a responder that
        // was given nothing to report must not report that everything is fine. See HealthDto.Subsystems.
        Func<IReadOnlyDictionary<string, string>>? subsystems = null,
        // Inspection finding I2-03 ("Clean up Your Throttle", 2026-09-05): the Gateway's own record of what it
        // transcribed, so a prompt's spoken claim is verified against it rather than trusted. Null (older
        // callers, tests) means no claim can ever be honoured - fail closed, every prompt is typed.
        Voice.SpokenClaimRegistry? spokenClaims = null,
        // Mission "Stop a session": the append-only governance audit trail a stop is recorded in. The owner
        // allowed any session to stop any other ON THE GROUND THAT IT IS AUDITED, so this is load-bearing.
        // Nullable only so a test can map the routes without a database - and a null is never silent: a stop
        // served with nothing wired logs loudly that it went unrecorded, because a governance check whose
        // pass condition is an absence certifies a run that never happened.
        Governance.GovernanceAuditLog? governanceAudit = null)
    {
        // The old issue #1188 "session lock" (423 Locked on human input while a PENDING dictation record
        // existed) was removed deliberately (issue #1308). This is a single-operator tool: a collision
        // between the operator's own inbound dictation and their own typed send is theirs to make, not
        // the Gateway's to police - and a wedged PENDING marker used to falsely block every send for its
        // whole lifetime. The marker itself stays (it paints the roster's orange "receiving a dictation").

        // Issue #1177 (Phase 4a): the freshness window used both by /sessions (pushed-cache serve) and by
        // LocateSessionAsync (pushed-cache session location). Resolved once here so every session endpoint's
        // owner lookup shares the exact window the roster uses. When stream mode is off pushedSessions is null,
        // so this value is never consulted and location stays on the HTTP pull, byte-identical to today.
        var streamStaleResolved = streamStaleAfter ?? TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);

        // Issue #1229: the Hub's broadcast governance state - the human-issued grant store and the
        // per-sender broadcast rate limiter. One instance per Gateway process, shared by the grant-mint
        // endpoint and the /fanout guard below. The pure scope rule lives in FleetBroadcastPolicy.
        var broadcastGovernor = new BroadcastGovernor();

        // Gateway Cleanup mission, Phase 2 (PR E-B): the async voice-turn submit/poll surface (issue #376)
        // is RETIRED. It drove the Director's SSE /sessions/{sid}/voice-turn endpoint over a raw HTTP dial
        // (a Gateway->Director dial the tunnel-only endgame must remove), and it is CLIENT-DEAD - its only
        // caller was the retired native MAUI phone client; cockpit and mobile both use /wingman/voice-turn,
        // which runs the whole turn Gateway-side. The Gateway endpoint + its two dedicated tests are deleted;
        // the Director SSE endpoint is on the Phase 1 deletion DROP list, removed at the cut.

        // Issue #1292: the per-tenant session-number authority. A Director asks for a number when it
        // creates a session (so the number is unique across every Director THAT TENANT owns) and frees
        // it when the session ends. Guarded by the same auth middleware as every other Director-facing
        // route, so the Director's own fleet credential is required.
        //
        // Audit H2: the allocator is partitioned by tenant, so both routes resolve the caller's OWN tenant
        // (server-side, from the authenticated device key - never from the request body) and touch only
        // that tenant's partition. A request whose device key binds to no tenant is DENIED on hosted rather
        // than served the Local partition; self-host always resolves to Local, unchanged.
        if (sessionNumbers is not null)
        {
            app.MapPost("/session-numbers/allocate", (SessionNumberAllocateRequest req, HttpContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.SessionId))
                    return Results.BadRequest(new { error = "sessionId is required" });
                var tenant = ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no tenant is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);
                var number = sessionNumbers.Allocate(tenant.Value, req.SessionId, req.DirectorId ?? "");
                return Results.Ok(new SessionNumberAllocateResponse { Number = number });
            });

            app.MapDelete("/session-numbers/{sessionId}", (string sessionId, HttpContext ctx) =>
            {
                var tenant = ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no tenant is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);
                sessionNumbers.Release(tenant.Value, sessionId);
                return Results.NoContent();
            });
        }

        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission surface. Missions are a fleet-level
        // concept (they span Directors and machines), so the source of truth lives here at the Gateway -
        // like fleet messaging and scheduling - and mission-existence VALIDATION lives here now.
        // These routes inherit the host-wide token middleware, exactly like /cron/jobs and /session-numbers.
        // The Director's own /missions routes stay until a later phase; this is the additive equivalent.
        //   POST /missions        body { missionName } -> 201 MissionDto | 400
        //   GET  /missions        -> [ MissionDto ]
        //   GET  /missions/{mid}  -> MissionDto | 404
        if (missions is not null)
        {
            app.MapPost("/missions", (NewMissionRequest req, HttpContext ctx) =>
            {
                FileLog.Write($"[GatewayEndpoints] POST /missions: name=\"{req?.MissionName}\"");
                if (req is null || string.IsNullOrWhiteSpace(req.MissionName))
                    return Results.BadRequest(new { error = "missionName is required" });

                // #1039: stamp the CALLER's own tenant on the record at write time. A read-side filter
                // alone would be a deferred leak - unattributed rows would keep accumulating behind it,
                // which is exactly how the shared store came to hold several accounts' missions.
                var tenant = ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no tenant is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);

                // Workflows mission (phase 4, issue #1771): a mission IS a run of the built-in
                // "mission" workflow. The EXPECTED failure (mission workflow unrunnable) is checked
                // BEFORE the Mission record is written, so it cannot leave a mission behind with no
                // governance run. The Mission store and the run store are two different stores (JSON
                // and EF), so a process death exactly between the two writes can still orphan a
                // mission - a transition-era window that closes when the JSON mission store retires
                // onto the EF layer; the pre-check removes every failure mode short of that.
                // The owner's switch (register redesign ruling): a mission whose workflow the
                // owner EXPLICITLY turned off still gets created - it runs UNGOVERNED (no run
                // record) until the switch flips back. Three-valued on purpose: only an explicit
                // FALSE is the owner's choice; a MISSING mission workflow (null - a broken or
                // unseeded store) keeps the fail-loud path below, because silently ungoverned
                // missions are exactly the gap the outcome spine exists to close.
                var missionWorkflowEnabled = workflowRuns?.GetWorkflowEnabled("mission") ?? true;
                if (workflowRuns is not null && missionWorkflowEnabled != false)
                {
                    try
                    {
                        workflowRuns.EnsureRunnable("mission");
                    }
                    catch (Workflows.WorkflowValidationException ex)
                    {
                        FileLog.Write($"[GatewayEndpoints] POST /missions refused: {ex.Message}");
                        return Results.BadRequest(new { error = ex.Message });
                    }
                }

                var mission = missions.Create(tenant.Value, req.MissionName);
                var dto = ToMissionDto(mission);
                if (workflowRuns is not null && missionWorkflowEnabled != false)
                {
                    try
                    {
                        var run = workflowRuns.Create(
                            "mission", mission.MissionName, missionId: mission.MissionId);
                        dto.WorkflowRunId = run.Id;
                    }
                    catch (Workflows.WorkflowValidationException)
                        when (workflowRuns.GetWorkflowEnabled("mission") == false)
                    {
                        // The owner flipped the switch between the pre-check and the run create.
                        // The mission record already exists, and the ruling says an explicit OFF
                        // makes an UNGOVERNED mission - so honor the flip instead of returning an
                        // error for a mission that was in fact created.
                        FileLog.Write($"[GatewayEndpoints] POST /missions: the mission workflow was " +
                                      $"turned OFF mid-create - mission {mission.MissionId} is UNGOVERNED");
                    }
                }
                else if (workflowRuns is not null)
                {
                    FileLog.Write($"[GatewayEndpoints] POST /missions: the mission workflow is OFF - " +
                                  $"mission {mission.MissionId} created UNGOVERNED (no run record)");
                }
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            });

            // #1039: the list is the caller's OWN missions. It used to be missions.List() - every account's
            // missions, served to every account, on one shared hosted store. A request that resolves to no
            // tenant is DENIED (403), never served the Local partition.
            // ACTIVE ONLY by default. ?state=complete|removed|active returns exactly that state, and
            // ?state=all returns every one. An existing caller that knows nothing about states gets a
            // shorter, correct list rather than one padded with finished work - the safe direction to be
            // wrong in, and what every current caller actually wants.
            app.MapGet("/missions", (HttpContext ctx, string? state) =>
            {
                var tenant = ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no tenant is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);

                var wanted = (state ?? "").Trim().ToLowerInvariant();
                var all = wanted == "all";
                if (!all && wanted.Length > 0 && Core.Sessions.MissionStates.Normalize(wanted) is null)
                    return Results.BadRequest(new
                    {
                        error = "state must be one of: active, complete, removed, all",
                    });

                var list = missions.List(
                    tenant.Value,
                    state: all || wanted.Length == 0 ? null : wanted,
                    includeEnded: all);
                return Results.Json(list.Select(ToMissionDto).ToList());
            });

            // #1039: resolve INSIDE the caller's own tenant. A mission id that belongs to another account
            // answers 404 - the same answer as an id that does not exist - so an id cannot be probed for
            // existence, let alone read.
            app.MapGet("/missions/{mid}", (string mid, HttpContext ctx) =>
            {
                if (!Guid.TryParse(mid, out var missionId))
                    return Results.BadRequest(new { error = "invalid mission id format" });

                var tenant = ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no tenant is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);

                var mission = missions.Get(tenant.Value, missionId);
                return mission is null
                    ? Results.NotFound(new { error = "mission not found" })
                    : Results.Json(ToMissionDto(mission));
            });

            // PATCH /missions/{mid}  body { why } -> 200 MissionDto | 400 | 403 | 404
            //
            // Set or clear a mission's WHY. A blank why CLEARS it, returning the card to its "no why set"
            // flag - the same "empty means unset" rule the old note store had, kept so the observable
            // behaviour does not change under the owner.
            //
            // This route REPLACES PUT /gateway/missions/notes, which keyed the WHY by the mission's
            // lower-cased NAME. Two differences matter beyond the key:
            //
            //  * It resolves the CALLER's tenant explicitly, exactly like GET /missions/{mid} above, and
            //    refuses when none is bound. The old notes route did no tenant resolution of its own at all
            //    - it leaned on an ambient query filter - so this is a strictly stronger boundary, not a
            //    port of the same one.
            //  * An unknown mission and another tenant's mission answer IDENTICALLY (404). The id alone
            //    reaches nothing, and cannot be probed for existence.
            //
            // Phase 2 grows this same route with missionName (rename) and state (complete/removed). It is
            // deliberately a PATCH from the start so those are added fields rather than a second route.
            app.MapPatch("/missions/{mid}", async (string mid, HttpContext ctx) =>
            {
                if (!Guid.TryParse(mid, out var missionId))
                    return Results.BadRequest(new { error = "invalid mission id format" });

                var tenant = ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no tenant is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);

                MissionPatchRequest? req;
                try
                {
                    req = await ctx.Request.ReadFromJsonAsync<MissionPatchRequest>();
                }
                catch (System.Text.Json.JsonException)
                {
                    return Results.BadRequest(new { error = "the request body is not valid JSON" });
                }

                // Nothing to change is a client mistake worth naming, not a silent success that looks like
                // the edit was applied.
                if (req is null || (req.Why is null && req.MissionName is null && req.State is null))
                    return Results.BadRequest(new { error = "one of why, missionName or state is required" });

                // Reject a blank NAME up front rather than storing one: a mission nobody can refer to is
                // not a rename, it is a broken record. (A blank WHY is different - that is the clear path.)
                if (req.MissionName is not null && string.IsNullOrWhiteSpace(req.MissionName))
                    return Results.BadRequest(new { error = "missionName cannot be blank" });

                var wantedState = req.State is null ? null : Core.Sessions.MissionStates.Normalize(req.State);
                if (req.State is not null && wantedState is null)
                    return Results.BadRequest(new
                    {
                        error = "state must be one of: active, complete, removed",
                    });

                var now = DateTimeOffset.UtcNow;
                Core.Sessions.Mission? updated = null;

                if (req.Why is not null)
                    updated = missions.SetWhy(tenant.Value, missionId, req.Why, now);

                if (req.MissionName is not null)
                    updated = missions.Rename(tenant.Value, missionId, req.MissionName) ?? updated;

                if (wantedState is not null)
                    updated = missions.SetState(tenant.Value, missionId, wantedState, now) ?? updated;

                // Every accessor above answers null for BOTH an unknown mission and another tenant's, so a
                // null here is a 404 - the same answer either way, and the id cannot be probed.
                if (updated is null)
                {
                    FileLog.Write($"[GatewayEndpoints] PATCH /missions/{mid}: unknown to this tenant");
                    return Results.NotFound(new { error = "mission not found" });
                }

                var result = new MissionPatchResultDto { Mission = ToMissionDto(updated) };

                // ===== ENDING A MISSION ALSO ENDS ITS WORKFLOW RUN =====
                //
                // A Mission is also a RUN of the built-in "mission" workflow. Leaving the run open while the
                // mission is finished would leave the fleet governed by conduct for work that is over, and
                // would keep it in every "what is still running" answer the run store gives.
                //
                // THE TRANSITION TABLE IS REAL AND HAS TO BE OBEYED. A run goes
                // created -> active -> succeeded, so "complete" cannot be applied to a run still sitting at
                // created in one move. Every mission run on this fleet IS still at created, because nothing
                // has ever advanced one - so the common case is precisely the one that needs two steps. The
                // intermediate move is not a fiction: sessions really were seated on that run and it really
                // was in use; the transition was simply never recorded.
                //
                // A run that cannot be advanced does NOT block the ending. The mission's own state is the
                // primary fact and it is already stored; the run is a second store. But the caller is TOLD,
                // in the same shape MissionAttachResultDto reports a seat, because only the Gateway can see
                // what happened here and a caller told nothing would report a clean ending it cannot vouch for.
                if (wantedState is Core.Sessions.MissionStates.Complete or Core.Sessions.MissionStates.Removed
                    && workflowRuns is not null)
                {
                    var terminal = wantedState == Core.Sessions.MissionStates.Complete
                        ? WorkflowRunStatus.Succeeded
                        : WorkflowRunStatus.Abandoned;
                    result.Note = EndMissionRun(workflowRuns, missionId, terminal);
                }

                FileLog.Write($"[GatewayEndpoints] PATCH /missions/{mid}: " +
                              $"why={(req.Why is null ? "unchanged" : updated.Why.Length == 0 ? "cleared" : "set")} " +
                              $"name={(req.MissionName is null ? "unchanged" : "renamed")} " +
                              $"state={updated.State}");
                return Results.Json(result);
            });

            FileLog.Write("[GatewayEndpoints] mapped Gateway-native /missions routes");
        }

        // Issue #469 closed the secret-embedding phone-pairing QR endpoints (/pair/qr.png and
        // /pair/payload) that put the shared fleet token directly in a QR/link - a full compromise
        // if leaked. They are removed; a request to them now falls through to a 404 (no secret is
        // exposed anywhere over the network). Device enrollment uses the per-device pairing-code
        // flow (DeviceEnrollmentEndpoint, wired in GatewayHost): the key never travels in a QR or
        // link, only the short-lived code shown on the Gateway host's own local window does.

        // Graceful exit for the self-update helper: answer first (so the caller gets its 200),
        // then hand off to the host's shutdown handler shortly after. 501 when the hosting
        // process wired no handler - this endpoint never half-stops the host on its own.
        //
        // HOSTED DENY (production-readiness B2). This route triggers a PROCESS-WIDE shutdown of the whole
        // Gateway. On self-host that is exactly right - the single owner's self-update helper POSTs it to
        // make the process exit so the exe unlocks. On the HOSTED Gateway the process is SHARED
        // infrastructure serving every tenant, and this route has no per-tenant meaning and no owner check,
        // so any authenticated tenant's device key could POST /shutdown and take the Gateway down for
        // everyone else. It is therefore refused on hosted through the shared refusal primitive - the same
        // boundary #1904 adopted for /vault - which on hosted maps a verb-less refusal in place of the
        // handler and never binds it, while off hosted maps the real handler byte-identically to before.
        // ExclusiveGroup because /shutdown owns its prefix outright, so the one catch-all also covers any
        // process-control route added beneath it later without a fresh deny.
        var shutdownGroup = Tenancy.HostedRouteDeny.ExclusiveGroup(app, "/shutdown", ShutdownHostedDenial());
        shutdownGroup.MapPost("", () =>
        {
            FileLog.Write("[GatewayEndpoints] POST /shutdown");
            if (requestShutdown is null)
                return Results.Json(new { error = "shutdown not supported by this host" }, statusCode: StatusCodes.Status501NotImplemented);

            _ = Task.Run(async () =>
            {
                await Task.Delay(250); // let the 200 flush before the host starts tearing down
                if (!requestShutdown())
                    FileLog.Write("[GatewayEndpoints] /shutdown: no handler registered; nothing stopped");
            });
            return Results.Json(new { shuttingDown = true });
        });

        var logoutVisibility = authEnabled ? "" : "style=\"display:none\"";

        // Phone recorder ingest (offline-recorded audio -> transcription -> vault).
        RecordingEndpoints.Map(app, tenantBoundary, recordingKeyVault, transcriptionHistory, transcriptionAudioArchive,
            dictionarySuggestions, dictionaryDismissals, suggestionEmailComposer);

        // Read-only view of the Communication Manager approval queue (see the phone's
        // pending drafts remotely). Step 1 of centralizing the comm queue on the Gateway.
        // HOSTED DENY (CR-6): the queue is one process-global SQLite with no tenant anywhere, so on
        // hosted the whole /comm-queue family is refused through the shared refusal primitive
        // (HostedRouteDeny.ExclusiveGroup, inside CommQueueEndpoints.Map); self-host is untouched.
        CommQueueEndpoints.Map(app);

        // Local-machine exe/slot management (the "Exes" page). Defect 6: it gets the snooze registry so its
        // fleet pass applies the SAME expired-snooze override the roster applies - without it the page says
        // "Snoozed" while the roster says "Needs you".
        // Windows-only: the whole surface builds developer slot exes by shelling out to
        // powershell.exe scripts/local-build-avalonia.ps1 against a local_builds directory, which
        // exists only on a Windows dev box. Off Windows the routes are simply not mapped.
        if (OperatingSystem.IsWindows())
            ExesEndpoints.Map(app, registry, pushedSessions, streamStaleResolved, snoozeRegistry);

        // ===== HTML pages =====
        // The Gateway serves NO UI pages anymore (docs/plans/one-url-cockpit.md): "/" and every
        // other UI path fall through to the Cockpit via the fallback proxy. Only the token
        // login/logout pair remains (it guards the Gateway itself when auth is enabled). It lives in
        // GatewayLoginEndpoint, which bind-breaks the whole /login surface on hosted (MH-2) and routes the
        // self-host cookie write through the single GatewayTokenCookie helper.
        GatewayLoginEndpoint.Map(app, token);

        // ===== REST =====
        app.MapGet("/healthz", () =>
        {
            // NOT READY UNTIL THE DATABASE IS OPEN, and this is load-bearing rather than cosmetic.
            //
            // The listener now binds BEFORE the database is connected, so that a slow database can never
            // push the bind past the platform's container-start deadline and make it stop the site (#2383,
            // #2585). That fixes the outage, but it creates a window in which this process is listening and
            // cannot serve data - and /healthz is what the deploy warms on (it polls staging for a sustained
            // 200 carrying the new commit) and what the platform's own swap warm-up gate pings. Answering
            // 200 in that window would hand production to a Gateway that cannot read anything, which is a
            // worse outage than the one being fixed.
            //
            // 503 is the honest answer and it costs nothing: the warm-up simply keeps polling, which is what
            // a warmed slot swap is for.
            if (databaseReady is not null && !databaseReady())
            {
                return Results.Json(new HealthDto
                {
                    Status = "starting",
                    Version = version,
                    Commit = Environment.GetEnvironmentVariable("COCKPIT_COMMIT"),
                    Subsystems = subsystems?.Invoke(),
                    ServerTime = DateTime.UtcNow,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // The exact commit this image was built from (COCKPIT_COMMIT is baked into the container as an
            // ENV in the Dockerfile). NULL when unstamped (e.g. a local dev run) - HealthDto omits it then.
            // The deploy pipeline polls /healthz until this equals the commit it just shipped: that is the
            // honest "the new image is now serving" signal, because the OLD container keeps answering 200
            // until the very moment it is recycled. Reported on both the hosted and self-host branches -
            // build identity is the same for every tenant, so it carries no per-tenant fact.
            var commit = Environment.GetEnvironmentVariable("COCKPIT_COMMIT");

            // Hosted Multi-Tenancy (session-serving PR2): /healthz is PUBLIC - it is the unauthenticated
            // liveness probe every Director and endpoint selector dials, so it carries no credential and
            // therefore has NO TENANT. On the hosted Gateway the fleet counts below are fleet-GLOBAL: an
            // anonymous caller reading "directors: 2" is reading an aggregate over every account's Directors.
            // That is a cross-tenant leak, and it cannot be fixed by making the count tenant-aware, because a
            // request with no tenant has no correct number to print. So on hosted the aggregate is not
            // computed at all - deny-by-default applies to metrics exactly as it applies to data. Liveness
            // (status, version, server time) is what a probe actually needs and stays public.
            //
            // Self-host is untouched: one tenant, one owner, and the counts are what the Director's own
            // connectivity self-test and the settings gateway probe read.
            //
            // Gated on the PROCESS-level hosted flag, never on the nullable boundary argument: a caller
            // passing a literal null! boundary on a hosted process must not reopen the aggregate.
            if (GatewayHostedMode.IsHosted)
            {
                // Directors/Sessions left NULL, which OMITS them from the JSON (HealthDto). Leaving them to
                // serialize as 0 would state a fleet of zero to every probe on hosted - false rather than
                // merely absent, and this is the endpoint the Director's connectivity self-test reads.
                return Results.Json(new HealthDto
                {
                    Status = "ok",
                    Version = version,
                    Commit = commit,
                    Subsystems = subsystems?.Invoke(),
                    ServerTime = DateTime.UtcNow,
                });
            }

            // Self-host status only (this branch is not reached on hosted): the single tenant is Local.
            var directors = registry.ListDirectors(TenantId.Local);
            // Post-cut: the roster lives ONLY in the push store, so count from there. A Director with no
            // fresh pushed snapshot is not connected to the tunnel and contributes zero.
            int totalSessions = directors.Sum(d =>
            {
                // Self-host only (see above): the single tenant is Local.
                var cached = pushedSessions?.TryGetFresh(TenantId.Local, d.DirectorId, streamStaleResolved);
                return cached?.Count ?? 0;
            });

            return Results.Json(new HealthDto
            {
                Status = "ok",
                Directors = directors.Count,
                Sessions = totalSessions,
                Version = version,
                Commit = commit,
                Subsystems = subsystems?.Invoke(),
                ServerTime = DateTime.UtcNow,
            });
        });

        // ===== Network diagnostics (auto-network-switching mission) =====
        // Back the mobile Diagnostics page so the owner can measure the phone-to-Gateway path from the
        // phone itself (a phone cannot run `tailscale ping`). These routes are gated like the rest of the
        // data API - the page calls them with its per-device key - so they are not an open bandwidth tap.

        // GET /diag/echo: report what the Gateway sees about the caller's connection. RemoteIpAddress
        // reflects X-Forwarded-For (UseForwardedHeaders trusts ONLY the loopback tailscale-serve proxy),
        // so it is the phone's tailnet 100.x address through the front door and its 192.168.x LAN address
        // on a direct hit - the one clean signal that says "you are relaying through Tailscale" vs "you are
        // direct on the LAN". Also hands back the Gateway's own LAN IP and tailnet name so the page can
        // show where a direct path would point.
        app.MapGet("/diag/echo", (HttpContext ctx) => Results.Json(new NetDiagEchoDto
        {
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ClientPath = NetDiag.ClassifyClientIp(ctx.Connection.RemoteIpAddress),
            ForwardedFor = ctx.Request.Headers["X-Forwarded-For"].ToString(),
            Host = ctx.Request.Host.Value ?? "",
            MachineName = Environment.MachineName,
            GatewayLanIp = LanIdentity.TryGetPrimaryLanIpv4(),
            GatewayTailnetName = TailscaleIdentity.TryGetMagicDnsName(),
            ServerTime = DateTime.UtcNow,
        }));

        // GET /diag/payload?bytes=N streams N bytes of incompressible data so the phone can time a DOWNLOAD
        // and derive throughput. Size is clamped so the endpoint cannot be turned into a bandwidth
        // amplifier, and the response is no-store so a proxy or the service worker never serves a cached
        // copy that would fake the number.
        app.MapGet("/diag/payload", (HttpContext ctx, int? bytes) =>
        {
            int size = Math.Clamp(bytes ?? NetDiag.DefaultPayloadBytes, 0, NetDiag.MaxPayloadBytes);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(NetDiag.BuildPayload(size), "application/octet-stream");
        });

        // POST /diag/payload reads and discards the request body and returns the byte count, so the phone
        // can time an UPLOAD (the direction that carries dictation audio) and derive throughput.
        app.MapPost("/diag/payload", async (HttpContext ctx) =>
        {
            long received = 0;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer)) > 0)
                received += read;
            return Results.Json(new { received });
        });

        // GET /diag/network: the Gateway-owned finished connection verdict plus the underlying self-hosted
        // Tailscale diagnostic. Hosted browsers reach this shared Gateway over the public internet, so a
        // tailnet diagnostic is neither relevant nor tenant-safe there: answer Connected from the successful
        // request itself without invoking Tailscale or returning the host's shared peer inventory. Self-hosted
        // mode keeps the direct-versus-relay diagnostic and folds it here, before the response reaches a client.
        var connectionVerdicts = new NetworkConnectionVerdictFold();
        app.MapGet("/diag/network", async (HttpContext ctx) =>
        {
            if (GatewayHostedMode.IsHosted)
                return Results.Json(TailscaleDiagnostics.HostedConnection());

            var collector = collectNetworkDiagnostic ?? TailscaleDiagnostics.Collect;
            var diag = await Task.Run(collector);
            return Results.Json(connectionVerdicts.Fold(diag, ctx.Connection.RemoteIpAddress));
        });

        // GET /diag/ping: the featherweight latency endpoint the client's latency loop hits. Unlike
        // /diag/echo it does NO network-interface scan or Tailscale lookup, so its round trip is the wire
        // time, not server work - keeping the reported latency honest.
        app.MapGet("/diag/ping", () => Results.Json(new { t = DateTime.UtcNow }));

        // GET /diag/loadmetrics: the Stage 0 load-test instrumentation window (issue #1173) - lock wait,
        // fold duration, sweep overlap, hub connection and push-ingress pressure, roster latency, and the
        // standard process numbers. Behind the same auth gate as every other route. `?reset=true` starts a
        // fresh window after the read, so each load step scrapes its own numbers.
        app.MapGet("/diag/loadmetrics", (bool? reset) =>
            Results.Json(Diagnostics.LoadTestMetrics.Snapshot(reset == true)));

        // Result logging: the phone/Cockpit POSTs its completed speed-test result here; the Gateway stamps
        // what IT saw about the connection, writes one greppable log line, and keeps it in a small ring so
        // an agent can read the recent history at GET /diag/results with no phone. This is the "log all of
        // this so the agent can get to it" piece of the mission.
        //
        // TENANT-PARTITIONED (Hosted Multi-Tenancy; unsafe-collection census rows 21 and 22). All three of
        // these routes carry the SAME obligation, and it has two halves that must both hold: the WRITE
        // stamps the caller's authenticated tenant onto what it stores, and BOTH READS serve only that
        // tenant's partition. A write-only fix still leaks on the reads; a read-only filter is a DEFERRED
        // leak - cross-tenant data would keep accumulating behind it, so the day the filter is lifted it
        // exposes a contaminated history. Neither half is worth anything without the other.
        //
        // The tenant comes from the caller's authenticated device key (ResolveReadTenant), never from the
        // posted body. A null is a DENY (403): on hosted, an authenticated key with no bound tenant is
        // refused, never served or credited to the Local partition.
        var netDiagResults = new NetDiagResultStore(Path.Combine(CcStorage.Root(), "diagnostics-results.json"));
        app.MapPost("/diag/result", async (HttpContext ctx, NetDiagResultDto result) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
            {
                FileLog.Write("[NetDiag] POST /diag/result DENIED - the authenticated device key resolves to no tenant, so there is no partition to credit this result to (never Local)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            result.ClientIp = ctx.Connection.RemoteIpAddress?.ToString();
            result.ClientPath = NetDiag.ClassifyClientIp(ctx.Connection.RemoteIpAddress);
            result.ReceivedAt = DateTime.UtcNow;
            netDiagResults.Add(reqTenant.Value, result);
            // Fold into the hourly quality rollup by the MEASURED path (Direct/IsLanPath the client tagged
            // from its authoritative self-peer), never the front-door ClientPath. Keyed by tenant AND hour:
            // the hour alone is server time, which every tenant shares, so an unkeyed fold is an addition
            // into a shared aggregate that nobody can attribute or undo afterwards.
            netDiagRollup?.Fold(reqTenant.Value, result.ReceivedAt, result.LatencyMedianMs, result.Direct, result.IsLanPath, result.DownloadMbps, result.UploadMbps);
            FileLog.Write(
                $"[NetDiag] result tenant={reqTenant.Value.ToLogString()} surface={result.Surface} route={result.Route} clientPath={result.ClientPath} " +
                $"client={result.ClientIp} latencyMedian={result.LatencyMedianMs}ms down={result.DownloadMbps}Mbps " +
                $"up={result.UploadMbps}Mbps rating={result.Rating} loadedFrom={result.LoadedFrom}");
            await Task.CompletedTask;
            return Results.Json(new { ok = true });
        });
        app.MapGet("/diag/results", (HttpContext ctx) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
            {
                FileLog.Write("[NetDiag] GET /diag/results DENIED - the authenticated device key resolves to no tenant, so it owns no results (never the Local partition)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return Results.Json(netDiagResults.Recent(reqTenant.Value));
        });

        // GET /diag/rollup: the hourly quality trend (one bucket per UTC hour, oldest first) for the
        // Cockpit dashboard - percent-direct over time, latency trend, and the stored home/away split.
        // Served from the caller's own tenant partition only.
        app.MapGet("/diag/rollup", (HttpContext ctx) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
            {
                FileLog.Write("[NetDiag] GET /diag/rollup DENIED - the authenticated device key resolves to no tenant, so it owns no rollup (never the Local partition)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return Results.Json(netDiagRollup?.All(reqTenant.Value) ?? new List<NetDiagRollupStore.HourBucket>());
        });

        // About: the three SERVER-SIDE products - this Gateway, the Cockpit bundle it serves, and the
        // mobile app bundle it serves - plus how it is reached. Feeds the Cockpit About page;
        // loopback-reachable like the rest of the read API.
        //
        // The Director is deliberately NOT here (owner ruling 2026-07-26): it has its own About box and
        // its own Cockpit screen, so this page is about server versions only. The install root, machine
        // name, run mode and installer component manifest went with it - on the hosted service they were
        // internal detail about infrastructure the caller does not own, and the install root leaked the
        // operating-system user name to every enrolled device.
        //
        // CockpitUrl comes from GatewayPublicUrl.ResolveCockpit(): {base}/cockpit, where base is the
        // configured public URL in hosted mode and the tailnet front door (null when Tailscale is down)
        // self-hosted. One derivation rule, both modes (owner ruling 2026-07-20).
        app.MapGet("/gateway/about", () =>
        {
            return Results.Json(new AboutDto
            {
                Version = AboutInfo.VersionFull,
                BuildDate = AboutInfo.BuildDate()?.ToString("yyyy-MM-dd HH:mm:ss"),
                // The served bundles name themselves through the build.json each Vite build emits; the
                // Gateway cannot read the commit compiled into their JavaScript. Null when no built bundle
                // is staged, which the page reports as such rather than guessing a build.
                Cockpit = BundleStamp.Read(CockpitReactApp.WebRoot),
                Mobile = BundleStamp.Read(MobileApp.WebRoot),
                // Folded here, not in the client (CLAUDE.md rule 7).
                Deployment = GatewayHostedMode.IsHosted ? "Hosted service" : "Self-hosted",
                Address = GatewayPublicUrl.ResolveBase(),
                CockpitUrl = GatewayPublicUrl.ResolveCockpit(),
                // The internal listen port is meaningful ONLY self-hosted. On hosted, callers reach this
                // Gateway through Address on 443 and the platform forwards to the container's internal
                // port; printing that number beside an https address reads as a reachable port and is not
                // one. So it is omitted there - the Gateway decides, the client renders.
                Port = GatewayHostedMode.IsHosted ? null : gatewayPort?.Invoke(),
                UptimeSeconds = gatewayStartedAtUtc is { } startedAt ? (long)(DateTime.UtcNow - startedAt).TotalSeconds : 0,
                ServerTime = DateTime.UtcNow,
            });
        });

        // Where is this machine's Cockpit? Url is resolved on the Gateway by GatewayPublicUrl from the ONE
        // public base: Url = {base}/cockpit. In hosted mode (CC_GATEWAY_HOSTED=1) the base is the configured
        // public base; self-hosted it is the tailnet front door (Url null when Tailscale is unavailable, and
        // the caller surfaces that). The desktop Cockpit button opens Url verbatim - a dumb client never
        // composes a path onto Url (the Gateway owns the URL - CLAUDE.md rule 7). Port is the Gateway port
        // and Up is true whenever answering.
        app.MapGet("/cockpit", (HttpContext ctx) =>
        {
            return Results.Json(new CockpitInfoDto
            {
                Url = GatewayPublicUrl.ResolveCockpit(),
                Port = ctx.Connection.LocalPort,
                Up = true,
            });
        });

        // Issue #1847: serve THIS request's tenant's Directors, resolved from its authenticated device key -
        // the same seam the session read path uses. The list used to be fleet-global while the by-id legs
        // were gated, which made it the ENUMERATION surface: any authenticated account could read back every
        // other account's Director id, machine name, operating system user, process id, client version and
        // liveness. A request with no bound tenant is DENIED (403), never served the Local partition.
        app.MapGet("/directors", (HttpContext ctx) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            return Results.Json(registry.ListDirectors(reqTenant.Value));
        });

        // ===== HTTP discovery (Phase 1) =====
        // The Director POSTs /directors/register on startup and heartbeats every 15 s.
        // On graceful shutdown it DELETEs its registration. Same-machine Directors that
        // don't have gateway.url configured continue to be discovered via the filesystem
        // watch path - both paths coexist permanently.

        app.MapPost("/directors/register", (DirectorRegistrationRequest req) =>
        {
            // MTR-01 (Codex round 1): the HTTP register/heartbeat/doorbell/unregister legs are the legacy
            // SAME-MACHINE discovery plane - a self-host-only concept. A hosted Director reaches the Gateway
            // over the tunnel, never these HTTP legs, and every entry this plane writes is keyed to the Local
            // tenant. Left open on hosted, POST /directors/register is exactly the Local-shadow path: a hosted
            // caller could fabricate a Local registration for an arbitrary director id and then read that id's
            // Local event ring. Make the whole plane explicitly UNAVAILABLE on hosted (403) so the shadow can
            // never be created; self-host is unchanged. Gated on the PROCESS-level hosted flag, never on
            // the nullable boundary argument, so a null! boundary on a hosted process cannot reopen the
            // plane (the same discipline as HostedRouteDeny).
            if (GatewayHostedMode.IsHosted)
                return LegacyDiscoveryPlaneUnavailable();
            if (req is null || string.IsNullOrEmpty(req.DirectorId))
                return Results.BadRequest(new { error = "directorId is required" });
            // An empty endpoint is the NORMAL registration now: the Remove-the-network-port mission
            // deleted the Director's listener, so a current Director has no inbound address to
            // advertise and reachability is the tunnel connection itself. The old rule here - reject
            // an empty endpoint unless it carried its own unreachable-reason (issue #324) - guarded
            // against undialable entries in the era when the Gateway dialled Directors back; nothing
            // dials any more, so the guard's question no longer exists. Old Directors that still
            // send an endpoint or a reason are stored as they always were.
            FileLog.Write($"[GatewayEndpoints] POST /directors/register: id={req.DirectorId}, endpoint={(string.IsNullOrEmpty(req.TailnetEndpoint) ? "(none - tunnel only)" : req.TailnetEndpoint)}, machine={req.MachineName}");
            var dto = registry.Upsert(req);
            return Results.Json(dto, statusCode: StatusCodes.Status201Created);
        });

        app.MapPost("/directors/{id}/heartbeat", async (string id, HttpContext ctx) =>
        {
            // MTR-01 (Codex round 1): part of the legacy same-machine discovery plane - unavailable on hosted
            // (see /directors/register). This also replaces the pre-fix 410 that an unbound hosted request got
            // here with the correct 403 for a plane that does not serve hosted accounts. Gated on the
            // process-level hosted flag so a null! boundary cannot reopen it (see /directors/register).
            if (GatewayHostedMode.IsHosted)
                return LegacyDiscoveryPlaneUnavailable();
            var ok = registry.Heartbeat(id);
            if (!ok)
            {
                FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/heartbeat: unknown id (caller should re-register)");
                // 410 Gone tells the Director "you're not in the registry anymore" so its
                // client can re-POST /directors/register instead of just retrying heartbeats.
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            // Issue #186: a new Director's heartbeat carries a per-session state snapshot -
            // the reconcile channel for lost doorbell pings. Old Directors POST no body.
            if (onSessionState is not null && ctx.Request.ContentLength > 0)
            {
                DirectorHeartbeatRequest? body = null;
                try { body = await ctx.Request.ReadFromJsonAsync<DirectorHeartbeatRequest>(ctx.RequestAborted); }
                catch (System.Text.Json.JsonException ex)
                {
                    FileLog.Write($"[GatewayEndpoints] heartbeat body unparsable from {id}: {ex.Message}");
                }
                if (body?.Sessions is { } sessions)
                {
                    // A state-carrying heartbeat (even with zero sessions) proves this
                    // Director pushes its own signals - the reconcile poll skips it.
                    registry.MarkStateReporting(id);
                    foreach (var s in sessions)
                        onSessionState(id, s.SessionId, s.ActivityState);
                }
            }
            return Results.Json(new { ok = true });
        });

        // Issue #186: the turn-end doorbell. The Director announces THAT a session's
        // mechanical state changed; the Gateway pulls the truth afterwards. Always 200 for
        // a known Director (a dropped observation costs nothing - the heartbeat reconciles);
        // 410 tells an unregistered Director to re-register first. Issue #330: the same
        // ping may carry an event-vocabulary tag (session-created/session-exited/
        // prompt-detected) which lands in the per-director event ring; a tag-less ping is
        // the pre-#330 shape and records nothing.
        app.MapPost("/directors/{id}/doorbell", (string id, DoorbellRequest req) =>
        {
            // MTR-01 (Codex round 1): the doorbell is a leg of the legacy same-machine HTTP discovery plane -
            // unavailable on hosted (see /directors/register), where leaving it open would let a hosted caller
            // inject into a bare-id event ring. On self-host its entries are always keyed to the Local tenant
            // (see DirectorRegistry.Upsert), so it resolves within Local and records under Local. Gated on
            // the process-level hosted flag so a null! boundary cannot reopen it (see /directors/register).
            if (GatewayHostedMode.IsHosted)
                return LegacyDiscoveryPlaneUnavailable();
            if (registry.Get(TenantId.Local, id) is null)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (req is null || string.IsNullOrEmpty(req.SessionId) || string.IsNullOrEmpty(req.NewState))
                return Results.BadRequest(new { error = "sessionId and newState are required" });

            registry.MarkStateReporting(id);
            if (directorEvents is not null && !string.IsNullOrEmpty(req.Event))
                directorEvents.Record(TenantId.Local, id, req.SessionId, req.Event, req.NewState);
            onSessionState?.Invoke(id, req.SessionId, req.NewState);
            return Results.Json(new { ok = true });
        });

        // Issue #330: the per-director event debug surface - the recent doorbell events
        // (session-created/session-exited/prompt-detected) the Gateway has recorded for a
        // KNOWN director, oldest first. This is the minimal Phase-1 observable sink; the
        // real consumer (the SSE/WS event hub) is Phase 3.
        app.MapGet("/directors/{id}/events", (string id, HttpContext ctx) =>
        {
            // MTR-01 (Codex round 1): this is a CLIENT-serving read, so it resolves the request's OWN tenant
            // and reads only that tenant's ring for this id. 403 when no tenant is bound (deny-by-default,
            // never the Local partition), 404 when the id is not the caller's Director. Because the ring is now
            // keyed by (tenant, id), a hosted account can never read another account's ring - even for the same
            // id, and even if a Local shadow of the id existed, the caller's tenant reads a different queue.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (registry.Get(reqTenant.Value, id) is null)
                return Results.NotFound(new { error = "director not found" });
            var events = directorEvents?.For(reqTenant.Value, id) ?? (IReadOnlyList<DirectorEventDto>)Array.Empty<DirectorEventDto>();
            return Results.Json(new { directorId = id, events });
        });

        // Gateway Cleanup mission (Phase 2/3): the two-way connectivity handshake (POST /directors/{id}/verify
        // + the verify-ws leg) is DELETED. It dialed the Director's HTTP/WebSocket callback endpoints - a
        // Gateway->Director HTTP path the tunnel-only endgame removes - and drove the reachability circuit
        // breaker, which is also gone. Liveness is now the tunnel connection itself.

        app.MapDelete("/directors/{id}/registration", (string id) =>
        {
            // MTR-01 (Codex round 1): part of the legacy same-machine discovery plane - unavailable on hosted
            // (see /directors/register). Left open, an unbound hosted caller holding the shared machine token
            // could remove a Local registration; on hosted there is no such plane to unregister from. Gated
            // on the process-level hosted flag so a null! boundary cannot reopen it (see /directors/register).
            if (GatewayHostedMode.IsHosted)
                return LegacyDiscoveryPlaneUnavailable();
            FileLog.Write($"[GatewayEndpoints] DELETE /directors/{id}/registration");
            var removed = registry.Remove(id);
            return removed
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "director not found" });
        });

        // Fleet-wide read aggregator. Fans out in parallel to every registered Director,
        // stamps each returned SessionDto with the owning Director's machine name, user,
        // tailnet endpoint, and a full deep-link ViewUrl. Failed Directors do not poison
        // the response: by default they're silently skipped (backward-compat flat list);
        // with ?envelope=true they're surfaced in machineErrors so the UI can render an
        // inline "unreachable" placeholder.
        // GET /repositories + /worktrees - the fleet's repository/worktree fact (repositories mission,
        // #510 phase C). Tenant-scoped exactly like /sessions: the caller sees only its own partition.
        // Read-only: reaping runs on the owning Director after a live re-verify, never from here.
        app.MapGet("/repositories", (HttpContext ctx, string? machine, string? repo) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (pushedRepositories is null)
                return Results.Json(new List<RepoStatusDto>());

            var rows = new List<RepoStatusDto>();
            foreach (var directorId in pushedRepositories.DirectorIdsFor(reqTenant.Value))
            {
                var fresh = pushedRepositories.TryGetFresh(reqTenant.Value, directorId, streamStaleResolved);
                if (fresh is null)
                    continue;
                // The one repository-level serve fold (ruling R2-3): a pre-fix Director can push
                // Provisional=true with a stale safe count and stale worktree states - the
                // Gateway owns the verdict at SERVE time, for /repositories exactly as for
                // /worktrees. A provisional repository serves a zero safe count and "verifying"
                // worktrees, whatever was pushed.
                rows.AddRange(fresh.Value.Repositories.Select(FleetWorktreeFold.FoldRepositoryForServe));
            }
            if (!string.IsNullOrWhiteSpace(machine))
                rows = rows.Where(r => string.Equals(r.MachineName, machine, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(repo))
                rows = rows.Where(r => string.Equals(r.Name, repo, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Json(rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
        });

        app.MapGet("/worktrees", (HttpContext ctx, string? machine, string? repo, string? state) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (pushedRepositories is null)
                return Results.Json(new List<FleetWorktreeDto>());

            var rows = new List<FleetWorktreeDto>();
            foreach (var directorId in pushedRepositories.DirectorIdsFor(reqTenant.Value))
            {
                var fresh = pushedRepositories.TryGetFresh(reqTenant.Value, directorId, streamStaleResolved);
                if (fresh is null)
                    continue;
                // The one shared flatten fold: a provisional repository's worktrees are served as
                // "verifying" - never "safe-to-reap" - whatever the pushing Director sent.
                rows.AddRange(FleetWorktreeFold.Flatten(fresh.Value.Repositories, fresh.Value.DataAgeSeconds));
            }
            if (!string.IsNullOrWhiteSpace(machine))
                rows = rows.Where(w => string.Equals(w.MachineName, machine, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(repo))
                rows = rows.Where(w => string.Equals(w.RepoName, repo, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(state))
                rows = rows.Where(w => string.Equals(w.State, state, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Json(rows.OrderBy(w => w.RepoName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Branch, StringComparer.OrdinalIgnoreCase).ToList());
        });

        // GET /reports/repositories-weekly - the memory turned into numbers (repositories mission,
        // #510 phase D): weekly worktree/disk trends plus today's dirty-too-long callouts. Feeds the
        // dev-effectiveness report; tenant-scoped like every read.
        app.MapGet("/reports/repositories-weekly", (HttpContext ctx, int? weeks) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (repoHistory is null)
                return Results.Json(new { error = "repository history is not enabled on this Gateway" },
                    statusCode: StatusCodes.Status404NotFound);
            var trends = repoHistory.WeeklyTrends(reqTenant.Value, Math.Clamp(weeks ?? 8, 1, 26));
            var dirty = repoHistory.DirtyOverThreshold(reqTenant.Value);
            return Results.Json(new
            {
                weeks = trends,
                dirtyOverThreshold = dirty,
                dirtyThresholdDays = Streaming.RepoHistoryStore.DirtyThresholdDays,
            });
        });

        app.MapGet("/sessions", (HttpContext ctx, string? director, string? agent, string? state,
                                       string? statusColor, string? machine,
                                       bool? includeExited, string? q, bool? envelope) =>
        {
            // Hosted Multi-Tenancy (session-serving PR1): serve THIS request's tenant's roster, resolved from
            // its authenticated device key. On hosted a request with no bound tenant is DENIED (403), never
            // served the Local partition. Self-host is Local, unchanged.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Hosted Multi-Tenancy (audit H1, gap audit-a): serve THIS request's tenant's Directors, resolved
            // from the registry's tenant-scoped (tenant, id) partition - NOT the fleet-global ListDirectors().
            // The fleet-global overload names every tenant's Directors, and the `director=` / `machine=` filters
            // below match on a BARE id / machine name, so a cross-tenant Director sharing the requested id or
            // machine would survive into this tenant's roster and leak its DirectorDto (machine name, the
            // "unreachable" reachability / machineError rows) in the ?envelope response. ListDirectors(tenant)
            // confines the list to the caller's own partition so no such collision can name another tenant. A
            // hosted Director reaches the registry only via its tunnel Hello, which first binds it into its
            // tenant's partition, so scoping to the partition drops nothing of the tenant's own. On self-host the
            // registry partition IS the one Local tenant, so this is the same list as before.
            var directors = registry.ListDirectors(reqTenant.Value)
                .Where(d => string.IsNullOrEmpty(director) || string.Equals(d.DirectorId, director, StringComparison.OrdinalIgnoreCase))
                .Where(d => string.IsNullOrEmpty(machine) || string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Within that already tenant-scoped list, hosted additionally keeps only Directors that have pushed a
            // session snapshot - the roster source below is the pushed stream cache, so a registered-but-unpushed
            // Director has nothing to serve. This intersection is by bare id, which is safe ONLY because the list
            // above is confined to this tenant's partition (every id here belongs to reqTenant); it is a
            // pushed-vs-registered filter, not the tenant boundary. On self-host it is skipped, so a
            // registered-but-unpushed Director still surfaces (unchanged).
            // Gated on the process-level hosted flag (never the nullable boundary argument): with a null!
            // boundary on a hosted process this filter must still apply.
            if (GatewayHostedMode.IsHosted && pushedSessions is not null)
            {
                var mine = new HashSet<string>(pushedSessions.DirectorIdsFor(reqTenant.Value), StringComparer.OrdinalIgnoreCase);
                directors = directors.Where(d => mine.Contains(d.DirectorId)).ToList();
            }

            var includeExitedActual = includeExited ?? false;
            var streamStale = streamStaleResolved;

            var all = new List<SessionDto>();
            // Defect 13: the UNFILTERED fleet - the role universe. `all` is the filtered response set and is
            // drawn from these same instances, which is what lets one role pass serve both.
            //
            // KNOWN LIMITATION, deliberately not fixed here: this universe is already scoped by the
            // `machine=` filter applied to the Director list at the top of this handler, so a Worker on
            // MACHINE_A whose Manager runs on MACHINE_B still gets its red un-suppressed by
            // `?machine=MACHINE_A`. Reordering cannot fix that one - the other Director is never read at all -
            // and fixing it means pulling every Director on every filtered read, which is a cost change that
            // needs its own decision. Recorded in docs/new_architecture/session-state.html.
            var fleet = new List<SessionDto>();
            var machineErrors = new List<MachineErrorDto>();
            // Session ids drawn from a serve the owning machine confirmed - see where it is filled below.
            var confirmedLive = new HashSet<string>(StringComparer.Ordinal);
            var reachability = new List<DirectorReachabilityDto>();

            // Epic #1159 step A - THE ROSTER SERVES WHAT THE GATEWAY LAST KNEW, ALWAYS.
            //
            // The read no longer asks "is this machine's data fresh enough to be worth showing". It asks the
            // store what it last knew, serves that, and reports how old it is. Age decides what the roster
            // SAYS about a machine; it no longer decides whether the machine is on it.
            //
            // WHAT THIS REPLACED, because the shape is the whole point. There used to be two staleness
            // authorities stacked in this one path, and both of them deleted:
            //   - the pushed store was read through TryGetFresh, which returns null past twenty seconds, and a
            //     null was rendered as "unreachable, no sessions";
            //   - that "unreachable" was then fed to a last-known-good cache which granted three poll cycles of
            //     grace and, once they were spent, declared the machine Offline and DROPPED its sessions.
            // A Director re-pushes every ten seconds against a twenty-second window, so two missed ticks
            // emptied a machine. With the tunnel dropping dozens of times a day the owner's roster blanked -
            // sessions, colours and all - several times an hour, while the Gateway sat holding the very data it
            // had just refused to show. The grace-window cache is DELETED, not merely bypassed: a second
            // authority that can still be wired back in is a defect waiting to be re-introduced, and its whole
            // job - keep serving a machine that just went quiet - is now the unconditional behaviour here.
            //
            // The three wire states survive unchanged, because the Cockpit already renders them. What changed
            // is that they are read off the TUNNEL rather than off a countdown, and that offline stopped
            // meaning deleted:
            //   online  - tunnel up and the last push is current: this is live data.
            //   wobbly  - tunnel up but nothing recent: real data, going stale, machine still there.
            //   offline - tunnel down: real data, dated, and the machine cannot be acted on. STILL SERVED.
            //
            // Sessions now leave the roster for exactly two reasons, neither of them a display timeout: the
            // Director said so (its snapshot pruned them, or it sent a remove), or the machine passed
            // DirectorRegistry.EvictionHorizon and was swept out of the registry entirely.
            var served = new List<(DirectorDto Director, List<SessionDto> Sessions, bool Stale, bool Reachable)>();
            var rosterNow = DateTime.UtcNow;
            foreach (var d in directors)
            {
                // No pushed store wired at all (a Gateway assembled without one) - there is no roster source,
                // so the machine is surfaced as an error exactly as it was before, with nothing served.
                if (pushedSessions is null)
                {
                    const string noStore = "this gateway has no pushed session store";
                    machineErrors.Add(new MachineErrorDto
                    {
                        DirectorId = d.DirectorId,
                        MachineName = d.MachineName ?? "",
                        Error = noStore,
                    });
                    // AND a reachability row, which this branch used to skip. The warning line above the map
                    // is folded from the reachability list, so a branch that fills machineErrors alone is a
                    // branch where the Gateway knows it cannot reach a single machine and says nothing at all
                    // - the silent-failure shape this whole change exists to remove, reintroduced in the one
                    // path that has no roster source whatsoever.
                    var noStoreRow = new DirectorReachabilityDto
                    {
                        DirectorId = d.DirectorId,
                        MachineName = d.MachineName ?? "",
                        DisplayName = d.DisplayName ?? "",
                        State = DirectorReachabilityDto.StateOffline,
                        LastSeenUtc = null,
                        LastSeenAgeSeconds = null,
                        Error = noStore,
                    };
                    FleetReachabilityFold.Describe(noStoreRow);
                    reachability.Add(noStoreRow);
                    continue;
                }

                var known = pushedSessions.GetLastKnown(reqTenant.Value, d.DirectorId);
                var ageSeconds = known.AsOfUtc is DateTime asOf ? Math.Max(0, (rosterNow - asOf).TotalSeconds) : (double?)null;
                // FRESH means both halves: the tunnel is up AND the newest push is inside the staleness window.
                // Only a fresh serve is authoritative, and only a fresh serve may be acted upon below.
                var fresh = known.Connected && ageSeconds is double age && age <= streamStale.TotalSeconds;

                string linkState;
                string? reason;
                if (fresh)
                {
                    linkState = DirectorReachabilityDto.StateOnline;
                    reason = null;
                }
                else if (known.Connected)
                {
                    linkState = DirectorReachabilityDto.StateWobbly;
                    reason = "no recent push from this director";
                }
                else if (d.StoppedAtUtc is not null)
                {
                    // IT SAID GOODBYE. The tunnel is down because the process ended on purpose and told us so
                    // (DirectorHub.DirectorStopping -> DirectorRegistry.MarkStopped), not because anything
                    // failed. Reported as its own state so it is never counted as a machine we cannot reach:
                    // the registration outlives the process by a day, and calling that whole day "unreachable"
                    // turned every orderly shutdown into a standing warning about a healthy machine.
                    //
                    // The stamp is cleared by the next Hello, so a Director that comes back is online again on
                    // its first push - and one that dies WITHOUT a farewell never had a stamp, so it still
                    // reads offline, which is the case actually worth showing.
                    linkState = DirectorReachabilityDto.StateStopped;
                    reason = "director was shut down";
                }
                else
                {
                    linkState = DirectorReachabilityDto.StateOffline;
                    // Issue #324: a flagged registration declared its own endpoint unreachable (no tailnet
                    // identity on that machine) - surface the Director's own reason, which names the fix.
                    reason = !string.IsNullOrEmpty(d.EndpointUnreachableReason)
                        ? d.EndpointUnreachableReason!
                        : "director not connected to the tunnel";
                }

                var reachRow = new DirectorReachabilityDto
                {
                    DirectorId = d.DirectorId,
                    MachineName = d.MachineName ?? "",
                    DisplayName = d.DisplayName ?? "",
                    State = linkState,
                    // WHEN THE GATEWAY LAST HEARD THIS MACHINE, taken from the store's own arrival stamp rather
                    // than from the clock at serve time. The old online branch wrote DateTime.UtcNow with an age
                    // of zero, which is a measurement of when the response was assembled, not of when anything
                    // was heard - it could never have been anything but zero, on any roster, ever.
                    LastSeenUtc = known.AsOfUtc,
                    LastSeenAgeSeconds = ageSeconds,
                    Error = reason,
                };
                // Every judgement the client would otherwise make about what this state MEANS - the badge word,
                // whether the rows are last-known, whether a start could be delivered, what to print when there
                // are no sessions - is folded on here, once (CLAUDE.md rule 7).
                FleetReachabilityFold.Describe(reachRow);
                reachability.Add(reachRow);

                // machineErrors keeps its historical meaning - "the Gateway cannot reach this director" - and so
                // keeps its historical membership: offline only. It is no longer a statement that the machine's
                // sessions were dropped, because they were not; and a STOPPED director is not in it at all,
                // because nothing failed. Note the noun: these rows are per-DIRECTOR, which is why the banner
                // built from them is folded by FleetReachabilityFold rather than counted by a view.
                if (linkState == DirectorReachabilityDto.StateOffline)
                {
                    machineErrors.Add(new MachineErrorDto
                    {
                        DirectorId = d.DirectorId,
                        MachineName = d.MachineName ?? "",
                        Error = reason!,
                    });
                }

                FileLog.Write($"[GatewayEndpoints] /sessions director={d.DirectorId} state={linkState} sessions={known.Sessions.Count} asOfAgeSeconds={(ageSeconds is double s ? s.ToString("F0") : "never")}");
                // TWO DIFFERENT QUESTIONS, and they are deliberately not the same flag.
                //   Stale     - is this data confirmed current? Governs what may be ACTED on, and a merely
                //               late push is enough to withhold that.
                //   Reachable - is the machine there at all? Governs whether its sessions may NAG, and only
                //               the tunnel answers it.
                // Collapsing them into one would make the badge flicker off every time a push ran a few
                // seconds late, which is this mission's own defect in a third disguise.
                served.Add((d, known.Sessions.ToList(), Stale: !fresh, Reachable: known.Connected));
            }

            foreach (var (d, sessions, stale, reachable) in served)
            {
                // Issue #291: a reachable Director's returned list is the authoritative live set for it.
                // Prune any session the cache still attributes to this Director that is no longer live here
                // - it exited or disappeared - so the per-session WS proxy reverts to 404 instead of #288's
                // 503 "owner offline". Computed from the raw returned list (before the per-session view
                // filters below) and excluding Exited rows (a Director may include them when
                // includeExited=true). Owners on OTHER Directors are untouched, so an offline owner's
                // sessions stay cached -> still 503 (#288 unchanged).
                // Issue #1215: SKIP this prune for a Wobbly (stale) serve - the Director did NOT answer, so
                // the stale snapshot is not authoritative and must not evict live ownership records.
                //
                // Epic #1159 step A: THIS GUARD IS NOW LOAD-BEARING FOR THE WHOLE ENDPOINT, and it is why the
                // read above marks every unconfirmed serve stale rather than quietly widening "online". The
                // roster is not only a display read - it is the authority several destructive consumers act
                // on, and it now carries data from machines that are not answering. A last-known set is
                // last-known: it cannot say a session ENDED, only that nobody has heard otherwise. Acting on
                // it would delete a live session's snooze from the database and evict its ownership record,
                // and both faults would surface long after the roster looked fine again.
                //
                // The consumers were enumerated for this change, and each was placed deliberately:
                //   INSIDE this guard, because they DELETE state keyed off "what is live here":
                //     owners.RetainForDirector    - evicts ownership records absent from the list
                //     snoozeRegistry.PruneNotLive - deletes snooze rows from the database
                //   OUTSIDE, on the fresh subset only, because a stale set would not corrupt them but WOULD
                //   inflate them with sessions on machines that are not running:
                //     inputStats.ObserveSnapshot  - per-session high-water tallies
                //     concurrency.Observe         - peak concurrent sessions and hourly activity
                //   OUTSIDE, on everything served, because they only ever ADD and holding them is correct
                //   while a machine is merely unreachable:
                //     owners.Remember             - records ownership, so the proxy says "owner offline"
                //     sessionNumbers.Adopt        - marks a number in use, never frees one
                // Two more destructive consumers were checked and are NOT reached from here: the auto-dismiss
                // sweeper, which kills sessions, reads PushedSessionStore.SnapshotFresh and so still sees only
                // connected machines; and the desktop worktree reaper, which consumes this endpoint but
                // refuses to run at all while any Director on its machine reads other than online.
                if (!stale)
                {
                    var liveIds = new HashSet<string>(
                        sessions
                            .Where(x => !string.IsNullOrEmpty(x.SessionId)
                                     && !string.Equals(x.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                            .Select(x => x.SessionId),
                        StringComparer.Ordinal);
                    owners?.RetainForDirector(reqTenant.Value, d.DirectorId, liveIds);
                    // Snooze Length mission: a reachable Director's returned list is authoritative, so a
                    // snoozed session that has permanently exited is no longer live here - drop its
                    // snooze entry so the registry does not accumulate stale entries on disk. Runs only
                    // for a Director that actually answered (!stale), so a transient miss never loses a
                    // pending snooze.
                    snoozeRegistry?.PruneNotLive(d.DirectorId, liveIds);
                }

                var baseUrl = DeriveDirectorBaseUrl(ctx, d);
                var gatewayBaseUrl = DeriveGatewayBaseUrl(ctx);
                foreach (var s in sessions)
                {
                    // Defect 13: the ROLE UNIVERSE is the UNFILTERED fleet, and it is collected HERE -
                    // before the filters below get a vote. A session the caller filtered out still exists,
                    // and still keeps its worker's red suppressed. See StampFleetRolesAndFold.
                    //
                    // The filters deliberately stay where they are rather than moving below the fold. Moving
                    // them would silently widen four unrelated things that read the FILTERED set today:
                    // owners?.Remember (ownership records), inputStats?.ObserveSnapshot, concurrency?.Observe
                    // and sessionNumbers.Adopt. Those are second-order effects of a "simple" reorder and none
                    // of them is part of this defect.
                    // Epic #1159 step A: the Gateway's own answer to "may this session nag the human". Stamped
                    // before the filters so every instance carries it, in the role universe as well as the
                    // response set. False means the machine's TUNNEL IS DOWN: the session is still shown,
                    // dimmed and dated, but it is out of the "needs you" badge and out of the voice queue,
                    // because nobody can act on it.
                    //
                    // It is keyed on the tunnel and NOT on staleness, which is the narrower and correct test.
                    // A wobbly machine - tunnel up, push merely late - can still be acted on: a command sent
                    // to it lands. Suppressing its badge would make the count blink off whenever a push ran a
                    // few seconds late, which is exactly the transient-staleness-deletes-information defect
                    // this mission exists to end. Only a machine that is genuinely gone stops nagging.
                    // See SessionDto.MachineReachable.
                    s.MachineReachable = reachable;
                    fleet.Add(s);

                    if (!string.IsNullOrEmpty(agent) && !string.Equals(s.Agent, agent, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(state) && !string.Equals(s.ActivityState, state, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(statusColor) && !string.Equals(s.StatusColor, statusColor, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!includeExitedActual && string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(q))
                    {
                        var needle = q;
                        var nameHit = !string.IsNullOrEmpty(s.Name) && s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
                        var repoHit = !string.IsNullOrEmpty(s.RepoPath) && s.RepoPath.Contains(needle, StringComparison.OrdinalIgnoreCase);
                        if (!nameHit && !repoHit) continue;
                    }

                    s.DirectorId = d.DirectorId;
                    // Issue #335: Director-supplied identity fields win over Gateway-derived ones.
                    // A NEW Director (issue #335+) populates MachineName, User, TailnetEndpoint,
                    // and ViewUrl itself; the Gateway must not overwrite them (they carry the
                    // Director's own resolved tailnet identity). An OLD Director sends empty fields;
                    // the Gateway enriches them as before (back-compat for mixed-version fleets).
                    if (string.IsNullOrEmpty(s.MachineName))
                        s.MachineName = d.MachineName;
                    if (string.IsNullOrEmpty(s.User))
                        s.User = d.User;
                    if (string.IsNullOrEmpty(s.TailnetEndpoint))
                        s.TailnetEndpoint = baseUrl;
                    // Issue #288: remember who owns this session so the WS proxy answers 503 (owner
                    // offline) instead of 404 once this Director goes dark.
                    owners?.Remember(reqTenant.Value, s.SessionId, d.DirectorId);
                    // Issue #549: the always-on turn-brief pipeline is retired. The Gateway no
                    // longer stamps the assessed-state refutation (issue #186, Option A) nor the
                    // brief stamping (issue #187 BriefingState/RailLine) - the brief agent that
                    // wrote those is deleted. "Needs you" reverts to the Director's raw mechanical
                    // signal; AssessedState stays null so every UI's "AssessedState ?? ActivityState"
                    // falls through to the raw ActivityState.
                    // Issue #531 voice mode: while the gateway's warm-brain wingman is producing this
                    // session's spoken summary, present it through the yellow "wingman reading" window
                    // (red -> yellow -> red). Gated on raw red so a working (blue) session is
                    // untouched. Independent of any brief agent; never spawns a --print explain.
                    //
                    // "Gated on raw red" is what this comment ALWAYS said. The code did not do it: it gated
                    // on s.StatusColor - the DIRECTOR's cooked colour - so a colour rendered on the phone and
                    // the Cockpit depended on a decision the Director made. That is precisely what law 2
                    // forbids (the Gateway is the only thing that picks a colour), and it was the last
                    // Gateway consumer of the cooked field. The comment described the intended design and the
                    // code never matched it; now it does.
                    //
                    // THE STAMP THAT WAS HERE IS DELETED, AND MUST NOT COME BACK (gap 5). It read:
                    //
                    //     if (voiceGeneratingFor is not null
                    //         && (s.BriefingState is null or "None" or "Briefed")
                    //         && SessionOrdering.IsRawRed(s)
                    //         && voiceGeneratingFor(s.SessionId))
                    //         s.BriefingState = "Briefing";
                    //
                    // THE GATEWAY MUST NOT OVERWRITE A FIELD THE DIRECTOR OWNS. BriefingState is the
                    // Director's fact. Writing "Briefing" over it destroyed the Director's answer, and a
                    // destroyed fact cannot be argued back: a row carrying BriefingState="Briefing" plus
                    // VoiceGenerating=true could no longer say whether the Director genuinely was briefing
                    // (the desktop folds yellow too - agreement) or the Gateway had overwritten a "None"
                    // (the desktop folds red - a real disagreement). Those are opposite verdicts from an
                    // identical row. The agreement check could only call it "indeterminate" and refuse to
                    // grade it - which is a workaround for the instrument, not a fix for the product.
                    //
                    // NOTHING REPLACED IT, and that is the fix - not a new rule somewhere else. The Gateway
                    // already adds its fact: VoiceGenerating, stamped unconditionally two lines down, and
                    // SessionOrdering.IsVoicePreparing already folds it to the same yellow. The stamp was
                    // redundant as well as destructive.
                    //
                    // READ THIS BEFORE YOU "RESTORE THE MISSING RULE": IsVoicePreparing is NOT this stamp's
                    // condition and is not meant to be. It is narrower - it requires VoiceMode and a session
                    // actually WAITING, where the stamp fired on any raw-red session with voice generating.
                    // A first attempt at this fix did add a rule carrying the stamp's exact condition, on
                    // the theory that it preserved every pixel; the existing suite refuted it
                    // (StateLabel_VoicePreparing_IsPreparingVoice and
                    // EffectiveColor_NonVoiceWaiting_NoAudio_StaysRed both went red) and that attempt was
                    // thrown away. Two rules for one fact is two answers, which is this mission's whole
                    // defect class. If a row looks like it is missing a yellow, the question is whether
                    // IsVoicePreparing is right - not whether this stamp should come back.
                    //
                    // The stamp also made the words wrong, which nobody noticed: hijacking BriefingState
                    // sent a voice-generating session down the fold's IsBriefing arm, so it read "Wingman
                    // reading" when the Gateway's own rule says the truer "Preparing voice". The dot was
                    // yellow either way. Both facts now ride the row, nothing is destroyed, the check can
                    // grade it, and the label is honest.
                    //
                    // If you need the Gateway to say something new about a session, add a Gateway-owned
                    // field. Never reach for a Director-owned one because it happens to be the shape you
                    // want - that trade is a rendered pixel now for an unanswerable row forever.

                    // Issue #553: surface the two voice readiness booleans the color rule and the /m
                    // client read directly. VoiceGenerating = the wingman is producing this session's
                    // spoken summary now; VoiceAudioReady = the gateway has fetchable, playable audio
                    // (the SINGLE truthful "there is voice you can play right now" signal). VoiceGenerating
                    // is the only "preparing voice" hold; VoiceAudioReady controls playback affordances.
                    if (voiceGeneratingFor is not null)
                        s.VoiceGenerating = voiceGeneratingFor(reqTenant.Value, s.SessionId);
                    if (voiceAudioReadyFor is not null)
                        s.VoiceAudioReady = voiceAudioReadyFor(reqTenant.Value, s.SessionId);
                    // Issue #939: when the gateway could not keep this session's voice because hosted AI
                    // is unavailable (out of credits / cap / no key), stamp the ONE shared message so the
                    // owning UI shows the consistent add-credit / add-key state instead of a silently
                    // missing play triangle. Null (voice fine) leaves the field unset.
                    if (voiceUnavailableFor is not null && voiceUnavailableFor(reqTenant.Value, s.SessionId) is Core.HostedAi.HostedAiState reason)
                        s.VoiceUnavailable = HostedAi.HostedAiHttp.Dto(reason);
                    // The FOLDED voice-mode display verdict the Voice screen renders VERBATIM. Every piece
                    // of ruling the phone used to do for itself - the badge, the message, and crucially
                    // whether a "Generate narration" button appears - is decided HERE, from the facts just
                    // stamped plus the "nothing to narrate" marker, so a dumb client never has to guess (the
                    // guess is what put a dead-end Generate button next to a red "unavailable" badge). This
                    // is the law: the Gateway rules, the client renders (docs/new_architecture/session-state.html).
                    // Issue #2576: the wait-for-voice clock. Stamped from the SAME facts the fold
                    // immediately below reads, so the elapsed time on the row and the words on the row can
                    // never disagree about whether this session is waiting at all. It is a SECOND clock
                    // beside NeedsYouSince because that one is stamped only on RED and a session waiting
                    // for voice is YELLOW - which is why nothing could say "48 minutes" when it mattered.
                    var voiceAgentWorking = string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);
                    s.VoiceWaitingSince = voiceWaitingStampFor?.Invoke(
                        reqTenant.Value, s.SessionId,
                        Wingman.VoiceDisplayFold.IsWaitingForVoice(s.VoiceMode, s.VoiceAudioReady, voiceAgentWorking));
                    s.VoiceDisplay = Wingman.VoiceDisplayFold.Fold(
                        voiceMode: s.VoiceMode,
                        agentWorking: voiceAgentWorking,
                        hasAudio: s.VoiceAudioReady,
                        generating: s.VoiceGenerating,
                        unavailable: voiceUnavailableFor?.Invoke(reqTenant.Value, s.SessionId),
                        nothingToNarrate: nothingToNarrateFor?.Invoke(reqTenant.Value, s.SessionId) ?? false,
                        directorCannotSendConversation: directorCannotSendConversationFor?.Invoke(reqTenant.Value, s.SessionId) ?? false,
                        narrationAbandoned: narrationAbandonedFor?.Invoke(reqTenant.Value, s.SessionId) ?? false,
                        servedViaFallback: servedViaFallbackFor?.Invoke(reqTenant.Value, s.SessionId) ?? false,
                        waitingSince: s.VoiceWaitingSince);
                    // Orange "Transcribing..." while a dictated utterance is uploading/transcribing in
                    // the background for this session (mobile Speak -> Send released the screen). Stamped
                    // BEFORE the NeedsYouSince clock below so the EffectiveColor fold already sees orange
                    // (a transcribing session is not "needs you") when the clock reads the final color.
                    if (transcribingFor is not null)
                        s.Transcribing = transcribingFor(reqTenant.Value, s.SessionId);
                    // Issue #1181, Task 4: the honest phase label - "Uploading from phone" (durable PENDING
                    // marker) vs "Transcribing" (active run). Drives the same orange, but the clients render
                    // this string so the user knows whether it is their phone still uploading or the server.
                    if (dictationStatusFor is not null)
                        s.DictationStatus = dictationStatusFor(reqTenant.Value, s.SessionId);
                    // The authoritative presentation fold (EffectiveColor / StateLabel / TriageBucket /
                    // NeedsYouSince) is stamped in ONE post-pass AFTER this loop assembles the whole fleet -
                    // see StampFleetRolesAndFold below. It is deferred because SessionRole (which the fold
                    // now reads to suppress a live Worker's red) needs the full roster, not one session.
                    // THE LINK IS MINTED FROM THIS GATEWAY'S OWN ORIGIN, ALWAYS - never derived from a
                    // Director's endpoint, and never taken from what a Director supplied. See
                    // GatewaySessionLink for why that reversal is the fix and not merely a tidy-up.
                    s.ViewUrl = GatewaySessionLink(gatewayBaseUrl, s.SessionId);
                    all.Add(s);
                    // The statistics subset: sessions from a serve that was CONFIRMED CURRENT. Narrower than
                    // MachineReachable on purpose - a wobbly machine may nag, but its months-old numbers must
                    // not be re-folded as though they were this minute's activity.
                    if (!stale)
                        confirmedLive.Add(s.SessionId);
                }
            }

            // The whole fleet is now assembled: compute each session's automatic role from the roster and
            // stamp the presentation fold (which reads the role to suppress a live Worker's red toward the
            // human). Done here, once, because the role needs the full fleet view - the UNFILTERED one
            // (`fleet`), not the response set (`all`). See defect 13 in StampFleetRolesAndFold.
            StampFleetRolesAndFold(fleet, all, needsYouStampFor, snoozeRegistry, reqTenant.Value, handRaises);

            // DevThrottle Stats: fold the assembled roster's per-session input tallies into the always-
            // available aggregate that backs "Your Throttle". This is the ONE path that carries
            // SessionDto.InputStats on the live Gateway regardless of stream mode (the SignalR DirectorHub
            // fold only runs when stream mode is on, which it is not in production). The aggregator's
            // per-session high-water logic makes folding the full roster on every read idempotent - only a
            // genuine increase is added, so repeated /sessions polls never double-count.
            // MTR-08: stamp the REQUEST TENANT. The roster assembled above is this tenant's own (the
            // owned-Director gate filtered it), so its input tallies fold into this tenant's partition and can
            // never coalesce with another account's.
            // Epic #1159 step A: the statistics fold over the CONFIRMED-LIVE subset, not the whole served
            // roster. The roster now carries sessions from machines that are not answering, and a session on
            // a sleeping laptop is not running work - counting it would report activity that is not happening
            // and, worse, would keep reporting it on every poll for as long as the machine stayed away. The
            // stamp is the Gateway's own, set from the same freshness decision that produced the reachability
            // state above, so there is one rule and not a second staleness test here.
            //
            // CONTAINED (failure review M2). The fold runs inline on the request thread, so a statistics
            // write failure used to leave this route - the roster is fully assembled by this line, and it
            // would still answer 500. Statistics are a background concern and must not be able to break the
            // path every client polls. Contained, never swallowed: see StatsObservation.
            var live = all.Where(s => confirmedLive.Contains(s.SessionId)).ToList();
            // RESOLVED PER REQUEST, never captured - see the parameter for why.
            if (inputStats?.Invoke() is { } statsNow)
                Stats.StatsObservation.Contain(statsNow.Health, "GET /sessions roster fold",
                    () => statsNow.ObserveSnapshot(live, DateTime.UtcNow, reqTenant.Value));

            // DevThrottle Stats: record fleet concurrency and the hourly activity log from the same
            // assembled roster - max concurrent loaded/running (live) and actively working, plus how many
            // distinct sessions/machines/repositories ran each hour. Per-tenant with no per-Director
            // instrumentation, since the roster already sees this tenant's sessions on every machine. The
            // tracker keeps only the higher value per hour, so folding on every /sessions read never inflates.
            // Contained for the same reason as the fold above, and separately from it: two observers, two
            // sets of counters, so a log line and a failure count name which one is failing.
            if (concurrency?.Invoke() is { } concurrencyNow)
                Stats.StatsObservation.Contain(concurrencyNow.Health, "GET /sessions concurrency observation",
                    () => concurrencyNow.Observe(live, DateTime.UtcNow, reqTenant.Value));

            // Issue #1292: adopt every observed number into the allocator's in-use set. This is how the
            // Gateway learns numbers it did not hand out - a number a Director assigned offline, or any
            // number still live after a Gateway restart - so it never hands the same number to a new
            // session. Adopt only ever marks a number in use (never frees one), so doing it from this
            // possibly-filtered view is safe: a Director that is momentarily absent from the aggregation
            // can never lose its numbers here.
            // Audit H2: adopt into THIS REQUEST'S tenant partition. The roster assembled above is this
            // tenant's own (the owned-Director gate filtered it), so its numbers belong to this tenant and
            // can never mark another account's partition in use.
            if (sessionNumbers is not null)
                foreach (var s in all)
                    if (s.Number is int num)
                        sessionNumbers.Adopt(reqTenant.Value, s.SessionId, s.DirectorId, num);

            if (envelope == true)
            {
                // Issue #1215: the envelope also carries the per-Director reachability (Online / Wobbly /
                // Offline / Not running, with a last-seen age), so the Cockpit renders the states in place.
                // machineErrors is retained unchanged for back-compat (an Offline Director appears in both).
                //
                // unreachableBanner is the FLEET-LEVEL verdict, folded here rather than counted by a view: a
                // machine is called unreachable only when every Director on it is, and a single dead slot on a
                // healthy machine is reported as the slot it is. Null when there is nothing wrong. A client
                // prints it verbatim; there is nothing left for it to decide.
                // Remove-the-network-port mission, phase 2: the COMPLETENESS verdict is folded here too.
                // It used to be folded by the Director, on the way past, because the command line reached
                // the fleet through its own Director; the tools now call this endpoint directly, and a
                // verdict computed by a middleman that no longer sits in the path has to move to the end
                // that still does. It is the same RosterCompleteness fold on the same reachability list, so
                // no wording changes and the two cannot say different things.
                //
                // Purely ADDITIVE - `sessions`, `machineErrors`, `directors` and `unreachableBanner` are
                // untouched, so every existing reader of this envelope (the Cockpit, the phone, and the
                // Director's own relay) is unaffected.
                var (rosterComplete, rosterIncompleteReason) = RosterCompleteness.Fold(reachability);
                return Results.Json(new
                {
                    sessions = all,
                    machineErrors,
                    directors = reachability,
                    unreachableBanner = FleetReachabilityFold.UnreachableBanner(reachability),
                    rosterComplete,
                    rosterIncompleteReason,
                    rosterStaleAnswerCaution = RosterCompleteness.StaleAnswerCaution(reachability),
                });
            }
            return Results.Json(all);
        })
        // Issue #806: advertise the default response shape (a SessionDto array) in the OpenAPI
        // document so the mobile app's openapi-typescript codegen generates a typed roster client.
        .Produces<List<SessionDto>>(StatusCodes.Status200OK);

        // Interrupted sessions (issue #212 W3): fan out to every Director for the crash
        // journals left on its machine by Directors that died abnormally, flatten to one row
        // per recoverable session, and enrich each with the Gateway's last-known brief so the
        // Cockpit Interrupted sessions list is triageable. Directors on one machine share the journal dir, so the
        // same dead journal can be reported by several live Directors - dedupe by directorId+pid.
        app.MapGet("/interrupted", async (HttpContext ctx, CancellationToken ct) =>
        {
            // MTR-01: the interrupted plane used the fleet-global director list, so it fanned out to - and
            // enumerated - every tenant's Directors. Scope it to THIS request's tenant so a caller only ever
            // sees, and only ever reaches over the tunnel, its own Directors' crash journals. A request with no
            // bound tenant is DENIED (403), never served the fleet-global list.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var directors = registry.ListDirectors(reqTenant.Value);
            var fanout = directors.Select(async d =>
            {
                // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-list verb, director-level).
                // A non-null stream result is authoritative for this Director - Ok carries its journals, a non-Ok
                // is treated as no journals (skipped).
                var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, d.DirectorId, "interrupted-list", "", null, ct, machineName: d.MachineName);
                // Post-cut: tunnel-only. A null result means the Director is not connected, so no journals.
                return (Director: d, Journals: sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<List<CrashJournalDto>>(sr) : null);
            }).ToList();
            var results = await Task.WhenAll(fanout);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outList = new List<InterruptedSessionDto>();
            foreach (var (d, journals) in results)
            {
                if (journals is null) continue;
                foreach (var j in journals)
                {
                    if (!seen.Add($"{j.DirectorId}.{j.Pid}")) continue; // already reported by a sibling Director
                    foreach (var s in j.Sessions)
                    {
                        var (railLine, headline) = interruptedBriefFor?.Invoke(s.SessionId) ?? (null, null);
                        outList.Add(new InterruptedSessionDto
                        {
                            SessionId = s.SessionId,
                            Name = s.Name,
                            RepoPath = s.RepoPath,
                            Agent = s.Agent,
                            ClaudeSessionId = s.ClaudeSessionId,
                            CreatedAtUtc = s.CreatedAtUtc,
                            DeadDirectorId = j.DirectorId,
                            DeadPid = j.Pid,
                            MachineName = j.MachineName,
                            User = j.User,
                            DiedAtUtc = j.LastUpdatedUtc,
                            ReportedByDirectorId = d.DirectorId,
                            RailLine = railLine,
                            Headline = headline,
                        });
                    }
                }
            }
            return Results.Json(outList.OrderByDescending(x => x.DiedAtUtc).ToList());
        });

        // Dismiss one interrupted journal once recovered or unwanted. Routed to the live
        // Director that surfaced it (via=reportedByDirectorId), which owns its machine's dir.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}", async (HttpContext ctx, string deadDirectorId, int deadPid, string? via, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            // MTR-01: resolve the reporting Director in the request's OWN tenant, so a caller cannot dismiss a
            // journal via another tenant's Director (403 with no tenant, 404 for a foreign id).
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, via, out _, out var err))
                return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-dismiss verb on the reporting
            // Director). The HTTP path collapsed any non-success (incl a 404) to false -> 502, so a non-Ok
            // stream result maps to 502 to stay byte-identical.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, via, "interrupted-dismiss", "",
                new InterruptedDismissRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid }, ct);
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502 like a failed dismiss.
            return sr is not null && sr.Ok ? Results.Json(new { dismissed = true }) : TunnelFailure(sr);
        });

        // Dismiss ONE session from an interrupted journal (issue #212 W4): the rest of the
        // journal stays in the Interrupted sessions list. Routed like the journal-level dismiss above.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}/sessions/{sessionId}",
            async (HttpContext ctx, string deadDirectorId, int deadPid, string sessionId, string? via, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            // MTR-01: resolve the reporting Director in the request's OWN tenant (403 with no tenant, 404 foreign).
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, via, out _, out var err))
                return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-remove verb on the reporting
            // Director). Non-Ok -> 502, matching the HTTP path's false -> 502 collapse.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, via, "interrupted-remove", "",
                new InterruptedRemoveRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid, SessionId = sessionId }, ct);
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return sr is not null && sr.Ok ? Results.Json(new { removed = true }) : TunnelFailure(sr);
        });

        // Restore one interrupted session (issue #212 W4): create a CONTINUATION session -
        // a fresh session in the dead session's repo, seeded with a context document built
        // from the Gateway's surviving turn-brief history. Never `claude --resume`. The
        // continuation is created on req.ToDirectorId when given, else on the reporting
        // Director (req.Via) - the reporter shares the dead Director's machine, so the repo
        // path is valid there. After a successful create the restored session is removed
        // from the dirty journal so the Interrupted sessions list reflects what is still unrecovered.
        app.MapPost("/interrupted/{deadDirectorId}/{deadPid:int}/restore",
            async (HttpContext ctx, string deadDirectorId, int deadPid, RestoreInterruptedRequest req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] POST /interrupted/{deadDirectorId}/{deadPid}/restore: sid={req?.SessionId} via={req?.Via} toDir={req?.ToDirectorId}");
            if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
                return Results.BadRequest(new { error = "sessionId is required" });
            if (string.IsNullOrWhiteSpace(req.Via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });

            // MTR-01: both the reporting Director and any explicit target Director are resolved in the request's
            // OWN tenant, so a restore can neither read a foreign crash journal nor spawn a continuation session
            // on another tenant's Director (403 with no tenant, 404 for a foreign id).
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, req.Via, out var reporter, out var reporterErr))
                return reporterErr;
            DirectorDto target;
            if (string.IsNullOrWhiteSpace(req.ToDirectorId))
                target = reporter;
            else if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, req.ToDirectorId, out target, out var targetErr))
                return targetErr;

            // The journal is the source of truth for what is restorable - never trust the
            // caller for repo/name. Re-read it from the reporting Director. Gateway Cleanup Phase 2 (PR D):
            // ride the tunnel first (interrupted-list verb on the reporting Director); a null return falls
            // back to the HTTP read. A non-Ok stream result surfaces as the same 502 the HTTP null produced.
            var journalsSr = await DirectorCommandRouter.TrySendAsync(sendCommand, req.Via, "interrupted-list", "", null, ct);
            // Post-cut: tunnel-only. A null result (reporting Director not connected) yields null journals -> 502 below.
            List<CrashJournalDto>? journals = journalsSr is not null && journalsSr.Ok
                ? DirectorCommandRouter.ReadBody<List<CrashJournalDto>>(journalsSr) : null;
            if (journals is null)
                return Results.Problem("reporting director did not serve its crash journals", statusCode: StatusCodes.Status502BadGateway);
            var journal = journals.FirstOrDefault(j =>
                string.Equals(j.DirectorId, deadDirectorId, StringComparison.OrdinalIgnoreCase) && j.Pid == deadPid);
            var row = journal?.Sessions.FirstOrDefault(s =>
                string.Equals(s.SessionId, req.SessionId, StringComparison.OrdinalIgnoreCase));
            if (journal is null || row is null)
                return Results.NotFound(new { error = "interrupted session not found in that journal (already restored or dismissed?)" });

            var briefs = briefHistoryFor?.Invoke(row.SessionId) ?? new List<TurnBriefDto>();
            var context = Recovery.RestoreContextBuilder.Build(
                row.Name, row.SessionId, row.RepoPath, row.ClaudeSessionId, journal.LastUpdatedUtc, briefs);

            // Create the continuation over the tunnel (create verb, director-level so SessionId is "").
            // The tunnel unary has no 2s aggregate timeout - keep-alive sustains a multi-second spawn - so
            // the orphan risk the old dedicated 20s HttpClient guarded against does not apply.
            var spawnReq = new NewSessionRequest
            {
                RepoPath = row.RepoPath,
                Agent = row.Agent,
                PrePrompt = context,
                // Session origin (devthrottle_internal issue #982). The SURFACE is certain - this is a
                // direct Gateway API route, not the command line and not a schedule - so it is stated.
                // The KIND is NOT: restoring an interrupted session can be asked for by a person in the
                // Cockpit or by an agent cleaning up after a crash, and this handler cannot tell which.
                // Left unstated, so it records "unknown", which is exactly what we know.
                OriginSurface = Core.Sessions.SessionOriginSurfaces.Api,
            };
            var createSr = await DirectorCommandRouter.TrySendAsync(sendCommand, target.DirectorId, "create", "", spawnReq, CancellationToken.None, machineName: target.MachineName);
            if (createSr is null)
                return Results.Problem("target director is not connected to the tunnel", statusCode: StatusCodes.Status502BadGateway);
            SessionDto? created = createSr.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(createSr) : null;
            if (created is null && createSr.Ok is false)
                return Results.Problem(
                    $"target director failed to create the continuation session: {DirectorCommandRouter.DescribeFailure(createSr)}",
                    statusCode: StatusCodes.Status502BadGateway);
            if (created is null)
                return Results.Problem("target director returned an empty session body", statusCode: StatusCodes.Status502BadGateway);
            created.DirectorId = target.DirectorId;
            FileLog.Write($"[GatewayEndpoints] restore: created continuation {created.SessionId} on {target.DirectorId} for dead {row.SessionId}");

            // Give the continuation the dead session's name. Best-effort: a failed rename
            // does not undo a successful restore.
            var restoredName = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name;
            if (restoredName is not null)
            {
                // Gateway Cleanup Phase 2: rename over the tunnel (patch verb, tunnel-first, HTTP fallback pre-cut).
                var renameReq = new SessionUpdateRequest { Name = restoredName };
                SessionDto? renamed; string? patchErr;
                var patchSr = await DirectorCommandRouter.TrySendAsync(sendCommand, target.DirectorId, "patch", created.SessionId, renameReq, CancellationToken.None, machineName: target.MachineName);
                // Post-cut: tunnel-only. A null result (Director not connected) leaves the rename un-applied.
                renamed = patchSr is not null && patchSr.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(patchSr) : null;
                patchErr = patchSr is null ? "target director not connected to the tunnel"
                    : (patchSr.Ok ? null : DirectorCommandRouter.DescribeFailure(patchSr));
                if (renamed is not null) { renamed.DirectorId = target.DirectorId; created = renamed; }
                else FileLog.Write($"[GatewayEndpoints] restore: rename failed (continuing): {patchErr}");
            }

            // Pull the restored session out of the Interrupted sessions list. Best-effort too - the
            // user can still Dismiss the row by hand if this leg fails.
            // Gateway Cleanup Phase 2: journal cleanup over the tunnel (interrupted-remove verb on the reporting
            // Director, tunnel-first, HTTP fallback pre-cut).
            var removeReq = new InterruptedRemoveRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid, SessionId = row.SessionId };
            var removeSr = await DirectorCommandRouter.TrySendAsync(sendCommand, reporter.DirectorId, "interrupted-remove", "", removeReq, CancellationToken.None, machineName: reporter.MachineName);
            var cleaned = removeSr is not null && removeSr.Ok;
            if (!cleaned)
                FileLog.Write($"[GatewayEndpoints] restore: journal cleanup failed for {row.SessionId} (row stays in the Interrupted sessions list)");

            return Results.Json(new RestoreInterruptedResponse
            {
                Restored = true,
                TargetSession = created,
                ContextSent = context,
                JournalCleaned = cleaned,
            }, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/sessions/{sid}", async (HttpContext ctx, string sid) =>
        {
            // Hosted Multi-Tenancy (session-serving PR1): resolve the request's tenant from the authenticated
            // device key and DENY (403) when hosted returns no bound tenant - a by-id read must never fall back
            // to Local or SYSTEM. On self-host this is always Local.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

            // LocateSessionAsync resolves the OWNING DIRECTOR (and refreshes the ownership record). It also
            // hands back a session copy, which we deliberately drop: that copy is not part of the role
            // universe assembled below, and stamping an instance the role pass never walked would leave
            // SessionRole null and fold a colour from it. We take our instance from the fleet instead.
            var (director, _) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, reqTenant.Value, owners);
            if (director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            // Defect 15: this route returned EffectiveColor / StateLabel / TriageBucket as NULL and left the
            // expired-snooze override unapplied, because it never ran the fold - StampFleetRolesAndFold was
            // private to the roster handler and this route just serialized the raw cached DTO. SessionDto
            // documents all three as "Required on Gateway /sessions responses", and this route violated it.
            //
            // HONEST SCOPE: that is a verified CODE fact, not an observed symptom. No shipped client fetches
            // this route - the Cockpit and the phone read the roster and go through client-core, which throws
            // if the fields are missing, and neither app calls this. The fix is justified by the contract the
            // DTO documents, not by a user-visible bug, and no such bug is claimed.
            var byDirector = FleetByDirector(registry, pushedSessions, streamStaleResolved, reqTenant.Value);
            var fleet = byDirector.Values.SelectMany(x => x).ToList();
            var session = fleet.FirstOrDefault(x => string.Equals(x.SessionId, sid, StringComparison.Ordinal));
            if (session is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            var baseUrl = DeriveDirectorBaseUrl(ctx, director);
            session.DirectorId = director.DirectorId;
            session.MachineName = director.MachineName;
            session.User = director.User;
            session.TailnetEndpoint = baseUrl;
            session.ViewUrl = GatewaySessionLink(DeriveGatewayBaseUrl(ctx), session.SessionId);

            // needsYouStampFor is deliberately NOT passed: the needs-you clock has entry/exit semantics and
            // is driven by the roster read. Letting a by-id read stamp it would drive that clock out of band
            // and corrupt the roster's own waiting times. NeedsYouSince stays unstamped here, exactly as
            // before - this fix does not claim it.
            StampFleetRolesAndFold(fleet, new[] { session }, needsYouStampFor: null, snoozeRegistry: snoozeRegistry, tenant: reqTenant.Value, handRaises: handRaises);
            return Results.Json(session);
        });

        // ---------------------------------------------------------------------------------------------
        // STOPPING A SESSION (mission "Stop a session"). ONE stop, ONE fold, ONE audit trail, behind TWO
        // DOORS. Everything below this comment is that one handler and the two doors onto it.
        // ---------------------------------------------------------------------------------------------

        // The one stop. Both doors land here, so there is one place the Director is asked, one place the
        // sentences are folded (SessionStopFold) and one place the audit row is written.
        //
        // THE REASON IS OPTIONAL HERE, AND THAT IS DELIBERATE. Ruling 4 says an agent's key can only ever
        // stop a session with a reason attached - and that requirement lives on the POST DOOR, not in this
        // shared handler, because the other door (DELETE /sessions/{sid}) carries no body and therefore can
        // carry no reason. Making the requirement structural instead would have broken shipped clients in
        // the middle of the mission. What keeps the ruling exact is the ALLOW LIST: the bare DELETE stays
        // REFUSED to session keys (see SessionKeyGuard), so the only callers who can reach this handler
        // without a reason are devices and people. Do not "fix" this by demanding a reason here.
        //
        // When no reason arrives, the audit row SAYS SO in words. It never fabricates one.
        //
        // <paramref name="legacyDeleteDoor"/> changes ONE thing and nothing else: what "there is no such
        // session in this account" answers with. See the notOnFleet branch below for why the old door keeps
        // its 404. The stop itself, the fold and the audit row are identical on both doors.
        async Task<IResult> StopSessionAsync(
            HttpContext ctx, string sid, string? reason, bool legacyDeleteDoor, CancellationToken ct)
        {
            // Resolve the tenant explicitly rather than through LocateSessionForRequestAsync, because that
            // helper collapses "no tenant is bound to this request" and "no such session" into the same
            // pair of nulls - and this route MUST tell them apart. Answering notOnFleet (a success) to an
            // unauthenticated-tenant request would tell a caller that nothing in the account carries that
            // identifier when nothing ever looked in an account at all.
            var tenant = ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

            var actor = StopActorFor(ctx);
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, tenant.Value, owners);
            if (director is null || session is null)
            {
                // NOT FOUND FRESH IS NOT THE SAME AS NOT IN THE ACCOUNT. A session whose Director has simply
                // stopped pushing IS in this account, so answering "nothing in this account carries the id"
                // would be false - and it is Ruling 3's OTHER case: located, not reachable, a failure. That
                // is what SessionUnavailable already says, with a retry-after and a sentence.
                var knownButStale = pushedSessions is not null
                    && pushedSessions.TryLocateIgnoringFreshness(tenant.Value, sid) is not null;
                if (knownButStale)
                {
                    FileLog.Write($"[GatewayEndpoints] stop {sid}: the owning Director has not pushed recently - answering retryable rather than notOnFleet");
                    return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
                }

                // THE LEGACY DOOR KEEPS ITS 404, AND THIS IS THE ONE PLACE THE TWO DOORS DIFFER.
                //
                // Found by the parked suite, which is the only place either of these runs: turning the old
                // DELETE into a thin forward silently changed its answer for an unknown session from 404 to
                // a 200, and broke two EXISTING tests -
                // StreamCommandTests.StreamModeOff_KillEndpoint_StaysOnHttp and, far more seriously,
                // HostedSessionCommandRouteTenancyTests.Another_tenant_cannot_reach_it(DELETE), which is a
                // CROSS-TENANT ISOLATION test: it pins that one account naming another account's session id
                // gets exactly the locator's not-found answer.
                //
                // Ruling 3's "notOnFleet is a success" is about THE STOP - the verb this mission adds, which
                // the command line calls and which POST /sessions/{sid}/stop serves. DELETE is a legacy door
                // kept for one reason only: a shipped native phone client that does not deploy with the
                // Gateway calls it. Keeping it means keeping its answers. Changing its status codes is a
                // quieter way of breaking the app in somebody's hand, which is the very thing keeping it was
                // for - and the fix that would have made the tenancy test pass again was to EDIT the tenancy
                // test, which is exactly the move to distrust.
                //
                // This is not two stops. Nothing has been stopped on this path: no Director was asked, no
                // answer was folded, no audit row is written. The divergence is only in how each door says
                // "there is nothing of yours here", and the stop itself stays single.
                if (legacyDeleteDoor)
                {
                    FileLog.Write($"[GatewayEndpoints] stop {sid}: nothing in this tenant carries that identifier; the legacy DELETE door answers not-found (actor={actor})");
                    return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
                }

                // notOnFleet: a SUCCESS (Ruling 3), never SessionUnavailable's 404 - a 404 would make the
                // SECOND stop an error, which is the exact failure that ruling exists to prevent.
                //
                // NO AUDIT ROW IS WRITTEN HERE, AND THAT IS A DELIBERATE GAP RATHER THAN AN OVERSIGHT.
                // Nothing was stopped, and there is no session in this account for the row to be about -
                // GovernanceAuditLog rows are keyed by session id, so a row here would attach a stop to an
                // identifier the account has never seen. What it costs: a caller repeatedly stopping an
                // identifier that does not exist leaves no trace. That is accepted; nothing is destroyed.
                FileLog.Write($"[GatewayEndpoints] stop {sid}: notOnFleet - no session in this tenant carries that identifier; no machine was asked (actor={actor})");
                return Results.Json(SessionStopFold.NotOnFleet(sid, reason, actor));
            }

            // -----------------------------------------------------------------------------------------
            // EVERYTHING BELOW THIS LINE HAS DISPATCHED A STOP, AND THE AUDIT ROW BELONGS TO THE DISPATCH
            // RATHER THAN TO THE REPLY.
            //
            // The owner accepted Ruling 4 - any session may stop any other - ON THE EXPLICIT GROUND THAT
            // STOPS ARE AUDITED. An audit that only lands when the caller is still listening is not an
            // audit. Inspection 1 finding I1: this handler used to append the row only after a successful
            // reply, so an ordinary operator action lost it. Closing the desktop stop dialog cancels its
            // request (StopSessionDialog.axaml.cs) and its client gives up after ten seconds
            // (GatewayClient.cs); a Director that carried the stop out and answered a moment too late left
            // ZERO rows behind. A tunnel timeout lost the row the same way. No database fault was needed.
            //
            // Two things change and nothing else does. The row is written on EVERY road out of here that
            // dispatched something - including the roads where the Gateway never learns what came of it,
            // which are recorded as exactly that. Where the outcome IS known, the row is unchanged.
            //
            // The request's token still goes down the tunnel on purpose: a caller who has gone away should
            // not hold a Director wait open. What it must no longer do is delete the record.
            var recorded = false;
            void RecordOnce(string? verdict, string? outcomeUnknownBecause = null)
            {
                // One dispatch, one row. Nothing between the append and the return can throw today, but a
                // trail that could hold two rows for one stop would read as two stops - which is the same
                // class of untruth this finding is about, facing the other way.
                if (recorded)
                    return;
                recorded = true;
                RecordStopInTheAuditTrail(session.SessionId, verdict, actor, reason, outcomeUnknownBecause);
            }

            try
            {
                // Tunnel-only, exactly as the DELETE forwarded it before this mission. On a timeout or a
                // mid-flight drop the session may or may not have been stopped, and TunnelFailure carries
                // the Director's own words about which.
                //
                // THE REASON NOW GOES DOWN THE TUNNEL instead of a null payload, so the Director is given
                // the caller's own words for the stop it is about to carry out. STATED PLAINLY BECAUSE IT
                // MATTERS: the Director IGNORES this payload today - SessionCommandExecutor.KillAsync
                // never reads command.PayloadJson - so nothing on the far end reads it yet, and no
                // consumer is being claimed here. It is sent because the Gateway holding the reason while
                // the machine doing the destroying is never told it is part of what I1 named; the Director
                // half of that belongs to another seat.
                var streamResult = await DirectorCommandRouter.TrySendAsync(
                    sendCommand, director.DirectorId, "kill", sid,
                    string.IsNullOrWhiteSpace(reason) ? null : new SessionStopRequest { Reason = reason },
                    ct, machineName: director.MachineName);
                if (streamResult is null || !streamResult.Ok)
                {
                    FileLog.Write($"[GatewayEndpoints] stop {sid} FAILED on the tunnel to director={director.DirectorId}: {streamResult?.Error ?? "the Director is not connected"}");

                    // WHICH OF THESE FAILURES ACTUALLY SENT A STOP. A null result means nothing left the
                    // Gateway at all, so no row: see DispatchOutcomeUnknownBecause for the full reasoning
                    // and for the one road where the two cannot be told apart.
                    return TunnelFailure(streamResult, director.MachineName);
                }

                DirectorStopResult? answer = null;
                if (!string.IsNullOrWhiteSpace(streamResult.BodyJson))
                {
                    try
                    {
                        answer = JsonSerializer.Deserialize<DirectorStopResult>(streamResult.BodyJson, JsonWeb);
                    }
                    catch (JsonException ex)
                    {
                        FileLog.Write($"[GatewayEndpoints] stop {sid}: the Director's answer could not be read: {ex.Message}");
                    }
                }

                // An answer this Gateway cannot describe is NOT refused. The tunnel returned Ok, so the
                // verb ran and the session is stopped; answering a failure for that would report a failure
                // for an operation that succeeded, which is the exact complaint this mission exists to
                // fix. It folds to stoppedNotDescribed instead - the stop is reported, the description is
                // not invented. The commonest cause is a Director older than this Gateway, which reports
                // only the original killed/removed pair and says nothing about what it found.
                //
                // BOTH DOORS get this. DELETE /sessions/{sid} must not start failing against an older
                // Director: it succeeds there today, and shipped clients call it.
                if (!SessionStopFold.CanDescribe(answer))
                    FileLog.Write($"[GatewayEndpoints] stop {sid}: the Director on {director.MachineName} could not describe the stop (older version?): body={streamResult.BodyJson ?? "(none)"}");

                var response = SessionStopFold.Fold(sid, answer ?? new DirectorStopResult(), reason, actor);
                FileLog.Write($"[GatewayEndpoints] stop {sid}: {response.Headline} (actor={actor})");

                RecordOnce(response.Verdict);
                return Results.Json(response);
            }
            catch (OperationCanceledException)
            {
                // THE CALLER WENT AWAY AFTER THE STOP WAS SENT. This is the exact road I1 reproduced: the
                // Director had removed the session and was about to answer when the request was cancelled,
                // and the handler left without recording anything. Hanging up does not undo the stop, so
                // the row is written here and the cancellation is then allowed to continue - it is still a
                // cancellation, and reporting it as anything else would be a second untruth.
                //
                // The append itself takes no cancellation token and does no awaiting, so a cancelled
                // request cannot interrupt it half-written.
                RecordOnce(verdict: null, CallerLeftBeforeTheAnswer);
                throw;
            }
        }

        // Why the Gateway does not know what came of a stop it sent, or null when nothing was sent and
        // therefore nothing happened that a row could be about.
        //
        // NOTHING WAS DISPATCHED (no row):
        //   - a null result. The Director is not tunnel-connected, or the Gateway refused its own send.
        //     Either way the command never left, and a row would attach a stop to something that did not
        //     happen - the same reasoning the notOnFleet branch above already spells out.
        //   - a failure the DIRECTOR ITSELF sent (BadRequest, NotFound, Conflict, Locked, Error). The
        //     Director was reached and answered; it is saying what it did, and it is the one party that
        //     knows. "The process would not die" is the live example: the row is deliberately left in
        //     place there and the session is still running, so recording a stop against it would be
        //     false. The caller gets that failure verbatim.
        //
        // THE OUTCOME IS UNKNOWN (a row saying so):
        //   - Timeout. The Gateway synthesized it because no answer came; the Director may well have
        //     carried the stop out and answered late. DirectorCommandRouter's own timeout sentence tells
        //     the caller the same thing.
        //   - TunnelDropped. HONEST LIMIT, NAMED RATHER THAN GLOSSED: on this road the Gateway genuinely
        //     cannot tell "the send threw before the command left" from "it was delivered and the
        //     connection then died". Both are recorded as unknown, because between a row that says the
        //     outcome is not known for a stop that never left, and no row at all for a stop that
        //     happened, silence is the worse error.
        static string? DispatchOutcomeUnknownBecause(DirectorCommandResult? streamResult, CancellationToken ct)
        {
            if (streamResult is null)
                return null;

            return streamResult.Status switch
            {
                DirectorCommandStatus.Timeout => "the Director did not answer in time",
                // The router turns a caller cancellation into this status whenever the send throws
                // something that is not an OperationCanceledException - which SignalR does, as the
                // router's own comment records. Say which of the two actually happened rather than
                // blaming the tunnel for the caller leaving.
                DirectorCommandStatus.TunnelDropped => ct.IsCancellationRequested
                    ? CallerLeftBeforeTheAnswer
                    : "the tunnel dropped while the stop was in flight",
                _ => null,
            };
        }

        // Who asked, read ONCE off the items the authentication gate stamped - never re-derived from the raw
        // request, because two parsers of one request eventually disagree. The words themselves are composed
        // in the fold, which is pure and testable without a server.
        string StopActorFor(HttpContext ctx)
        {
            var callingSession = AuthMiddleware.CallingSession(ctx);
            var device = ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedDeviceItemKey, out var d)
                ? d as Pairing.DeviceCredentialIdentity
                : null;
            var credentialAuthenticated = ctx.Items.ContainsKey(AuthMiddleware.AuthenticatedCredentialItemKey);
            return SessionStopFold.ActorFor(
                callingSession?.SessionId.ToString(), device?.DeviceType, device?.DeviceId, credentialAuthenticated);
        }

        // The audit detail. For an ordinary stop it is the reason, unchanged: capped to what the trail
        // accepts and the SAME string the response showed, so what an operator read and what the trail
        // holds cannot differ.
        //
        // For a stop the Gateway sent but could not learn the outcome of, the unknown is said FIRST and the
        // reason follows it. That order is the whole design of this string: the cap below truncates the
        // TAIL, so if a five-hundred-character reason has to lose something it loses the reason and keeps
        // the fact that the outcome is not known. An operator reading the trail must never see what looks
        // like a plain completed stop when the Gateway never established that it completed.
        static string StopAuditDetail(string? reason, string? outcomeUnknownBecause)
        {
            var reasonPart = string.IsNullOrWhiteSpace(reason) ? SessionStopFold.NoReasonGivenDetail : reason;
            var detail = outcomeUnknownBecause is null
                ? reasonPart
                : $"{StopOutcomeUnknownPrefix}: {outcomeUnknownBecause}. reason: {reasonPart}";

            // The trail REJECTS an over-long detail rather than trimming it, and that rejection would be
            // swallowed by the best-effort catch above - losing the very row this finding exists to add.
            // The POST door already caps the reason; this caps what is built around it.
            return detail.Length > Governance.GovernanceAuditLog.MaxDetailChars
                ? detail[..Governance.GovernanceAuditLog.MaxDetailChars]
                : detail;
        }

        // The audit row. The owner accepted "any session may stop any other" ON THE EXPLICIT GROUND THAT IT
        // IS AUDITED, so this is load-bearing, not decoration - and a stop served with no audit log wired
        // says so LOUDLY rather than passing silently, because a check whose pass condition is an absence
        // certifies a run that never happened.
        //
        // A failed write does not fail the response. The session is already stopped by this point, and
        // answering an error would tell the operator the stop did not happen, which would be a lie about the
        // one fact this verb exists to report. It is logged as FAILED instead.
        //
        // IT TAKES NO CANCELLATION TOKEN, AND IT NEVER WILL. This is the record of a destructive act that
        // has already been asked for; it does not belong to the lifetime of the request that asked. Handing
        // it the caller's token would restore inspection 1 finding I1 exactly - see the dispatch comment in
        // StopSessionAsync. The append is synchronous and does not await, so a cancelled request cannot
        // interrupt it part-written either.
        //
        // <paramref name="verdict"/> is for the LOG LINE ONLY and is null where the Gateway never learned
        // one. It is not a verdict word and no fifth verdict exists.
        // <paramref name="outcomeUnknownBecause"/>, when present, says why the Gateway could not learn what
        // came of a stop it had already sent. The row then records THAT, in words, rather than not existing.
        void RecordStopInTheAuditTrail(
            string sessionId, string? verdict, string actor, string? reason, string? outcomeUnknownBecause = null)
        {
            var reported = verdict ?? "outcome not known";
            if (governanceAudit is null)
            {
                FileLog.Write($"[GatewayEndpoints] stop {sessionId}: NO AUDIT LOG IS WIRED - verdict={reported}, actor={actor}. "
                    + "This stop is NOT recorded in the governance trail. Every stop is meant to be audited.");
                return;
            }

            try
            {
                governanceAudit.Append(new AppendGovernanceAuditEventRequest
                {
                    SessionId = sessionId,
                    Category = GovernanceAuditCategory.Intervention,
                    // The SAME event type as any other stop, deliberately. A stop whose outcome is unknown
                    // is still a stop that was sent, and it has to appear to anyone reading the stops for
                    // this session - which a separate event type nobody queries would prevent. What is
                    // unknown is said in the detail, in words, where it is read rather than decoded.
                    EventType = GovernanceAuditEventType.Stopped,
                    Actor = actor,
                    Detail = StopAuditDetail(reason, outcomeUnknownBecause),
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayEndpoints] stop {sessionId}: WRITING THE AUDIT ROW FAILED: {ex.Message}. "
                    + $"The session was stopped (verdict={reported}, actor={actor}) and the trail does not record it.");
            }
        }

        // Door one: POST /sessions/{sid}/stop, body { "reason": "..." }. THE stop. It matches every other
        // session verb on the Gateway (prompt, interrupt, hold, request-deletion are all
        // POST /sessions/{sid}/<verb>), and a body is the natural place for a sentence.
        app.MapPost("/sessions/{sid}/stop", async (HttpContext ctx, string sid, SessionStopRequest? body, CancellationToken ct) =>
        {
            // Ruling 4: no reason, no stop. The sentence names the REASON as what is missing and names no
            // flag - the Gateway does not know what a client's options are called, and a client naming its
            // own flag is not a client deciding what a state means.
            var reason = (body?.Reason ?? "").Trim();
            if (reason.Length == 0)
            {
                FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/stop REFUSED - no reason was given");
                return Results.BadRequest(new { error = SessionStopFold.ReasonMissing });
            }

            // Capped where it is READ rather than where it is written, so the reason shown in the answer and
            // the reason held in the audit trail are the same string.
            if (reason.Length > Governance.GovernanceAuditLog.MaxDetailChars)
                reason = reason[..Governance.GovernanceAuditLog.MaxDetailChars];

            return await StopSessionAsync(ctx, sid, reason, legacyDeleteDoor: false, ct);
        });

        // Door two: DELETE /sessions/{sid}. A THIN FORWARD into the same handler, the same fold and the same
        // audit trail - not a second implementation.
        //
        // WHY IT IS KEPT. A native phone client (phone/CcDirectorClient/Voice/GatewayClient.cs) calls it and
        // does NOT deploy inside the Gateway container, so removing it would break an app in somebody's hand.
        // It carries no body, so it carries no reason, and it stays REFUSED to session keys in
        // SessionKeyGuard - that refusal is what keeps the owner's ruling exact.
        //
        // THE RESPONSE SHAPE GREW, AND HERE IS EXACTLY WHAT THAT CLAIM RESTS ON. The one remaining caller -
        // the native phone client - reads the STATUS CODE ONLY and ignores the body entirely. So the body
        // may become the full stop answer, which still carries the original killed/removed pair. That is
        // the whole of the claim: nothing is said here about clients nobody has read.
        //
        // Phase B took the OTHER caller off this door. `killSession` in packages/client-core/src/api/client.ts
        // is deleted; the Cockpit, the phone application and the Director window all go through
        // POST /sessions/{sid}/stop now, so they carry a reason. That leaves the native phone client as the
        // SOLE reason this door exists - and it is a sufficient one, because it does not ship inside the
        // Gateway container and so cannot be updated in lockstep with it.
        // ITS ANSWER FOR AN UNKNOWN SESSION IS UNCHANGED - still the locator's 404, not the stop's
        // notOnFleet. Keeping a door for compatibility means keeping what it answers; see the notOnFleet
        // branch in StopSessionAsync for the two existing tests that proved it, one of them a cross-tenant
        // isolation test.
        app.MapDelete("/sessions/{sid}", async (HttpContext ctx, string sid, CancellationToken ct) =>
            await StopSessionAsync(ctx, sid, reason: null, legacyDeleteDoor: true, ct));

        // Forward "flag this session for deletion" to the owning Director, so a session on ONE
        // machine (or a remote client) can request the async teardown of a session on another. The
        // owning Director's reaper does the actual removal. Body is optional ({ "reason": "..." }).
        app.MapPost("/sessions/{sid}/request-deletion", async (HttpContext ctx, string sid, SessionDeletionRequest? body, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Tunnel-only. The Ok result is success and synthesizes the { pendingDeletion } body; a null result
            // (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "request-deletion", sid, body, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok
                ? Results.Json(new { pendingDeletion = true })
                : TunnelFailure(streamResult);
        });

        // Forward "cancel the pending deletion" to the owning Director (grace-window undo).
        app.MapDelete("/sessions/{sid}/request-deletion", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Gateway Cleanup (Phase 2, PR C): tunnel-first, HTTP fallback on a null return (byte-identical).
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "cancel-deletion", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok
                ? Results.Json(new { pendingDeletion = false })
                : TunnelFailure(streamResult);
        });

        // Phase 4b: forward wingman observability through the Gateway so the merged
        // Session View on the gateway side can render WHY a dot is the color it is.
        app.MapGet("/sessions/{sid}/wingman", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Tunnel-only. The Ok body IS the WingmanViewDto JSON, passed through exactly as the HTTP body.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-view", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Phase 5: forward "ask the wingman" calls. Each is one fresh side-call
        // (Haiku for free-text asks; Opus when Mode=="explain"). Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/ask", async (HttpContext ctx, string sid, WingmanAskRequest req, CancellationToken ct) =>
        {
            var explain = string.Equals(req?.Mode, "explain", StringComparison.OrdinalIgnoreCase);
            if (req is null || (!explain && string.IsNullOrWhiteSpace(req.Question)))
                return Results.BadRequest(new WingmanAskResult { Status = "bad_request", Error = "question is required" });
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Gateway Cleanup (Phase 2, PR C): tunnel-first. This is a SLOW LLM call - the request ct threads
            // straight into the SignalR invocation (which has no per-invocation timeout; keep-alive pings sustain
            // the long await), so the synchronous browser contract is byte-identical to the HTTP forward. A null
            // The Ok body IS the WingmanAskResult JSON.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            // Runs a language model on the Director before it can answer, so it gets the longer wait.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-ask", sid, req, ct,
                timeout: DirectorCommandRouter.LanguageModelCommandTimeout, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Forward "set the session goal" to the owning Director. Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/goal", async (HttpContext ctx, string sid, WingmanGoalRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            var goalReq = req ?? new WingmanGoalRequest();
            // Post-cut: tunnel-only. The Ok stream body IS the { goal, goalSetAt, goalState } JSON; a null
            // result (Director not connected) or a non-Ok result collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-goal", sid, goalReq, ct, machineName: director.MachineName);
            var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
        });

        // Automatic session roles (chunk 2.5): (re)declare a session's sticky explicit role, routed DOWN the
        // stream first (DirectorCommandRouter), HTTP fallback otherwise. The Ok body is the updated SessionDto.
        app.MapPost("/sessions/{sid}/role", async (HttpContext ctx, string sid, SetRoleRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            var roleReq = req ?? new SetRoleRequest();
            // Post-cut: tunnel-only. The Ok stream body is the updated SessionDto JSON; a null or non-Ok result collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "set-role", sid, roleReq, ct, machineName: director.MachineName);
            var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
        });

        // Issue #2387: attach a session that ALREADY EXISTS to a Mission - or DETACH it. Sessions could only
        // ever be attached in the instant they were spawned (`session spawn --mission`), so a Mission could
        // only group work somebody planned in advance, which is the work that least needs grouping. The case
        // that found this was a release push that grew from one seat to about a dozen over a day: every one of
        // them was the same body of work and not one was foreseeable at spawn.
        //
        // THE TENANT GATE, and it is the whole reason this route is written out longhand rather than folded in
        // beside /role. Missions are TENANT-SCOPED (devthrottle_internal issue #1039): a mission NAME is free
        // text a person typed - customer and project names - and a shared hosted store once served every
        // account's list to every account. This route is a WRITE that names a mission by id, so it must not
        // become the way back in. It follows GET /missions/{mid} exactly:
        //   1. Resolve the CALLER's own tenant from the authenticated device key, server-side. A request that
        //      binds to no tenant is REFUSED (403) - never served the Local partition.
        //   2. Resolve the mission INSIDE that tenant. Another account's mission id resolves to nothing, and
        //      "someone else owns it" answers identically to "nobody has it", so the id cannot even be probed.
        //   3. Only then locate the session, which is itself tenant-scoped by LocateSessionForRequestAsync.
        // The mission is resolved BEFORE the session on purpose: the refusal is then a property of the tenant
        // gate alone, and cannot be confused with (or accidentally satisfied by) a Director being offline.
        //
        // The Gateway sends the RESOLVED NAME down with the id, so the Director stamps the attachment directly
        // instead of consulting its own local mission store - which is a different set and would reject a
        // mission that is real and owned (the failure #1548 fixed on the spawn path).
        if (missions is not null)
        {
            app.MapPost("/sessions/{sid}/mission", async (HttpContext ctx, string sid, SetMissionRequest req, CancellationToken ct) =>
            {
                var tenant = ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                {
                    FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/mission DENIED - no tenant is bound to this request");
                    return Results.Json(new { error = "no tenant is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                // Null/absent mission id DETACHES. Nothing to resolve, and nothing to leak.
                var payload = new SetMissionRequest { MissionId = req?.MissionId };
                if (req?.MissionId is Guid missionId)
                {
                    var mission = missions.Get(tenant.Value, missionId);
                    if (mission is null)
                    {
                        FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/mission: mission {missionId} is unknown to this tenant");
                        return Results.BadRequest(new
                        {
                            error = $"unknown mission '{missionId}'. List the missions with: cc-devthrottle mission list",
                        });
                    }
                    payload.MissionName = mission.MissionName;
                }

                var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
                if (session is null || director is null)
                    return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

                // ===== THE SEAT MOVES WITH THE MISSION =====
                //
                // A Mission is not only a record - it is also a RUN of the built-in "mission" workflow, and a
                // mission-scoped spawn seats the session on that run and records it in the run's participant
                // ledger. The seat is what pins the CONDUCT the agent was told to follow. So changing only the
                // mission link would leave a session DISPLAYED under one mission while GOVERNED by the one it
                // left, taking its conduct from a mission it is no longer in - and it would do that in exactly
                // the case this feature was built for. The seat therefore moves here, in the same call.
                //
                // THE RULE, and its one exception:
                //  * A seat that IS a run of the mission being left BELONGS to that mission, and follows it.
                //  * A seat the caller chose independently (spawned with an explicit --workflow-run that is
                //    not this mission's run) was never the mission's to take, and is PRESERVED untouched.
                //  * A session with no seat at all has nothing to preserve, so it simply gains the
                //    destination mission's seat.
                //
                // This decision is made HERE and nowhere else. Whether a run belongs to a mission is a fact
                // about the run store, which only the Gateway holds; a Director asked to decide it would have
                // to guess. The Director is sent a finished answer (MoveSeat plus the run to sit on) and
                // applies it in the same verb as the mission, so the two can never land apart.
                //
                // The session facts below are read from the located DTO, which is FRESH by construction: a
                // session whose Director has not pushed inside the freshness window is answered 503 by
                // SessionUnavailable above rather than served, so this is a current read and not a snapshot
                // of unknown age.
                //
                // ON THE RUN STORE AND TENANCY, because it looks like a hole and is not. The run store is not
                // itself partitioned by tenant, so both lookups below reach it by a bare id. Neither id is
                // caller-supplied: the destination is a mission this route has ALREADY resolved inside the
                // caller's own tenant, and the held run comes off the session DTO, which the tenant-scoped
                // locator produced. A caller therefore cannot steer either lookup at a run it does not own.
                // That the store would answer a foreign id if one reached it is a pre-existing property of
                // that store and is not made reachable here.
                var currentRunId = session.WorkflowRunId;
                var seatIsTheMissions = true;   // no seat -> nothing to preserve
                if (currentRunId is Guid heldRun)
                {
                    // Does the run the session sits on belong to the mission it is in? Only then is it the
                    // mission's seat. A run whose MissionId does not match (or a run that has since been
                    // removed) is treated as the caller's own and left alone - the conservative direction:
                    // preserving a seat we are unsure about is recoverable, silently discarding one is not.
                    var heldRunRecord = workflowRuns?.Get(heldRun);
                    seatIsTheMissions = heldRunRecord?.MissionId is Guid ownerMission
                        && session.MissionId is Guid currentMission
                        && ownerMission == currentMission;
                }

                Guid? previousRunId = null;
                string? seatNote = null;
                if (seatIsTheMissions)
                {
                    previousRunId = currentRunId;
                    payload.MoveSeat = true;
                    // The destination mission's run, or none when detaching or when the mission is UNGOVERNED
                    // (created while the owner had the mission workflow switched off). A mission with no run
                    // seats nobody, so the session correctly ends up unseated rather than keeping the old seat.
                    if (payload.MissionId is Guid destination && workflowRuns is not null)
                    {
                        var destinationRun = workflowRuns.List(missionId: destination, limit: 1).FirstOrDefault();
                        if (destinationRun is not null && destinationRun.WorkflowEnabled)
                        {
                            payload.WorkflowRunId = destinationRun.Id;
                            payload.WorkflowId = destinationRun.WorkflowId;
                            payload.WorkflowVersion = destinationRun.WorkflowVersion;
                        }
                        else if (destinationRun is not null)
                        {
                            // The owner switched this workflow OFF. The spawn path already refuses to seat
                            // onto a disabled workflow; a move must not sneak a seat in through the side door.
                            FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/mission: mission {destination}'s " +
                                          $"workflow is OFF - the session moves UNSEATED");
                            seatNote = "The destination mission's workflow is switched off, so the session "
                                     + "moved with no workflow seat and is governed by no conduct.";
                        }
                        else
                        {
                            seatNote = "The destination mission has no workflow run of its own, so the session "
                                     + "moved with no workflow seat.";
                        }
                    }
                }
                else
                {
                    FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/mission: session {sid} sits on run " +
                                  $"{currentRunId} which is not its mission's run - the seat is the caller's own and is PRESERVED");
                    seatNote = "The session's workflow seat was left alone: it sits on a run that is not this "
                             + "mission's, so the seat was never the mission's to move.";
                }

                FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/mission: mission={payload.MissionId?.ToString() ?? "(detach)"} " +
                              $"moveSeat={payload.MoveSeat} run={payload.WorkflowRunId?.ToString() ?? "(none)"} director={director.DirectorId}");
                // Tunnel-only. The Ok stream body is the updated SessionDto JSON; a null or non-Ok result collapses to 502.
                var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "attach-mission", sid, payload, ct, machineName: director.MachineName);
                var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
                if (body is null)
                    return TunnelFailure(streamResult);

                // THE PARTICIPANT LEDGER, after the session write has SUCCEEDED and never before. The ledger is
                // the persisted record of which sessions a run governed; updating it first would book a move
                // that might not happen. Leaving is recorded (LeftUtc), not erased - that the session WAS in
                // the run is true and stays true.
                //
                // A ledger write that fails is reported LOUDLY and does not fail the call: the mission and the
                // seat - the things a human reads and the agent is governed by - are already correct, and
                // answering 502 here would tell the caller nothing moved when in fact it did. The gap it
                // leaves is a stale participant row, which is visible in the run and repairable; claiming the
                // move failed is not.
                if (payload.MoveSeat && workflowRuns is not null)
                {
                    try
                    {
                        if (previousRunId is Guid leaving && leaving != payload.WorkflowRunId)
                            workflowRuns.Patch(leaving, new PatchWorkflowRunRequest
                            {
                                LeaveSessionIds = new List<string> { sid },
                            });

                        if (payload.WorkflowRunId is Guid joining && joining != previousRunId)
                            workflowRuns.Patch(joining, new PatchWorkflowRunRequest
                            {
                                AddParticipants = new List<WorkflowRunParticipantDto>
                                {
                                    new()
                                    {
                                        SessionId = sid,
                                        AgentKind = session.Agent ?? "",
                                        Role = session.ExplicitRole ?? "",
                                        Machine = director.MachineName ?? "",
                                    },
                                },
                            });
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/mission: the session moved, but the " +
                                      $"workflow-run participant ledger was NOT updated: {ex.Message}");
                        seatNote = "The mission and the workflow seat both moved, but this run's participant "
                                 + "list could not be updated, so it may still show the session as active.";
                    }
                }

                // The seat OUTCOME travels with the result, because only this process knows it: the seat the
                // session held before the call, and whether it belonged to the mission it left, are facts the
                // caller never had. A caller left to infer "did the seat move?" from the new state alone would
                // have to compare against a value it does not hold, and would report a PRESERVED seat as a
                // moved one.
                return Results.Json(new MissionAttachResultDto
                {
                    Session = JsonSerializer.Deserialize<SessionDto>(body, JsonWeb),
                    SeatMoved = payload.MoveSeat && previousRunId != payload.WorkflowRunId,
                    PreviousWorkflowRunId = previousRunId,
                    SeatNote = seatNote,
                });
            });

            FileLog.Write("[GatewayEndpoints] mapped POST /sessions/{sid}/mission (attach an existing session to a Mission)");
        }

        // Record (or clear) the Gateway-owned snooze for this session - "park / un-park" (hold) (Snooze
        // Length mission, docs/architecture/snooze-length-mission-2026-07-11.md). Snooze IS the hold: the
        // Gateway owns the state AND the expiry timestamp, so holding a session records a snooze-until in the
        // registry - the AUTHORITATIVE result - and the session is GUARANTEED to return to "needs you" on its
        // own even if its Director later dies; un-holding clears it. The Gateway does NOT forward a plain hold
        // to the Director: it mutates the registry FIRST, then triggers a prompt, bounded set-display-state
        // push so the desktop rail reflects the folded hold (the single writer of the Director's raw hold).
        // The registry mutation stands even if that push times out - the periodic sweep reconciles the rail.
        // RAISE A HAND TO YOUR SUPERVISOR (issue #2662). The other half of the supervised rule: a supervised
        // session is quiet toward the owner BY CONSTRUCTION, so this is the channel it uses instead.
        //
        // DELIBERATELY NOT A CHANNEL TO THE OWNER. Nothing here touches the colour, the label or the triage
        // bucket - the fold does not read the flag - so a worker cannot use this to reach him however hard it
        // tries. That is the prior-art lesson written into the roles design: enforce it in the transport, not
        // by asking a worker nicely. A worker with a genuinely owner-shaped problem tells its supervisor,
        // which is a Manager, which is a seat that may surface.
        //
        // THE REASON IS REQUIRED. A hand up with no words tells a supervisor almost nothing and turns this
        // into the "notice me" ping the roles design specifically rejects - messages are for CONTENT.
        app.MapPost("/sessions/{sid}/needs-manager", async (HttpContext ctx, string sid, NeedsManagerRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            if (handRaises is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null) return Results.NotFound();

            var body = req ?? new NeedsManagerRequest();
            if (!body.Raised)
            {
                handRaises.Clear(reqTenant.Value, sid);
                FileLog.Write($"[GatewayEndpoints] needs-manager CLEARED sid={sid}");
                return Results.Ok(new { sessionId = sid, raised = false });
            }

            var reason = (body.Reason ?? "").Trim();
            if (reason.Length == 0)
            {
                return Results.BadRequest(new
                {
                    error = "Say what you need. A raised hand with no words is a 'notice me' ping, and your "
                          + "supervisor cannot act on it without opening you - which is the work this is "
                          + "supposed to save."
                });
            }

            var raise = handRaises.Raise(reqTenant.Value, sid, reason);
            FileLog.Write($"[GatewayEndpoints] needs-manager RAISED sid={sid}: {reason}");
            return Results.Ok(new { sessionId = sid, raised = true, reason = raise.Reason, raisedAt = raise.RaisedAtUtc });
        }).RequireAuthorization();

        app.MapPost("/sessions/{sid}/hold", async (HttpContext ctx, string sid, HoldRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            var holdReq = req ?? new HoldRequest();
            // Issue #1500: an explicit per-call snooze length. Validate it BEFORE recording the hold, so a
            // bad value fails loudly (no fallback / no silent clamp) and never parks the session. Null = use
            // the per-user default. Only the Gateway reads SnoozeMinutes; the hold is recorded in the Gateway
            // registry and reflected to the Director through the display-state channel, not a plain hold, so
            // this stays a Gateway-only capability.
            if (holdReq.OnHold && holdReq.SnoozeMinutes is int requested
                && !Core.Configuration.SnoozeDefaultConfig.IsValid(requested))
            {
                return Results.BadRequest(new
                {
                    error = $"snoozeMinutes must be a whole number of minutes between "
                            + $"{Core.Configuration.SnoozeDefaultConfig.MinMinutes} and "
                            + $"{Core.Configuration.SnoozeDefaultConfig.MaxMinutes}"
                });
            }
            if (snoozeRegistry is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            // A DEAD SESSION CANNOT BE SNOOZED (issue #824).
            //
            // Every edge that LIFTS a hold is driven by an activity push (SnoozeLandingObserver.Observe), and
            // an exited session never pushes again. So a hold recorded AFTER the exit takes the not-working
            // branch below, lands Held, and nothing in the system ever clears it: parked Snoozed forever.
            // Worse, the fold reads OnHold BEFORE the base activity colour, so the row renders grey "Snoozed"
            // over what should be grey "Exited" - or over DEEP RED "Crashed", hiding the one state that most
            // needs the owner's eyes for the whole snooze length.
            //
            // This is the same rule the exit edge already applies in the other direction ("EXITED - drop the
            // hold entirely... a dead session must never hide behind a Snoozed label"), stated once more at
            // the entry point, because dropping on exit cannot help a hold that arrives after it.
            //
            // Only the PARK direction is refused. Clearing stays allowed unconditionally: un-holding a dead
            // session is the repair, and refusing it would strand any hold that was already recorded.
            if (holdReq.OnHold
                && string.Equals(session.ActivityState?.Trim(), nameof(Core.Sessions.ActivityState.Exited),
                    StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[GatewayEndpoints] POST /sessions/{sid}/hold REFUSED - the session has exited; "
                              + "a hold recorded now has no edge that could ever lift it");
                return Results.Json(new
                {
                    error = "This session has exited, so it cannot be snoozed. There is no turn left to come "
                            + "back to, and a snooze would hide how it ended."
                }, statusCode: StatusCodes.Status409Conflict);
            }

            // THE GATEWAY DECIDES, HERE, AND NOWHERE ELSE.
            //
            // This used to forward the hold to the Director, read HoldResponse.Pending back, and record
            // whatever the DIRECTOR had decided. That is the whole defect in one paragraph: the ruling
            // ("is it working, so should this defer?") was made on a Director, the clock was kept here,
            // and the two drifted - defects 12, 20, 21, 22, and every hold that died within minutes on
            // 15 July 2026.
            //
            // The activity is already in hand: LocateSessionAsync returned the session, and its
            // ActivityState is the one fact the Director reports and the Gateway rules on.
            var decided = HoldStates.None;
            if (holdReq.OnHold)
            {
                // Issue #1500: honour a per-call snooze length when the caller passed one (already validated
                // above); otherwise the per-user default, read now so a Settings change applies to the next
                // snooze. Issue #2017: that default is now PER TENANT - resolved for THIS request's tenant
                // (never the global config) when the resolver is wired. On hosted an unresolved tenant fails
                // closed (403), never Local; self-host resolves to Local. Without the resolver (older callers)
                // it stays the process-global read, byte-identical to before.
                int minutes;
                if (holdReq.SnoozeMinutes is int perCall)
                {
                    minutes = perCall;
                }
                else if (tenantSettings is not null)
                {
                    var holdTenant = ResolveReadTenant(ctx, tenantBoundary);
                    if (holdTenant is null)
                        return Results.Json(new { error = "a tenant could not be resolved for this request" },
                            statusCode: StatusCodes.Status403Forbidden);
                    minutes = tenantSettings.SnoozeDefaultMinutes(holdTenant.Value);
                }
                else
                {
                    minutes = Core.Configuration.SnoozeDefaultConfig.Get();
                }

                // Working -> DEFER. THE RULING (owner, 14 July 2026): the clock starts when the work ENDS,
                // so a deferral records its LENGTH and no deadline, and SnoozeLandingObserver starts the
                // clock when the Director reports the work has stopped. Arming a clock at request time is
                // what made an agent-requested snooze permanent.
                //
                // "Working" here means BOTH Working AND Starting - the same set Session.IsWorking uses and the
                // same set SnoozeLandingObserver's working edge deletes an armed snooze on. If this armed a
                // Starting session instead of deferring it, the very next Starting push would delete the
                // just-created snooze through that edge. The defer decision and the working edge must agree on
                // what "working" is, or a snooze set on a Starting session cannot survive.
                var activityNow = session.ActivityState?.Trim();
                var working = string.Equals(activityNow, nameof(Core.Sessions.ActivityState.Working), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(activityNow, nameof(Core.Sessions.ActivityState.Starting), StringComparison.OrdinalIgnoreCase);
                // The owner-turn BASELINE: this Director's own LastOwnerTurnAtUtc as of right now. The
                // hold is superseded when a LATER value arrives from that same Director - one clock,
                // compared against itself. Never against DateTime.UtcNow here: that is the GATEWAY's
                // clock, and comparing it to a Director's stamp makes every hold hostage to clock skew.
                var ownerTurnBaseline = session.LastOwnerTurnAtUtc;

                if (working)
                {
                    snoozeRegistry.SnoozeDeferred(sid, minutes, director.DirectorId, ownerTurnBaseline);
                    decided = HoldStates.DeferredHold;
                }
                else
                {
                    snoozeRegistry.Snooze(sid, DateTime.UtcNow.AddMinutes(minutes), director.DirectorId, ownerTurnBaseline);
                    decided = HoldStates.Held;
                }
            }
            else
            {
                // Manual unsnooze: drop the timer (an alarm turned off).
                snoozeRegistry.Clear(sid, ActivityCauses.ManualRelease);
            }

            // The hold is now a FACT, recorded and persisted in the registry. Round 4 finding 1: the desktop
            // rail is updated through the ONE reliable channel, not a second direct hold command. Trigger a
            // prompt push of the FOLDED hold state (from the registry we just changed) down the same
            // change-gated FleetDisplayStateObserver that serves every other surface - so there is a single
            // writer of the Director's raw hold and no descheduled second writer can leave it stale. Best-
            // effort BY DESIGN: the hold does not depend on it - a slow, unreachable or dead Director cannot
            // prevent the owner from holding a session, and the fold already reports the truth to every other
            // surface from the registry, with the periodic sweep reconciling the desktop.
            // Bounded and cancellable (round 5 finding 1): PushSessionAsync routes through the standard
            // DirectorCommandRouter 30s chokepoint carrying THIS request's token, so a connected-but-
            // unresponsive Director cannot hang the Snooze / Unsnooze click. On timeout or an unreachable
            // Director this still returns SUCCESS below - the registry mutation is the authoritative result
            // and the periodic sweep reconciles the desktop.
            if (fleetDisplayState is not null)
                await fleetDisplayState.PushSessionAsync(sid, ct);

            return Results.Json(new HoldResponse
            {
                OnHold = HoldStates.IsHeld(decided),
                Pending = decided == HoldStates.DeferredHold,
            });
        });

        // Mark / clear a session as transcribing a dictated utterance. Unlike hold this is a purely
        // Gateway-owned transient flag - it is NOT forwarded to the Director; it only feeds the
        // orange "Transcribing..." roster color. The mobile Speak flow calls { transcribing: true }
        // the instant the user hits Send (releasing the screen) and { transcribing: false } once the
        // background upload/transcribe/submit finishes or fails. A literal route so it wins over the
        // /sessions/{sid}/{**rest} catch-all Director proxy. Verified the session exists so a stale id
        // cannot pin a phantom mark.
        app.MapPost("/sessions/{sid}/transcribing", async (HttpContext ctx, string sid, TranscribingRequest req) =>
        {
            if (transcribingSessions is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            // The transcribing mark is keyed by (tenant, sid) (issue #1884, Gap B): resolve the caller's
            // tenant from the authenticated device key and refuse (403) when none resolves on hosted, so a
            // request can only ever set or clear ITS OWN account's mark - never paint or clear another
            // account's session by supplying that account's session id. Self-host resolves Local, unchanged.
            if (ResolveReadTenant(ctx, tenantBoundary) is not { } reqTenant)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            var transcribing = req?.Transcribing ?? false;
            if (transcribing)
                transcribingSessions.Begin(reqTenant, sid);
            else
                transcribingSessions.End(reqTenant, sid);
            return Results.Json(new { transcribing });
        });

        app.MapPatch("/sessions/{sid}", async (HttpContext ctx, string sid, SessionUpdateRequest req) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "request body is required" });

            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            FileLog.Write($"[GatewayEndpoints] PATCH /sessions/{sid}: name=\"{req.Name}\", director={director.DirectorId}");

            // Post-cut: tunnel-only. A null result means the Director is not connected -> 502.
            SessionDto? body;
            string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "patch", sid, req, CancellationToken.None, machineName: director.MachineName);
            if (streamResult is null)
            {
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (body is null)
                return Results.Problem(err ?? "patch failed", statusCode: StatusCodes.Status502BadGateway);

            body.DirectorId = director.DirectorId;
            return Results.Json(body);
        });

        app.MapGet("/sessions/{sid}/buffer", async (HttpContext ctx, string sid, int? lines, bool? raw, long? since, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            if (director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            // Post-cut: tunnel-only. The query params ride in a BufferRequest payload the Director's buffer
            // verb reads. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "buffer", sid,
                new BufferRequest { Lines = lines, Raw = raw == true, Since = since }, ct,
                machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Deliver a prompt down the tunnel and, when asked, wait for the session to go idle and return what
        // it printed. Extracted from POST /sessions/{sid}/prompt by the Remove-the-network-port mission's
        // phase 2 so the new POST /sessions/{sid}/message shares ONE delivery path with it: a message is a
        // framed prompt, and two copies of "send it and wait" would be two behaviours to keep equal. The
        // caller has already located the session and decided what the text is.
        // `outcome`, when given, is told exactly once whether the Director ACCEPTED the prompt - true only on a
        // 200 whose body says Accepted; false on a dropped tunnel, a Director failure, or a Director refusal.
        // The spoken-claim reservation is committed or released on that answer (finding F-07).
        async Task<IResult> DeliverPromptAsync(DirectorDto director, SessionDto session, string sid, PromptRequest req, Action<bool>? outcome = null)
        {
            // Post-cut: tunnel-only. A null result means the Director is not connected -> 502. The WaitForIdle
            // poll below is unchanged - it observes the session regardless of how the prompt was delivered.
            bool ok; PromptResponse? body; string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "prompt", sid, req, CancellationToken.None, machineName: director.MachineName);
            if (streamResult is null)
            {
                ok = false;
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                ok = streamResult.Ok;
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<PromptResponse>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (!ok || body is null)
            {
                outcome?.Invoke(false);
                return Results.Json(new PromptResponse
                {
                    Accepted = false,
                    Error = err,
                    ActivityState = session.ActivityState,
                }, statusCode: StatusCodes.Status502BadGateway);
            }
            outcome?.Invoke(body.Accepted);

            if (!req.WaitForIdle)
                return Results.Json(body);

            var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
            string finalState = body.ActivityState;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(750);
                // The idle poll rides the tunnel too (snapshot verb). Tunnel-only: there is no HTTP arm.
                var cur = await SnapshotTunnelFirstAsync(sendCommand, director, sid, CancellationToken.None);
                if (cur is null) { finalState = "Exited"; break; }
                finalState = cur.ActivityState;
                if (finalState is "Idle" or "WaitingForInput" or "Exited" or "Failed") break;
            }

            // Fetch new output since prompt was sent. Gateway Cleanup Phase 2: buffer verb, tunnel-first.
            string output = "";
            var buf = await BufferTunnelFirstAsync(sendCommand, director, sid, 500, body.BufferCursor, CancellationToken.None);
            if (buf is not null) output = buf.Text;

            body.WaitStatus = finalState switch
            {
                "Idle" or "WaitingForInput" => "idle",
                "Exited" or "Failed" => "failed",
                _ => "timeout",
            };
            body.Output = output;
            body.ActivityState = finalState;
            return Results.Json(body);
        }

        // Resolve the SENDER of an agent-to-agent message: the session whose key authenticated this request,
        // and its roster row, from which the display name and machine in the frame are read.
        //
        // THE SENDER IS NEVER TAKEN FROM THE REQUEST BODY. The Director's /fleet/send read a fromSessionId
        // out of the body and looked the name up locally, which was safe only because the body arrived over
        // loopback from a process on the same machine. This route is reachable by anything holding a session
        // key, so a body-supplied sender would let any agent send a message wearing another agent's name.
        // The authenticated identity cannot be chosen by the caller, so it is the only honest source.
        async Task<(Pairing.SessionCredentialIdentity? Identity, SessionDto? Row, DirectorDto? Owner)> ResolveSenderAsync(HttpContext ctx)
        {
            var identity = AuthMiddleware.CallingSession(ctx);
            if (identity is null) return (null, null, null);
            var (owner, row) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry,
                identity.SessionId.ToString(), pushedSessions, streamStaleResolved, owners);
            return (identity, row, owner);
        }

        // The frame the recipient sees, built from the sender's OWN roster row. A sender the roster cannot
        // produce is framed by its id alone rather than refused: the message is still worth delivering and
        // "which session" is the part that matters most.
        string FrameFromSender(Pairing.SessionCredentialIdentity identity, SessionDto? row, DirectorDto? owner, string text, bool includeReplyHint)
        {
            var name = row is null ? null : (string.IsNullOrWhiteSpace(row.Name) ? null : row.Name);
            var machine = row is not null && !string.IsNullOrWhiteSpace(row.MachineName)
                ? row.MachineName
                : (owner?.MachineName ?? "");
            return FleetMessaging.BuildFramedMessage(identity.SessionId.ToString(), name, machine, text, includeReplyHint);
        }

        // Every live session in one account, paired with the Director that owns it. This is the candidate set
        // POST /fleet/broadcast filters down to the sender's team, and it is deliberately built from the same
        // two sources the roster endpoint uses - the tenant's own Director partition and the pushed snapshot
        // cache - so "my team" is computed over exactly the fleet the caller can see listed.
        //
        // Exited rows are dropped: they are kept on the roster so a person can see that work stopped, but
        // sending a message to a session that has ended is not a delivery, it is a failure row nobody asked
        // for. A Director whose tunnel is down contributes its last-known rows, and the delivery attempt to
        // one of those is what reports the machine as unreachable - which is the honest answer.
        IEnumerable<(DirectorDto Director, SessionDto Session)> EnumerateTenantSessions(TenantId tenant)
        {
            if (pushedSessions is null) yield break;
            foreach (var d in registry.ListDirectors(tenant))
            {
                var known = pushedSessions.GetLastKnown(tenant, d.DirectorId);
                foreach (var s in known.Sessions)
                {
                    if (string.IsNullOrEmpty(s.SessionId)) continue;
                    if (string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase)) continue;
                    yield return (d, s);
                }
            }
        }

        // POST /sessions/{sid}/message - one agent sends one message to one session, ANYWHERE in the account
        // (Remove-the-network-port mission, phase 2).
        //
        // WHY THIS IS NOT JUST /prompt. A prompt is raw text typed into a session, exactly what a person at
        // the keyboard would type. A MESSAGE is from somebody: it carries a sender header and, for a one-way
        // message, the command to reply with, and it passes the fleet-message steward. Until now the Director
        // added all of that on the way past, because the command line reached the fleet through its own
        // Director's loopback port. That port is being removed, so the framing and the steward move to the end
        // that is still in the path. Doing it in the client instead was never an option - a sender name, a
        // machine and a steward verdict are RULINGS, and this repository's standing rule is that the Gateway
        // owns every ruling and the client only renders.
        //
        // `waitForIdle` is what makes this route serve `message ask` as well as `message send`: ask is the
        // same framed delivery, minus the reply hint (the asker is already waiting and reads the answer from
        // the target's own output, so telling the recipient to send a SEPARATE reply makes it answer into a
        // channel nobody is listening on), plus a wait for the session to finish.
        app.MapPost("/sessions/{sid}/message", async (HttpContext ctx, string sid, FleetMessageRequest req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            var (identity, senderRow, senderOwner) = await ResolveSenderAsync(ctx);
            if (identity is null)
                return Results.Json(new { error = "this route identifies the sender from the session key that authenticated the request; the caller presented no session key" },
                    statusCode: StatusCodes.Status403Forbidden);

            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            // The fleet-message steward: dedupe plus a per-sender rate limit on OUTGOING messages. Never
            // silent - a drop is logged AND returned to the sender. Not wired => allow, byte-identical.
            var from = identity.SessionId.ToString();
            if (messageSteward is not null)
            {
                var decision = messageSteward.CheckMessage(from, sid, req.Text);
                if (!decision.Allowed)
                {
                    FileLog.Write($"[GatewayEndpoints] POST message steward {decision.Outcome}: from={FleetMessaging.ShortId(from)} to={FleetMessaging.ShortId(sid)} - {decision.Reason}");
                    return Results.Json(new PromptResponse { Accepted = false, Error = decision.Reason },
                        statusCode: decision.Outcome == Core.Fleet.StewardOutcome.DuplicateSuppressed
                            ? StatusCodes.Status200OK
                            : StatusCodes.Status429TooManyRequests);
                }
            }

            var framed = FrameFromSender(identity, senderRow, senderOwner, req.Text, includeReplyHint: !req.WaitForIdle);
            FileLog.Write($"[GatewayEndpoints] POST message: from={FleetMessaging.ShortId(from)} to={FleetMessaging.ShortId(sid)}, director={director.DirectorId}, waitForIdle={req.WaitForIdle}");

            return await DeliverPromptAsync(director, session, sid, new PromptRequest
            {
                Text = framed,
                AppendEnter = true,
                WaitForIdle = req.WaitForIdle,
                TimeoutMs = req.TimeoutMs,
                // ONE AGENT PROMPTING ANOTHER, SAID OUT LOUD. This route is reached only by a caller that
                // authenticated with a SESSION key, so the sender is a session by construction and there is
                // nothing to infer. Without the marker the Director records the turn as an ordinary
                // UserInput with no origin, and it is then left out of the person's figures only because no
                // surface resolved for it - the right answer by the wrong road - while the agent-driven
                // lane, which exists precisely to count these, never sees it at all. Over the owner's week
                // of 2026-W35 that was 292 of 296 fleet messages: absent from both numbers. See ruling R12
                // of the "Clean up Your Throttle" mission (2026-09-05).
                AgentDriven = true,
            });
        });

        // POST /fleet/broadcast - "message send all": one message to the SENDER'S OWN TEAM, or (with
        // everyone + a reason + a human grant) the whole account (Remove-the-network-port mission, phase 2).
        //
        // WHY THIS EXISTS BESIDE /fanout. /fanout takes an explicit list of session ids and rules on whether
        // the sender may reach them. Somebody still has to WORK OUT the list, and that is the sender's team -
        // which is a ruling made from the roster, off the same BroadcastScope this Gateway already enforces.
        // The Director used to compute it and hand /fanout the finished list; with the Director out of the
        // path the computation belongs here, next to the rule it has to agree with. Putting it in the command
        // line would put the definition of "my team" in Python, one process removed from the Gateway that
        // decides whether the answer was allowed - two definitions of one thing, drifting by default.
        //
        // It then delegates to the SAME fanout path, so the scope decision, the grant check, the rate limit
        // and the delivery are all evaluated exactly once, in one place.
        app.MapPost("/fleet/broadcast", async (HttpContext ctx, FleetTeamBroadcastRequest req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            var (identity, senderRow, senderOwner) = await ResolveSenderAsync(ctx);
            if (identity is null)
                return Results.Json(new { error = "this route identifies the sender from the session key that authenticated the request; the caller presented no session key" },
                    statusCode: StatusCodes.Status403Forbidden);

            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (senderRow is null)
                return Results.Json(new FanoutResponse
                {
                    Denied = true,
                    DeniedReason = "The broadcasting session is not on the fleet roster, so its team cannot be resolved.",
                    StartedAt = DateTime.UtcNow,
                    FinishedAt = DateTime.UtcNow,
                }, statusCode: StatusCodes.Status404NotFound);

            // The candidate set is this ACCOUNT's roster, read from the Gateway's own tenant-scoped view.
            var from = identity.SessionId.ToString();
            // The owner is non-null whenever the row is (they come from one lookup), but the scope is built
            // from a real DirectorDto either way: the machine name falls back to the Director's when the
            // session record does not carry one, and a missing machine would silently widen "same machine".
            var senderScope = BuildBroadcastScope(senderOwner ?? new DirectorDto { DirectorId = identity.DirectorId }, senderRow);
            var targetIds = new List<string>();
            foreach (var (d, s) in EnumerateTenantSessions(reqTenant.Value))
            {
                if (string.Equals(s.SessionId, from, StringComparison.OrdinalIgnoreCase)) continue;
                if (req.Everyone || senderScope.Includes(BuildBroadcastScope(d, s)))
                    targetIds.Add(s.SessionId);
            }

            if (targetIds.Count == 0)
                return Results.Json(new FanoutResponse
                {
                    Results = new List<FanoutResult>(),
                    StartedAt = DateTime.UtcNow,
                    FinishedAt = DateTime.UtcNow,
                    Warning = req.Everyone ? "No other sessions in the fleet." : "No other sessions on your team.",
                });

            FileLog.Write($"[GatewayEndpoints] POST fleet/broadcast: from={FleetMessaging.ShortId(from)}, everyone={req.Everyone}, targets={targetIds.Count}");

            // A plain team broadcast passes no reason and no grant - every target is in scope by construction.
            // `everyone` carries the reason and the human grant the policy requires to reach past the team.
            return await RunFanoutAsync(ctx, new FanoutRequest
            {
                SessionIds = targetIds,
                Text = FrameFromSender(identity, senderRow, senderOwner, req.Text, includeReplyHint: true),
                FromSessionId = from,
                // DELIVER AND RETURN - do NOT wait for every recipient to finish. FanoutRequest defaults
                // WaitForIdle to true, which is right for a caller collecting answers and badly wrong here:
                // "message send all" is a notification, and waiting would hold the sender for as long as the
                // slowest recipient takes to go idle - up to the per-session timeout, on a command whose
                // predecessor returned as soon as the message was accepted.
                WaitForIdle = false,
                Reason = req.Everyone ? req.Reason : null,
                GrantId = req.Everyone ? req.GrantId : null,
            });
        });

        app.MapPost("/sessions/{sid}/prompt", async (string sid, PromptRequest req, HttpContext httpCtx) =>
        {
            if (req is null || string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            // DevThrottle Stats: stamp the surface from the VERIFIED device key (stashed by AuthMiddleware),
            // overwriting any client-supplied value so it cannot be forged. Rides both the SignalR command
            // and the HTTP fallback below to the Director's choke-point tally. This IS the operator front
            // door, so it is ALWAYS a real turn: when no device key resolved (a shared-machine-token call) we
            // stamp "unknown" - NOT null - so the Director still counts it, into the honest "unknown" surface
            // bucket the dashboard shows (decision 9: surface excluded volume, never silently drop it).
            // Machine-to-machine traffic (fanout/broadcast) never reaches this handler and never sets Surface,
            // so it stays null and is correctly excluded.
            req.Surface = (httpCtx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null) ?? "unknown";
            // THE TWO ATTRIBUTION MARKERS ARE THE GATEWAY'S TO SET, NEVER THE BODY'S (inspection findings I2-02
            // and I2-03 of the "Clean up Your Throttle" mission, 2026-09-05). The Director believes both fields
            // of this DTO unconditionally: AgentDriven relabels the turn as another agent's and takes it out of
            // the person's figures; a nonblank DeliveryUploadId makes it a voice delivery. Before this, both were
            // forwarded exactly as the caller sent them, so a device-authenticated body could count its own
            // typed prompt as agent traffic, or as speech under a made-up or replayed id.
            //
            // Who drove it is decided from the AUTHENTICATED credential, the same way the fanout decides: a
            // session key is one agent prompting another; anything else is the person. Whether it was spoken is
            // decided by spending the Gateway's own claim for the utterance id - known, this tenant's, unspent,
            // young, and the submitted words are the transcript - and an agent never spoke. Anything that does
            // not clear that bar is a typed turn (ruling R10: an edited, mixed, or replayed dictation is typed).
            // The text is still delivered either way; nothing here refuses the prompt.
            var callingSessionForAttribution = AuthMiddleware.CallingSession(httpCtx);
            req.AgentDriven = callingSessionForAttribution is not null;
            var claimedUploadId = req.DeliveryUploadId;
            req.DeliveryUploadId = null;
            // RESERVED HERE, COMMITTED OR RELEASED BELOW (final inspection finding F-07). The claim used to be spent
            // at this line, before the session was located or the menu guard ran, so a prompt that never entered a
            // session burned the only proof and the person's retry of the same words was filed as typed. Now the
            // claim is held until the Director answers: accepted commits it, anything else releases it.
            Voice.SpokenClaimRegistry.Reservation? spokenReservation = null;
            if (!string.IsNullOrWhiteSpace(claimedUploadId) && callingSessionForAttribution is null)
            {
                var claimTenant = ResolveReadTenant(httpCtx, tenantBoundary);
                var refusal = Voice.SpokenClaimRegistry.Refusal.None;
                if (spokenClaims is not null && claimTenant is { } ct
                    && spokenClaims.TryReserve(ct, claimedUploadId, req.Text, out refusal, out var reserved))
                {
                    req.DeliveryUploadId = claimedUploadId;
                    spokenReservation = reserved;
                    FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} spoken claim reserved (upload={claimedUploadId})");
                }
                else
                {
                    FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} spoken claim REFUSED, delivered as typed " +
                                  $"(upload={claimedUploadId}, reason={(spokenClaims is null ? "no-registry" : claimTenant is null ? "no-tenant" : refusal.ToString())})");
                }
            }
            else if (!string.IsNullOrWhiteSpace(claimedUploadId))
            {
                FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} spoken claim ignored - the caller is a session, and a session did not speak");
            }

            // WHICH CHARACTERS WERE SPOKEN (source logging, 2026-09-05). A browser composer tracks the ranges its
            // dictation occupies as the person types around them and CLAIMS them here. A claim is not a fact: each
            // one is verified against the registry - the characters it names must BE the transcript that id
            // registered, in this tenant, unexpired - and only verified spans are recorded. A claim that fails is
            // dropped and logged, so a hostile body can mark nothing as spoken that it cannot already reproduce
            // verbatim. Verification is read-only and spends nothing: whether the TURN counts as spoken is still
            // decided by the reservation above and by nothing else.
            var claimedSpans = req.SpokenSpans ?? new List<SpokenSpanClaimDto>();
            req.SpokenSpans = null;
            var verifiedSpans = new List<SpokenSpanDto>();
            var verifiedTranscriptIds = new HashSet<string>(StringComparer.Ordinal);
            if (claimedSpans.Count > 0 && callingSessionForAttribution is null && spokenClaims is not null
                && ResolveReadTenant(httpCtx, tenantBoundary) is { } spanTenant)
            {
                var text = req.Text ?? "";
                foreach (var claim in claimedSpans.OrderBy(c => c.Start))
                {
                    if (claim.Start < 0 || claim.Length <= 0 || (long)claim.Start + claim.Length > text.Length)
                    {
                        FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} spoken span claim REFUSED - {claim.Start}+{claim.Length} lies outside {text.Length} characters");
                        continue;
                    }
                    if (!spokenClaims.Matches(spanTenant, claim.TranscriptId, text.Substring(claim.Start, claim.Length)))
                    {
                        FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} spoken span claim REFUSED - the characters {claim.Start}+{claim.Length} are not the transcript of upload {claim.TranscriptId}");
                        continue;
                    }
                    verifiedSpans.Add(new SpokenSpanDto { Start = claim.Start, Length = claim.Length });
                    if (!string.IsNullOrWhiteSpace(claim.TranscriptId)) verifiedTranscriptIds.Add(claim.TranscriptId!.Trim());
                }
                FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} spoken span claims: {verifiedSpans.Count} of {claimedSpans.Count} verified");
            }
            else if (claimedSpans.Count > 0)
            {
                FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} {claimedSpans.Count} spoken span claim(s) ignored - the caller is a session, or no registry or tenant resolved");
            }

            // WHAT THIS DOOR KNEW AT ENTRY (owner's ruling, 2026-09-05: source logging), onto the wire for the
            // Director to record on the ledger row: the route (a session calling is a fleet message; anyone else
            // is the prompt route), the credential kind this gate verified, and the spoken characters - the
            // verified span claims, or, when a whole-text claim was reserved above and the client named no spans,
            // the whole text (the registry reserves a claim only for text that IS the transcript). Nothing here
            // is inferred later.
            if (verifiedSpans.Count == 0 && spokenReservation is not null && !string.IsNullOrEmpty(req.Text))
            {
                verifiedSpans.Add(new SpokenSpanDto { Start = 0, Length = req.Text.Length });
                if (!string.IsNullOrWhiteSpace(claimedUploadId)) verifiedTranscriptIds.Add(claimedUploadId!.Trim());
            }
            req.Provenance = new SubmissionProvenanceDto
            {
                Route = callingSessionForAttribution is not null ? Core.Sessions.SubmissionRoutes.FleetMessage : Core.Sessions.SubmissionRoutes.GatewayPrompt,
                IdentityKind = AuthMiddleware.IdentityKind(httpCtx),
                // One id names the transcript this turn came from; several means it was composed of more than
                // one, and no single id is the truth - the spans carry that detail instead of a guess.
                TranscriptId = verifiedTranscriptIds.Count == 1 ? verifiedTranscriptIds.First() : null,
                SpokenSpans = verifiedSpans,
            };

            var accepted = false;
            try
            {
                return await PromptAfterAttributionAsync();
            }
            finally
            {
                if (spokenReservation is { } held && spokenClaims is not null)
                {
                    if (accepted) spokenClaims.Commit(held);
                    else spokenClaims.Release(held);
                }
            }

            async Task<IResult> PromptAfterAttributionAsync()
            {
            var (director, session) = await LocateSessionForRequestAsync(httpCtx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(httpCtx, tenantBoundary, pushedSessions, sid);

            FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid}, director={director.DirectorId}, waitForIdle={req.WaitForIdle}");

            // THE WINGMAN MENU GUARD (issue #2193), opt-in per request and off for every existing caller.
            // A voice reply asks for it, so the live screen is read HERE - one hop before the send - rather
            // than by the client, which would leave a window for the screen to change between the check and
            // the send. On a menu we type nothing and press nothing: the trailing Enter this prompt carries
            // would otherwise confirm whatever option the picker had highlighted.
            //
            // Only a MODEL-CONFIRMED menu refuses (issue devthrottle_internal#1195): the pure classifier is
            // the tripwire, and when it fires the verdict comes from the wingman brain - served from the
            // per-screen verdict cache when the screen has not changed since the turn was narrated, one
            // small model call when it has. The regex alone convicted a finished summary of being a menu
            // (session 115) and locked its owner out of voice; it never convicts on its own again. A screen
            // the classifier cannot read is forwarded exactly as before the guard existed - blocking on
            // uncertainty would silently break ordinary voice replies.
            if (req.MenuGuard)
            {
                // The tenant is resolved BEFORE the check: the model confirmation and the spoken refusal
                // both need it, and a menu-guard request that cannot resolve one has no honest answer.
                var guardTenant = ResolveReadTenant(httpCtx, tenantBoundary);
                if (guardTenant is null)
                    return Results.Json(new { error = "a tenant could not be resolved for this request" },
                        statusCode: StatusCodes.Status403Forbidden);
                var guardRoute = new SessionVerbClient(director, sendCommand);
                if (await Wingman.WaitingScreenReader.ConfirmedMenuAsync(guardRoute, sid, guardTenant.Value, wingmanTranslator, CancellationToken.None))
                {
                    // The refusal is SPOKEN - a voice reply asked for this guard - so it is said in the
                    // account's language (issue #1009). The resolver is required rather than defaulted: a
                    // caller that reaches this branch without one would speak English at somebody who chose
                    // French. The on-screen Error line stays English like every other label.
                    if (tenantSettings is null)
                        throw new InvalidOperationException(
                            "The menu guard speaks a refusal, so GatewayEndpoints.Map must be given a "
                            + "TenantSettingsResolver to read the account's spoken language from.");
                    FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid} REFUSED - a menu owns the live screen (menu guard); nothing typed, no Enter pressed");
                    return Results.Json(new PromptResponse
                    {
                        Accepted = false,
                        BlockedByMenu = true,
                        BlockedSpoken = Speech.SpokenPhrases.WaitingScreenMenu.In(tenantSettings.SpokenLanguage(guardTenant.Value)),
                        // The language those words are in (issue #1031). The client speaks this one through the
                        // BROWSER's own synthesis and cannot build an utterance without a language; without this
                        // field a correct French refusal is read out in the device's default English voice.
                        BlockedSpokenLanguage = tenantSettings.SpokenLanguage(guardTenant.Value).Code,
                        Error = Wingman.WaitingScreenReader.MenuMessage,
                        ActivityState = session.ActivityState,
                    });
                }
            }

            return await DeliverPromptAsync(director, session, sid, req, wasAccepted => accepted = wasAccepted);
            }
        });

        app.MapPost("/sessions/{sid}/interrupt", async (HttpContext ctx, string sid) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "interrupt", sid, null, CancellationToken.None, machineName: director.MachineName);
            var ok = streamResult is not null && streamResult.Ok;
            return ok
                ? Results.Json(new { accepted = true })
                : TunnelFailure(streamResult);
        });

        app.MapPost("/sessions/{sid}/escape", async (HttpContext ctx, string sid) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "escape", sid, null, CancellationToken.None, machineName: director.MachineName);
            var ok = streamResult is not null && streamResult.Ok;
            return ok
                ? Results.Json(new { accepted = true })
                : TunnelFailure(streamResult);
        });

        // Phone image upload: the browser POSTs the image to the Gateway (its origin); we
        // forward the bytes to the owning Director, which files it into its screenshots
        // folder (same machine as the session) and returns the saved absolute path.
        app.MapPost("/sessions/{sid}/upload-image", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with an image file field 'file'" });

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "no image uploaded; use form field 'file'" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);

            FileLog.Write($"[GatewayEndpoints] POST upload-image: sid={sid}, director={director.DirectorId}, bytes={ms.Length}");

            var bytes = ms.ToArray();

            // Gateway Cleanup (Phase 2): upload the image DOWN the tunnel in bounded chunks - begin, then a
            // chunk per UploadChunkRawBytes, then complete - so a whole photo never rides as one large unary
            // message that would monopolize the shared tunnel (Architect ruling 2). A null begin means no
            // A null begin means the Director is not connected and collapses to 502 below. A
            // non-null-but-failed step is authoritative and collapses to 502 (a retryable upload failure).
            var uploadId = Guid.NewGuid().ToString("N");
            var begin = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-begin", sid,
                new UploadImageBeginRequest { UploadId = uploadId, FileName = file.FileName, TotalBytes = bytes.Length }, ctx.RequestAborted,
                machineName: director.MachineName);
            if (begin is not null)
            {
                // Issue #2190: carry the Director's OWN status out, instead of flattening every rejection to
                // 502. Uploading a file type we do not accept is the caller's request being wrong (400 with
                // the accepted list), not a broken gateway - and a 5xx made the client retry something that
                // could never succeed. A genuinely dropped tunnel still answers 502, through the same map.
                if (!begin.Ok)
                    return MapDirectorFailure(begin);

                for (var off = 0; off < bytes.Length; off += DirectorStreamLimits.UploadChunkRawBytes)
                {
                    var len = Math.Min(DirectorStreamLimits.UploadChunkRawBytes, bytes.Length - off);
                    var chunk = new UploadImageChunkRequest
                    {
                        UploadId = uploadId,
                        Seq = off / DirectorStreamLimits.UploadChunkRawBytes,
                        BytesBase64 = Convert.ToBase64String(bytes, off, len),
                    };
                    var cr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-chunk", sid, chunk, ctx.RequestAborted, machineName: director.MachineName);
                    if (cr is null || !cr.Ok)
                    {
                        FileLog.Write($"[GatewayEndpoints] upload-image FAILED mid-upload: sid={sid}, uploadId={uploadId}, "
                            + $"seq={chunk.Seq}, reason={(cr is null ? "tunnel dropped" : DirectorCommandRouter.DescribeFailure(cr))}");
                        return cr is null
                            ? Results.Json(new { error = "The connection to the machine running this session dropped part-way through the upload. Try again.", retryable = true },
                                statusCode: StatusCodes.Status502BadGateway)
                            : MapDirectorFailure(cr);
                    }
                }

                var done = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-complete", sid,
                    new UploadImageCompleteRequest { UploadId = uploadId }, ctx.RequestAborted,
                    machineName: director.MachineName);
                if (done is null || !done.Ok || string.IsNullOrEmpty(done.BodyJson))
                {
                    FileLog.Write($"[GatewayEndpoints] upload-image FAILED at complete: sid={sid}, uploadId={uploadId}, "
                        + $"reason={(done is null ? "tunnel dropped" : DirectorCommandRouter.DescribeFailure(done))}");
                    if (done is null)
                        return Results.Json(new { error = "The connection to the machine running this session dropped just before the upload finished. Try again.", retryable = true },
                            statusCode: StatusCodes.Status502BadGateway);
                    // An Ok result with an empty body is a contract breach, not a Director rejection: say so
                    // rather than returning a bodyless status the user cannot act on.
                    return done.Ok
                        ? Results.Json(new { error = "The image was uploaded but the machine running this session did not report where it was saved.", retryable = true },
                            statusCode: StatusCodes.Status502BadGateway)
                        : MapDirectorFailure(done);
                }

                return Results.Content(done.BodyJson, "application/json"); // { path, fileName }
            }

            // Post-cut: tunnel-only. A null begin means the Director is not connected -> 502. Say it in words
            // and mark it retryable (issue #2189), because a Director that just dropped is usually back
            // within a push cycle.
            FileLog.Write($"[GatewayEndpoints] upload-image REFUSED: sid={sid}, director={director.DirectorId} is not connected to the tunnel");
            return Results.Json(new
            {
                error = $"The machine running this session ({director.MachineName}) is not connected right now, so the image could not be delivered. Try again.",
                code = "director_not_connected",
                retryable = true,
            }, statusCode: StatusCodes.Status502BadGateway);
        });

        app.MapGet("/directors/{id}/repos", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (repos-list verb, director-level so SessionId
            // is ""). Tunnel-only: a null return means the Director is not connected, and a non-Ok stream
            // result collapses to 502.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repos-list", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<RepositoryDto>>(sr) ?? new List<RepositoryDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // The complete machine-scoped repository catalog used by mobile session creation. It is served
        // from Gateway storage rather than through the Director tunnel, so a temporarily disconnected
        // Director does not erase search history. The machine comes from the owned Director registration;
        // callers cannot supply a machine name and cross that ownership boundary.
        app.MapGet("/directors/{id}/known-repositories", (HttpContext ctx, string id) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var director, out var error))
                return error;
            if (knownRepositories is null)
                return Results.Json(new { error = "Known repository storage is not available." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var requestTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (requestTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(director.MachineName))
                return Results.Json(new { error = "The Director has not reported a machine name." },
                    statusCode: StatusCodes.Status409Conflict);

            return Results.Json(knownRepositories.ReadForMachine(requestTenant.Value, director.MachineName));
        });

        // Issue #330: pull a registered Director's machine facts (tool inventory with
        // versions + launcher presence) through the existing proxy leg. Pulled on
        // demand rather than pushed in registration/heartbeat: the inventory is large and
        // changes rarely, so riding the 15s heartbeat would bloat the hot path for a fact
        // a consumer reads occasionally.
        app.MapGet("/directors/{id}/facts", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (facts verb, director-level).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "facts", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                var body = DirectorCommandRouter.ReadBody<DirectorFactsDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // Issue #1497: the target Director's configured, enabled agents (one per kind) for the Cockpit New
        // Session dialog's agent picker. Rides the tunnel (agents-list verb, director-level), mirroring the
        // facts/repos-list read legs above; a null result (Director not connected) collapses to 502.
        app.MapGet("/directors/{id}/agents", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "agents-list", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<AgentChoiceDto>>(sr) ?? new List<AgentChoiceDto>());
            }

            return TunnelFailure(null, d.MachineName);
        });

        // --- The automation browsers on one machine (Remove-the-network-port mission, phase 2) ------------
        //
        // DevThrottle's automation browsers are the drivable, signed-in-once Chromium instances an agent
        // attaches to with browser-harness. They were reachable ONLY on the Director's own loopback port,
        // because the only caller was the command line on the same machine. That port is being removed, so
        // these eight legs give the browser verbs the tunnel path every other agent verb already had.
        //
        // THE GATEWAY DOES NOT DRIVE A BROWSER. A browser's debug port is loopback and its profile directory
        // is on one machine's disk, so the Director on that machine is still the only thing that can start,
        // stop or attach to it - these routes carry a command to it and carry the answer back. That is why
        // they are addressed to a DIRECTOR and not to a machine name in a payload: "the browsers on my
        // machine" is answered by the Director that owns the machine, and an agent's session key names its
        // own Director, so it cannot ask about somebody else's.
        //
        // The verb's body is the Director's own folded view, forwarded VERBATIM - the Gateway must not become
        // a second definition of what a browser looks like.
        {
            // Fold the {browserId} path segment into the body, so one payload carries both the id from the
            // path and any fields from the request. Exactly what the queue verbs do with theirs.
            // The payload is handed over as a JsonObject rather than a pre-serialized string: the router
            // serializes whatever it is given, so a string would arrive at the Director double-encoded - a
            // payload that parses into a quoted blob and reads as an empty request.
            static async Task<JsonObject> BrowserPayloadAsync(HttpContext ctx, string? browserId)
            {
                JsonObject obj;
                try
                {
                    using var reader = new StreamReader(ctx.Request.Body);
                    var raw = await reader.ReadToEndAsync(ctx.RequestAborted);
                    obj = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : (JsonNode.Parse(raw)?.AsObject() ?? new JsonObject());
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    obj = new JsonObject();
                }
                if (browserId is not null) obj["id"] = browserId;
                return obj;
            }

            async Task<IResult> BrowserVerbAsync(HttpContext ctx, string directorId, string verb, string? browserId, CancellationToken ct)
            {
                if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, directorId, out var d, out var err)) return err;

                var payload = await BrowserPayloadAsync(ctx, browserId);
                var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, directorId, verb, "",
                    payload, ct, machineName: d.MachineName);
                if (sr is null || !sr.Ok) return DirectorAnswerFailure(sr, d.MachineName);
                return Results.Content(sr.BodyJson ?? "{}", "application/json");
            }

            app.MapGet("/directors/{id}/browsers", (HttpContext ctx, string id, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-list", null, ct));

            app.MapPost("/directors/{id}/browsers", (HttpContext ctx, string id, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-create", null, ct));

            app.MapGet("/directors/{id}/browsers/{browserId}/attach", (HttpContext ctx, string id, string browserId, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-attach", browserId, ct));

            app.MapPost("/directors/{id}/browsers/{browserId}/start", (HttpContext ctx, string id, string browserId, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-start", browserId, ct));

            app.MapPost("/directors/{id}/browsers/{browserId}/stop", (HttpContext ctx, string id, string browserId, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-stop", browserId, ct));

            app.MapPost("/directors/{id}/browsers/{browserId}/signin", (HttpContext ctx, string id, string browserId, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-signin", browserId, ct));

            app.MapPost("/directors/{id}/browsers/{browserId}/rename", (HttpContext ctx, string id, string browserId, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-rename", browserId, ct));

            app.MapDelete("/directors/{id}/browsers/{browserId}", (HttpContext ctx, string id, string browserId, CancellationToken ct) =>
                BrowserVerbAsync(ctx, id, "browsers-delete", browserId, ct));
        }

        // Gateway Cleanup CUT RESTORATION: the Cockpit's Director Settings editor
        // (apps/cockpit/src/fleet/DirectorDetailView.tsx -> client-core getDirectorSettings/putDirectorSettings).
        // The cut dropped the HTTP reverse-proxy leg that used to serve these two and deferred remote config
        // editing to Phase 4, but the CALLER was left pointing here. With nothing mapped, the request fell
        // through to the single-page-app fallback, which answered the Cockpit's GET with the HTML shell at
        // status 200 - and the client only checks res.ok, so it loaded a web page into the settings editor and
        // called it settings. These legs ride the tunnel like every director-level verb above; the settings body
        // is an OPAQUE object the Director owns, so it is forwarded VERBATIM in both directions rather than
        // being modelled here (the Gateway must not become a second definition of the Director's config).
        app.MapGet("/directors/{id}/settings", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "settings-get", "", null, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return DirectorAnswerFailure(sr, d.MachineName);

            // The verb's body IS the config object; forward the exact bytes the Director sent.
            return Results.Content(sr.BodyJson ?? "{}", "application/json");
        });

        app.MapPut("/directors/{id}/settings", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            string raw;
            using (var reader = new StreamReader(ctx.Request.Body))
                raw = await reader.ReadToEndAsync(ct);

            // Parse only far enough to know it is a JSON object - the Director owns what the keys MEAN. A
            // malformed edit is rejected HERE, naming the fault, rather than travelling to the Director to
            // fail there or, worse, being written as garbage.
            JsonNode? patch;
            try
            {
                patch = JsonNode.Parse(raw);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = $"The settings you sent are not valid JSON: {ex.Message}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (patch is not JsonObject)
                return Results.Json(new { error = "request body must be a JSON object" },
                    statusCode: StatusCodes.Status400BadRequest);

            FileLog.Write($"[GatewayEndpoints] PUT /directors/{id}/settings: machine={d.MachineName}");

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "settings-put", "",
                new SettingsPutPayload { Settings = patch }, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return DirectorAnswerFailure(sr, d.MachineName);

            // The merged config the Director actually stored, forwarded verbatim.
            return Results.Content(sr.BodyJson ?? "{}", "application/json");
        });

        app.MapPost("/directors/{id}/sessions", async (HttpContext ctx, string id, NewSessionRequest req) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var ownerErr)) return ownerErr;
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions: repo={req.RepoPath}, agent={req.Agent}");

            // THE MISSION NAME AND THE WORKFLOW SEAT, resolved here exactly as the machine door resolves
            // them (issue #2629). This is the door an unqualified `cc-devthrottle session spawn` uses, and
            // it used to forward the create VERBATIM - so a mission-scoped spawn reached the Director
            // carrying an id and no name, the Director read that as an old caller naming a mission in its
            // own stale local store, and refused a mission that was real, active and listed. The seat was
            // missing too, silently: a session in a mission with none of the conduct the mission pins.
            //
            // Both doors now call the SAME resolver, so neither can drift from the other again.
            var spawnTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (spawnTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            var spawnRoute = $"POST /directors/{id}/sessions";
            if (!SpawnMissionAndSeat.TryResolve(req, spawnTenant.Value, missions, workflowRuns, spawnRoute,
                    out var seatRun, out var resolveError))
                return resolveError!;

            // Issue #1177 (Phase 1): create rides the target Director's stream. Tunnel-only: a null return
            // means the Director is not connected, and a non-Ok stream result (validation/creation failure)
            // collapses to 502 - both surface as the error below.
            SessionDto? body;
            string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "create", "", req, CancellationToken.None);
            if (streamResult is null)
            {
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (body is null)
                return Results.Problem(err ?? "failed", statusCode: StatusCodes.Status502BadGateway);

            // The membership row governance reads, on the same terms as the machine door: recorded only
            // when the Director's reply proves the seat landed, and never turned into an HTTP failure the
            // caller would retry into a second session.
            SpawnMissionAndSeat.RecordParticipant(seatRun, workflowRuns, req, body, d.MachineName ?? "", spawnRoute);

            return Results.Json(body, statusCode: 201);
        });

        app.MapDelete("/directors/{id}/repos", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (repo-delete verb, director-level). The
            // Director core returns { removed } in its body; a non-Ok stream result collapses to 502.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-delete", "", new RepoDeleteRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Content(sr.BodyJson ?? "{\"removed\":false}", "application/json");
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): register a repository explicitly in the recent list. Rides
        // the repo-add verb (director-level). The Director core validates directory-existence and returns
        // { added, repo }; added selects the old route's 201 (newly added) vs 200 (already present). A typed
        // failure preserves 400; a null result (Director not tunnel-connected) is 502.
        app.MapPost("/directors/{id}/repos", async (HttpContext ctx, string id, RepoAddRequest req, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (req is null || string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(new { error = "path is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-add", "", req, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<RepoAddResponse>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: body.Added ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): rename a registered repository (path is the identity). Rides
        // the repo-rename verb (director-level). A path not in the registry is the executor's NotFound -> 404;
        // a null result (Director not tunnel-connected) is 502.
        app.MapPatch("/directors/{id}/repos", async (HttpContext ctx, string id, RepoRenameRequest req, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (req is null || string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(new { error = "path is required" });
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-rename", "", req, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<RepositoryDto>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): the enriched per-repo overview the Repositories page reads.
        // Rides the repos-overview verb (director-level). A null result (Director not tunnel-connected) is 502.
        app.MapGet("/directors/{id}/repos/overview", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repos-overview", "", null, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            return Results.Json(DirectorCommandRouter.ReadBody<List<RepoOverviewDto>>(sr) ?? new List<RepoOverviewDto>());
        });

        app.MapGet("/directors/{id}/coaching/categories", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (coaching-categories verb, director-level).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "coaching-categories", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<CoachingCategoryDto>>(sr) ?? new List<CoachingCategoryDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapGet("/directors/{id}/claude-sessions", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (claude-sessions verb, director-level; no
            // repo filter on this route, so the payload is empty).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "claude-sessions", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<ClaudeSessionDto>>(sr) ?? new List<ClaudeSessionDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapGet("/directors/{id}/handovers", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (handovers-list verb, director-level; this
            // route has no repo filter, so the payload is empty).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handovers-list", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<HandoverDto>>(sr) ?? new List<HandoverDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapGet("/directors/{id}/handovers/content", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (handovers-content verb, director-level; the
            // ?path query rides in the payload). A non-Ok stream result collapses to 502, matching the HTTP null.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handovers-content", "", new HandoverContentRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                var body = DirectorCommandRouter.ReadBody<HandoverContentDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): create a standalone saved-handover document. Rides the
        // handover-create verb (director-level). Success is the old route's 201; a typed failure preserves 400;
        // a null result (Director not tunnel-connected) is 502.
        app.MapPost("/directors/{id}/handovers", async (HttpContext ctx, string id, HandoverCreateRequest req, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (req is null || string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required" });
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "content is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handover-create", "", req, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<HandoverDto>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: StatusCodes.Status201Created);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): delete a saved-handover document. Rides the handover-delete
        // verb (director-level; the ?path query rides in the payload). A path outside the handover folder is the
        // executor's BadRequest -> 400; a missing file its NotFound -> 404; a null result (Director not
        // tunnel-connected) is 502.
        app.MapDelete("/directors/{id}/handovers", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handover-delete", "", new RepoDeleteRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            return Results.Content(sr.BodyJson ?? "{\"removed\":true}", "application/json");
        });

        app.MapGet("/directors/{id}/fs/list", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (fs-list verb, director-level; the ?path
            // query rides in the payload). A non-Ok stream result (e.g. the Director core's bad-path BadRequest)
            // collapses to 502, exactly as the HTTP path surfaced a null.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "fs-list", "", new FsListRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                var body = DirectorCommandRouter.ReadBody<DirectoryListingDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapPost("/directors/{id}/sessions/github", async (HttpContext ctx, string id, GitHubSessionRequest req) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var ownerErr)) return ownerErr;
            if (req is null || string.IsNullOrWhiteSpace(req.Owner) || string.IsNullOrWhiteSpace(req.Repo))
                return Results.BadRequest(new { error = "owner and repo are required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions/github: {req.Owner}/{req.Repo} mode={req.TriggerMode}");

            // Gateway Cleanup Phase 2: create rides the target Director's stream (create-from-github verb,
            // director-level so SessionId is ""). Tunnel-only: a null return means the Director is not
            // connected, and a non-Ok stream result collapses to 502 - both surface as the error below.
            SessionDto? body;
            string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "create-from-github", "", req, CancellationToken.None);
            if (streamResult is null)
            {
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (body is null)
                return Results.Problem(err ?? "failed", statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: 201);
        });

        // Destructive-call gate (issue #212 W6/L4). A Director shutdown takes down every
        // claude.exe under it, so the request must (a) state a reason, and (b) when the
        // Director is reachable and has live sessions, confirm their count - a caller may
        // not kill sessions it did not know existed. Every branch logs loudly: the 2026-06-06
        // post-mortem found the force-kill path left no trace at all.
        app.MapDelete("/directors/{id}", async (HttpContext ctx, string id) =>
        {
            // Body read by hand instead of [FromBody]: an Accepts(application/json)
            // constraint would bounce body-less DELETEs off route matching, and a
            // body-less DELETE of an unknown id must still 404.
            ShutdownDirectorRequest body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<ShutdownDirectorRequest>() ?? new ShutdownDirectorRequest();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "invalid JSON body" });
            }
            catch (InvalidOperationException)
            {
                // Not a JSON request (typically a body-less DELETE): empty request.
                body = new ShutdownDirectorRequest();
            }

            // Identify the caller: the tailnet IP for remote callers (phone), and additionally
            // the owning process for loopback callers like the Cockpit (issue #212 L3).
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var localPeer = Core.Network.LoopbackPeerResolver.Resolve(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            var caller = localPeer is null ? ip : $"{ip} [{localPeer}]";
            FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} force={body.Force} " +
                $"confirmSessions={(body.ConfirmSessions?.ToString() ?? "-")} reason=\"{Truncate(body.Reason)}\" client={caller}");

            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var director, out var ownerErr))
                return ownerErr;

            if (string.IsNullOrWhiteSpace(body.Reason))
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director REJECTED (no reason): id={id} client={caller}");
                return Results.BadRequest(new { error = "reason is required: state why this Director is being shut down" });
            }

            // Post-cut: read the live session list from the push store (it carries the same SessionDto incl.
            // Status). A Director with no fresh push is not connected to the tunnel, so the live count is
            // unknowable and the session gate is skipped below. MTR-01: read the push store under the REQUEST's
            // own tenant (the same tenant the Director was just resolved in), never a hard-coded Local - on
            // hosted the Director lives in its account's partition, and Local would read the wrong one.
            var gateTenant = ResolveReadTenant(ctx, tenantBoundary)!.Value;
            var cachedSessions = pushedSessions?.TryGetFresh(gateTenant, director.DirectorId, streamStaleResolved);
            var sessions = cachedSessions?.ToList();
            if (sessions is not null)
            {
                var live = sessions
                    .Where(s => !string.Equals(s.Status, "Exited", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (live.Count > 0 && body.ConfirmSessions != live.Count)
                {
                    FileLog.Write($"[GatewayEndpoints] DELETE director BLOCKED by session gate: id={id} " +
                        $"liveSessions={live.Count} confirmSessions={(body.ConfirmSessions?.ToString() ?? "-")} client={caller}");
                    return Results.Json(new
                    {
                        error = $"director has {live.Count} live session(s); re-send with confirmSessions={live.Count} to proceed",
                        liveSessionCount = live.Count,
                        sessions = live.Select(s => new { s.SessionId, s.Name, s.RepoPath }).ToList(),
                    }, statusCode: StatusCodes.Status409Conflict);
                }
            }
            else
            {
                // Unreachable Director: the live count is unknowable, and an unreachable
                // Director is exactly the one an operator must still be able to stop.
                FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} live-session count UNKNOWN (director unreachable); session gate skipped");
            }

            // Gateway Cleanup Phase 2: the Gateway-initiated REMOTE stop rides the tunnel (shutdown verb,
            // director-level so SessionId is ""). Tunnel-only: there is no HTTP arm. POST /shutdown stays on the
            // Director loopback floor for the local launcher; this tunnel verb triggers the same in-process
            // self-shutdown.
            var shutdownSr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "shutdown", "", null, CancellationToken.None, machineName: director.MachineName);
            var ok = shutdownSr is not null && shutdownSr.Ok;
            if (ok)
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} pid={director.Pid} graceful shutdown accepted");
                return Results.Json(new { accepted = true });
            }

            if (body.Force)
            {
                // HOSTED DENY (production-readiness B2, process-control). The force-kill resolves the process by
                // director.Pid - a number the Director itself supplied in its Hello - and kills its WHOLE tree on
                // THIS Gateway's own machine (Process.GetProcessById runs against the local process table). On
                // self-host that is correct: the Director really is a process on this machine and the single owner
                // is force-killing their own stuck instance. On the HOSTED Gateway the process is SHARED
                // infrastructure and the Director is a REMOTE process reached over the tunnel - it is not on this
                // host at all - so that pid, resolved locally, names whatever unrelated process on the shared host
                // happens to hold that number: the Gateway itself, another tenant's container, anything. A tenant
                // must never be able to kill host processes by number, so the local force-kill is refused on
                // hosted. The graceful tunnel shutdown attempted above is already tenant-scoped and is the ONLY
                // stop a hosted caller gets; there is no host-local process for the Gateway to reach on their
                // behalf. 404 (not 403) for the same reason as POST /shutdown: on hosted this action does not exist
                // as a concept - no credential could ever make killing a host process by client pid safe.
                if (GatewayHostedMode.IsHosted)
                {
                    FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL REFUSED on hosted: id={id} pid={director.Pid} client={caller}");
                    return Results.Json(new
                    {
                        error = "force-killing a Director by process id is not available on the hosted Gateway",
                    }, statusCode: StatusCodes.Status404NotFound);
                }

                FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL: id={id} pid={director.Pid} " +
                    $"tree=true reason=\"{Truncate(body.Reason)}\" client={caller}");
                try
                {
                    var killed = (forceKillDirectorTree ?? DefaultForceKillProcessTree)(director.Pid);
                    FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL done: id={id} pid={director.Pid} killed={killed}");
                    return Results.Json(new { accepted = true, killed });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL FAILED: id={id} pid={director.Pid} error={ex.Message}");
                    return Results.Problem("could not kill process: " + ex.Message, statusCode: 500);
                }
            }

            FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} graceful shutdown failed and force=false; nothing stopped");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapGet("/sessions/{sid}/summary", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Tunnel-only. The Director's summary core sets DirectorId in its body, so the pass-through matches.
            if (director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "summary", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Read-only source-control snapshot proxy (issue #1266): forwards to whichever Director owns the
        // session and returns its GET /sessions/{sid}/git response (branch, ahead/behind, last commit, and
        // the additive per-file staged/unstaged lists) for the Cockpit's Source Control tab. This route is
        // READ-ONLY: it does not proxy any git WRITE route (stage / unstage / discard / commit stay
        // desktop-only). It self-checks HasValidToken(ctx, token, devices) so a phone or browser per-device
        // key is accepted, not only the shared machine token - the same 401-avoidance every browser-facing
        // session route needs (the device-blind check once bit the dictation route, issue #1045) - and so
        // the route stays gated even when the host-wide auth middleware is off.
        app.MapGet("/sessions/{sid}/git", async (string sid, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            // Issue #1240: pass the owner cache so a warm session is resolved with ONE Director probe
            // instead of a full fleet fan-out (the same fast path every other per-session route now uses).
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Tunnel-only (verb "git-status"). The Ok body IS the GitSnapshot JSON, passed through unchanged.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "git-status", sid, null, ctx.RequestAborted, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Handover info proxy (issue #1214). Forwards to whichever Director owns the session and returns
        // the desktop "Handover info" identity block (name, session id, repo, director id, machine,
        // version) for a browser. Gated by the same Bearer/device-key auth as every other session route
        // (the global AuthMiddleware 401s a credential-less request before it reaches here). The Director
        // address is never leaked: this returns HandoverInfoDto, which carries no Control API endpoint,
        // and the resolved ControlEndpoint stays server-side. 404 when the session is unknown to every
        // Director; 502 when the owning Director is unreachable (never a silent empty body).
        app.MapGet("/sessions/{sid}/handover", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            // Issue #1240: resolve the owner through the same cache fast path as every other per-session route.
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Tunnel-only. The Director's handover core sets DirectorId in its body, so the pass-through matches.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "handover", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Recap proxy. Both endpoints transparently forward to whichever Director owns the
        // session. The Director side does the heavy lifting (claude --print + cache); this
        // is just routing.
        app.MapGet("/sessions/{sid}/recap", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            // Tunnel-only (read the cached recap). This is the READ; the slow generate (POST) is handled separately.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "recap", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        app.MapPost("/sessions/{sid}/recap", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
            var model = ctx.Request.Query["model"].ToString();
            FileLog.Write($"[GatewayEndpoints] POST /recap: sid={sid}, director={director.DirectorId}, model={model ?? "(default)"}");
            // Gateway Cleanup (Phase 2, PR C): tunnel-first. Like wingman-ask this is a SLOW LLM call, so the
            // request ct (ctx.RequestAborted) threads into the SignalR invocation (no per-invocation timeout;
            // keep-alive pings sustain the long await) - synchronous browser contract byte-identical. A null
            // The Ok body IS the RecapResponse JSON, returned 201 as before.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "recap-generate", sid,
                new RecapGenerateRequest { Model = model }, ctx.RequestAborted,
                // Runs a language model on the Director before it can answer, so it gets the longer wait.
                timeout: DirectorCommandRouter.LanguageModelCommandTimeout, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json", null, StatusCodes.Status201Created)
                : Results.Problem("recap failed", statusCode: StatusCodes.Status502BadGateway);
        });

        // Compaction (issue #2150). This gets its OWN literal route rather than riding the catch-all,
        // for one reason: the catch-all's wait is the 30-second default, and a compaction is a language
        // model summarizing a whole conversation - it routinely outruns that. Killing a real compaction at
        // 30 seconds and reporting a timeout would be the failure this verb exists to prevent. So it takes
        // the documented override, the same one recap and handover take, and it sits OUTSIDE the Director's
        // own 2-minute compaction wait so the inner bound always fires first and says what did not happen.
        app.MapPost("/sessions/{sid}/compact-context", async (HttpContext ctx, string sid, CompactContextRequest? req) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);

            FileLog.Write($"[GatewayEndpoints] POST /compact-context: sid={sid}, director={director.DirectorId}, " +
                          $"continue={(string.IsNullOrWhiteSpace(req?.ContinuePrompt) ? "no" : "yes")}");
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "compact-context", sid,
                new CompactContextRequest { ContinuePrompt = req?.ContinuePrompt }, ctx.RequestAborted,
                timeout: DirectorCommandRouter.LanguageModelCommandTimeout, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        app.MapPost("/handover", async (HttpContext ctx, HandoverRequest req) =>
        {
            // Gateway-side /handover dispatches to whichever Director owns the source
            // session. Same-Director case: proxy the request to that Director. Cross-Director
            // case (toDirectorId set + different from source): read the prose context from
            // source-side, then spawn the target session on the target Director with the
            // context as PrePrompt.

            if (req is null || string.IsNullOrEmpty(req.FromSessionId))
                return Results.BadRequest(new { error = "fromSessionId is required" });
            if (string.IsNullOrEmpty(req.ToSessionId) && string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "exactly one of toSessionId or toRepoPath is required" });

            FileLog.Write($"[GatewayEndpoints] POST /handover: from={req.FromSessionId} toSid={req.ToSessionId} toRepo={req.ToRepoPath} toDir={req.ToDirectorId}");

            var (sourceDirector, sourceSession) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, req.FromSessionId, pushedSessions, streamStaleResolved, owners);
            if (sourceSession is null || sourceDirector is null)
                return SessionUnavailable(ctx, tenantBoundary, pushedSessions, req.FromSessionId);

            DirectorDto? targetDirector = null;
            if (!string.IsNullOrEmpty(req.ToDirectorId)
                && !string.Equals(req.ToDirectorId, sourceDirector.DirectorId, StringComparison.OrdinalIgnoreCase))
            {
                // Issue #1869: resolve the TARGET Director in the REQUEST'S OWN tenant. The id here is
                // client-supplied, and the fleet-global lookup this replaced would answer about a Director
                // belonging to another account - so whether another tenant's Director exists changed this
                // caller's answer. That mattered little while the route was unreachable on hosted; this change
                // makes it reachable, so activating it on a fleet-global lookup would open a cross-tenant path
                // in the act of fixing one. A caller can now only ever name a Director it owns.
                var targetTenant = ResolveReadTenant(ctx, tenantBoundary);
                targetDirector = targetTenant is null ? null : registry.Get(targetTenant.Value, req.ToDirectorId);
                if (targetDirector is null)
                    return Results.NotFound(new { error = "target director not found" });
            }

            if (targetDirector is null)
            {
                // Same-Director: proxy the entire request. Gateway Cleanup Phase 2: ride the tunnel first
                // (handover-generate verb, director-level so SessionId is ""). Tunnel-only: a null return means
                // the Director is not connected, and a non-Ok stream result collapses to 502.
                HandoverResponse? body; string? err;
                // Runs a language model on the Director before it can answer, so it gets the longer wait.
                var hgSr = await DirectorCommandRouter.TrySendAsync(sendCommand, sourceDirector.DirectorId, "handover-generate", "", req, CancellationToken.None,
                    timeout: DirectorCommandRouter.LanguageModelCommandTimeout, machineName: sourceDirector.MachineName);
                if (hgSr is null)
                {
                    body = null; err = "source director not connected to the tunnel";
                }
                else
                {
                    body = hgSr.Ok ? DirectorCommandRouter.ReadBody<HandoverResponse>(hgSr) : null;
                    err = hgSr.Ok ? null : DirectorCommandRouter.DescribeFailure(hgSr);
                }
                if (body is null)
                    return Results.Problem(err ?? "handover failed", statusCode: StatusCodes.Status502BadGateway);
                if (body.TargetSession is not null) body.TargetSession.DirectorId = sourceDirector.DirectorId;
                return Results.Json(body, statusCode: 201);
            }

            // Cross-Director path. Only the "new session in target Director" form is supported here.
            if (!string.IsNullOrEmpty(req.ToSessionId))
                return Results.BadRequest(new { error = "cross-director handover to an existing session is not supported in v1; use toRepoPath instead" });
            if (string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "toRepoPath is required for cross-director handover" });

            // Gateway Cleanup Phase 2: read the source session's handover context over the tunnel
            // (handover-context verb), falling back to the byte-identical HTTP GET when the source has no stream.
            // Post-cut: tunnel-only. A null result means the source Director is not connected -> 502.
            var ctxSr = await DirectorCommandRouter.TrySendAsync(sendCommand, sourceDirector.DirectorId, "handover-context",
                req.FromSessionId, new HandoverContextRequest { ExtraContext = req.ExtraContext }, CancellationToken.None);
            if (ctxSr is null)
                return Results.Problem("source director is not connected to the tunnel", statusCode: 502);
            if (!ctxSr.Ok)
                return Results.Problem("failed to read handover-context from source director: " + DirectorCommandRouter.DescribeFailure(ctxSr), statusCode: 502);
            string contextText = DirectorCommandRouter.ReadBody<HandoverContextResponse>(ctxSr)?.Text ?? "";

            var spawnReq = new NewSessionRequest
            {
                RepoPath = req.ToRepoPath,
                Agent = req.ToAgent,
                PrePrompt = contextText,
                // Session origin (devthrottle_internal issue #982): a direct API route, like the
                // interrupted-session restore above. The kind is left unstated for the same reason - a
                // handover is asked for by a person moving work or by a session handing itself over,
                // and this handler cannot tell them apart.
                //
                // The SOURCE session is deliberately NOT recorded as the parent. ParentSessionId means
                // "the session that asked for this one", and in a handover the source is the session
                // being LEFT, which is a different relationship; putting it here would make the lineage
                // tree quietly mean two things at once.
                OriginSurface = Core.Sessions.SessionOriginSurfaces.Api,
            };
            // Gateway Cleanup Phase 2: create the target over the tunnel (create verb, director-level), tunnel-first;
            // the dedicated 20s HTTP client is the fallback pre-cut (the tunnel unary has no 2s aggregate timeout).
            // Post-cut: tunnel-only. A null result means the target Director is not connected -> 502.
            var spawnSr = await DirectorCommandRouter.TrySendAsync(sendCommand, targetDirector.DirectorId, "create", "", spawnReq, CancellationToken.None, machineName: targetDirector.MachineName);
            if (spawnSr is null)
                return Results.Problem("target director is not connected to the tunnel", statusCode: 502);
            if (!spawnSr.Ok)
                return Results.Problem($"target director returned {DirectorCommandRouter.DescribeFailure(spawnSr)}", statusCode: 502);
            SessionDto? newSession = DirectorCommandRouter.ReadBody<SessionDto>(spawnSr);
            if (newSession is not null) newSession.DirectorId = targetDirector.DirectorId;

            return Results.Json(new HandoverResponse
            {
                Accepted = true,
                TargetSession = newSession,
                ContextSent = contextText,
                ArchivedAt = null, // archive is written only on the source side; cross-director skips
            }, statusCode: 201);
        });

        // Issue #1229: mint a human-issued broadcast grant. Reaching beyond a sender's own team needs
        // one of these. This endpoint sits behind the host-wide auth middleware (the shared token or a
        // per-device key) and has NO Director relay, so an agent - which can only reach its own Director,
        // never the Gateway directly - cannot mint its own grant. A human tool holding the token mints
        // one and hands the id to the broadcaster. (A dedicated human-approval surface can tighten who
        // may mint in a later pass.)
        app.MapPost("/fleet/broadcast-grants", (HttpContext ctx) =>
        {
            // audit-a: the grant is bound to the caller's OWN tenant (server-resolved from the device key,
            // never the body). On hosted a request with no bound tenant is DENIED (403) - it has no partition
            // to mint into. Self-host resolves to Local, so behaviour there is unchanged.
            var tenant = ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
            {
                FileLog.Write("[GatewayEndpoints] POST /fleet/broadcast-grants DENIED - no tenant is bound to this request");
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var grantId = broadcastGovernor.MintGrant(tenant.Value);
            FileLog.Write($"[GatewayEndpoints] POST /fleet/broadcast-grants: minted a broadcast grant for tenant={tenant.Value.ToLogString()}");
            return Results.Json(new { grantId, expiresInSeconds = (int)TimeSpan.FromMinutes(10).TotalSeconds });
        });

        app.MapPost("/fanout", (HttpContext ctx, FanoutRequest req) => RunFanoutAsync(ctx, req));

        // The fan-out itself: locate every target, rule on whether the sender may reach them, rate-limit,
        // deliver in parallel. Extracted from the route lambda by the Remove-the-network-port mission's phase
        // 2 so POST /fleet/broadcast - which computes the sender's team and then wants exactly this - runs the
        // SAME scope decision, grant check, rate limit and delivery rather than a second copy of them.
        async Task<IResult> RunFanoutAsync(HttpContext ctx, FanoutRequest req)
        {
            if (req is null || req.SessionIds is null || req.SessionIds.Count == 0)
                return Results.BadRequest(new { error = "sessionIds is required" });
            if (string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            FileLog.Write($"[GatewayEndpoints] POST fanout: count={req.SessionIds.Count}, len={req.Text.Length}, from={req.FromSessionId}");

            // audit-a: the tenant that owns this broadcast's rate-limit window and validates its grant is
            // resolved from the caller's authenticated device key (server-side), NEVER from the body. On
            // hosted a request with no bound tenant is DENIED (403) - it has no partition to charge or grant
            // against. Self-host resolves to Local, so behaviour there is unchanged.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
            {
                FileLog.Write("[GatewayEndpoints] fanout DENIED - no tenant is bound to this request");
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // A SESSION KEY MAY NOT NAME SOMEBODY ELSE AS THE SENDER.
            //
            // FanoutRequest carries a caller-supplied FromSessionId, and it is used for two things that
            // both decide authority: which team scope the broadcast is judged against, and which bucket
            // the rate limit counts into. Neither was compared with the authenticated caller, so a
            // session key could name another same-tenant session to borrow its team scope, or vary the
            // id to sidestep its own rate bucket. The newer /fleet/broadcast contract deliberately has no
            // sender field for exactly this reason; this is the same rule applied to the older route
            // rather than a second contract with a different answer.
            //
            // A device key (the desktop, the phone) is left alone: it acts for the account rather than as
            // a session, so it has no session identity to be pinned to.
            var callingSession = AuthMiddleware.CallingSession(ctx);
            var pinned = FanoutSenderPin.Resolve(callingSession?.SessionId.ToString(), req.FromSessionId);
            if (pinned.Overridden)
                FileLog.Write($"[GatewayEndpoints] fanout sender OVERRIDDEN: key belongs to {pinned.SessionId}, request claimed {req.FromSessionId}");
            req.FromSessionId = pinned.SessionId;

            // Resolve all directors once up-front, capturing each target's broadcast scope (issue #1229).
            var directorBySession = new Dictionary<string, DirectorDto>();
            var targetScopes = new List<(string SessionId, BroadcastScope Scope)>();
            foreach (var sid in req.SessionIds)
            {
                var (d, s) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
                if (d is not null && s is not null)
                {
                    directorBySession[sid] = d;
                    targetScopes.Add((sid, BuildBroadcastScope(d, s)));
                }
            }

            // Issue #1229: the Hub decides whether this broadcast may reach every recipient. A broadcast
            // that stays inside the sender's own team (its group, or - for a solo session - the same repo
            // on the same machine) is free; one that reaches beyond it is refused unless a human grant
            // (plus a reason) authorizes it. The sender's scope is read from the Gateway's OWN fleet view,
            // never trusted from the request body.
            BroadcastScope? senderScope = null;
            if (!string.IsNullOrWhiteSpace(req.FromSessionId))
            {
                var (sd, ss) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, req.FromSessionId, pushedSessions, streamStaleResolved, owners);
                if (sd is not null && ss is not null) senderScope = BuildBroadcastScope(sd, ss);
            }

            // Only resolve a grant when a recipient is genuinely out of team and a reason accompanies it,
            // so a valid grant is not spent validating a malformed request.
            var anyOutOfScope = senderScope is null
                ? targetScopes.Count > 0
                : targetScopes.Any(t => !senderScope.Value.Includes(t.Scope));
            var hasValidGrant = anyOutOfScope
                && !string.IsNullOrWhiteSpace(req.Reason)
                && broadcastGovernor.IsGrantValid(reqTenant.Value, req.GrantId);

            var decision = FleetBroadcastPolicy.Evaluate(senderScope, targetScopes, hasValidGrant, req.Reason);
            if (!decision.Allowed)
            {
                FileLog.Write($"[GatewayEndpoints] fanout DENIED ({decision.Outcome}): from={req.FromSessionId}, targets={req.SessionIds.Count}, outOfScope={decision.OutOfScopeTargetIds.Count}, reason='{req.Reason}'");
                return Results.Json(new FanoutResponse
                {
                    Denied = true,
                    DeniedReason = decision.DeniedReason,
                    StartedAt = DateTime.UtcNow,
                    FinishedAt = DateTime.UtcNow,
                });
            }

            // Rate-limit even an in-team broadcast so a runaway agent cannot storm the fleet in a loop.
            var rate = broadcastGovernor.TryRecordSend(reqTenant.Value, req.FromSessionId);
            if (!rate.Allowed)
            {
                FileLog.Write($"[GatewayEndpoints] fanout RATE-LIMITED: from={req.FromSessionId}, limit={rate.LimitPerWindow}/{rate.WindowSeconds}s");
                return Results.Json(new FanoutResponse
                {
                    Denied = true,
                    DeniedReason = $"Too many broadcasts in a short time (limit {rate.LimitPerWindow} per {rate.WindowSeconds} seconds). Wait a moment and try again. See issue #1229.",
                    StartedAt = DateTime.UtcNow,
                    FinishedAt = DateTime.UtcNow,
                });
            }

            FileLog.Write($"[GatewayEndpoints] fanout ALLOWED ({decision.Outcome}): from={req.FromSessionId}, inScope={decision.InScopeTargetIds.Count}, outOfScope={decision.OutOfScopeTargetIds.Count}");

            var startedAt = DateTime.UtcNow;

            // Send to all in parallel
            var sendTasks = req.SessionIds.Select(async sid =>
            {
                var sw = Stopwatch.StartNew();
                if (!directorBySession.TryGetValue(sid, out var director))
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        Status = "not_found",
                        Error = "session not found",
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                // AGENT-DRIVEN WHEN THE SENDER IS A SESSION, and not otherwise (ruling R12 of the "Clean up
                // Your Throttle" mission, 2026-09-05). A fanout from a session key is one agent prompting
                // others and must be recorded as agent traffic; left unmarked the Director records it as
                // ordinary UserInput with no origin, so it falls out of the person's figures only because
                // no surface resolved for it, and out of the agent-driven lane entirely. That was 292 of
                // the owner's 296 fleet messages in 2026-W35: absent from both numbers.
                //
                // The test is the AUTHENTICATED caller, never req.FromSessionId. A device key acts for the
                // account - a person broadcasting from the desktop or the phone - and it can put any string
                // in that field, so trusting it would let a caller decide whether its own turns count.
                var promptReq = new PromptRequest
                {
                    Text = req.Text,
                    AppendEnter = req.AppendEnter,
                    AgentDriven = callingSession is not null,
                };
                // Fanout delivery rides the tunnel (prompt verb). Tunnel-only: there is no HTTP arm.
                bool ok; PromptResponse? body; string? err;
                var deliverSr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "prompt", sid, promptReq, CancellationToken.None, machineName: director.MachineName);
                if (deliverSr is null)
                {
                    ok = false; body = null; err = "director not connected to the tunnel";
                }
                else
                {
                    ok = deliverSr.Ok;
                    body = deliverSr.Ok ? DirectorCommandRouter.ReadBody<PromptResponse>(deliverSr) : null;
                    err = deliverSr.Ok ? null : DirectorCommandRouter.DescribeFailure(deliverSr);
                }
                if (!ok || body is null)
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        DirectorId = director.DirectorId,
                        Status = "failed",
                        Error = err,
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                if (!req.WaitForIdle)
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        DirectorId = director.DirectorId,
                        Status = "idle",
                        Output = "",
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                // Poll for idle. Gateway Cleanup Phase 2: snapshot verb, tunnel-first (HTTP fallback pre-cut).
                var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
                string finalState = body.ActivityState;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(750);
                    var cur = await SnapshotTunnelFirstAsync(sendCommand, director, sid, CancellationToken.None);
                    if (cur is null) { finalState = "Exited"; break; }
                    finalState = cur.ActivityState;
                    if (finalState is "Idle" or "WaitingForInput" or "Exited" or "Failed") break;
                }

                // Get the diff. Gateway Cleanup Phase 2: buffer verb, tunnel-first (HTTP fallback pre-cut).
                var buf = await BufferTunnelFirstAsync(sendCommand, director, sid, 500, body.BufferCursor, CancellationToken.None);
                var output = buf?.Text ?? "";

                sw.Stop();
                return new FanoutResult
                {
                    SessionId = sid,
                    DirectorId = director.DirectorId,
                    Status = finalState switch
                    {
                        "Idle" or "WaitingForInput" => "idle",
                        "Exited" or "Failed" => "failed",
                        _ => "timeout",
                    },
                    Output = output,
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }).ToList();

            var results = await Task.WhenAll(sendTasks);

            return Results.Json(new FanoutResponse
            {
                Results = results.ToList(),
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
            });
        }

        app.MapGet("/events", async (HttpContext ctx) =>
        {
            var requestTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (requestTenant is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(
                    new { error = "no tenant is bound to this request" },
                    cancellationToken: ctx.RequestAborted);
                return;
            }
            var resolvedTenant = requestTenant.Value;

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            var ct = ctx.RequestAborted;
            var queue = System.Threading.Channels.Channel.CreateUnbounded<GatewayEvent>();

            void OnAdded(DirectorArrival arrival)
            {
                if (arrival.Tenant.Equals(resolvedTenant))
                    queue.Writer.TryWrite(new GatewayEvent("director.added", arrival.Director.DirectorId));
            }

            void OnRemoved(DirectorRemoval removal)
            {
                if (removal.Tenant.Equals(resolvedTenant))
                    queue.Writer.TryWrite(new GatewayEvent("director.removed", removal.DirectorId));
            }

            registry.OnDirectorAdded += OnAdded;
            registry.OnDirectorRemoved += OnRemoved;

            // Flush the response start NOW (SSE convention): events are not replayed,
            // so a subscriber must be able to treat "headers received" as "attached".
            // Without this Kestrel holds the headers until the first event is written.
            await ctx.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var ev in queue.Reader.ReadAllAsync(ct))
                {
                    var line = $"event: {ev.Type}\ndata: {{\"id\":\"{ev.Id}\"}}\n\n";
                    await ctx.Response.WriteAsync(line, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                registry.OnDirectorAdded -= OnAdded;
                registry.OnDirectorRemoved -= OnRemoved;
            }
        });

        // Windows-only: this launches the Windows desktop cc-director.exe via ShellExecute, which
        // only exists on a Windows install. Off Windows the route is not mapped.
        if (OperatingSystem.IsWindows())
        app.MapPost("/directors", async (LaunchDirectorRequest? body) =>
        {
            body ??= new LaunchDirectorRequest();
            FileLog.Write($"[GatewayEndpoints] POST director: launch new instance");

            // HOSTED DENY (production-readiness B2, process-control). This route ShellExecutes a cc-director.exe
            // on the GATEWAY's OWN machine (Process.Start below). On self-host that is the desktop spawning a
            // local Director. On the SHARED hosted Gateway it would start an arbitrary process on shared
            // infrastructure at any authenticated tenant's request, with no per-tenant meaning and no owner check
            // - so it is refused on hosted. 404 for the same reason as POST /shutdown: on hosted launching a
            // host-local process does not exist as a concept. NOTE: this is an in-handler refusal rather than the
            // verb-less HostedRouteDeny primitive because the tenant-scoped GET /directors list shares this exact
            // path, and that primitive refuses EVERY verb on a path - it would take the list route off the air on
            // hosted too. Self-host reaches the launch below byte-identically to before.
            if (GatewayHostedMode.IsHosted)
            {
                FileLog.Write("[GatewayEndpoints] POST /directors REFUSED on hosted (host-local process launch)");
                return Results.Json(new
                {
                    error = "launching a Director is not available on the hosted Gateway",
                }, statusCode: StatusCodes.Status404NotFound);
            }

            var exePath = ResolveDirectorExe();
            if (exePath is null)
                return Results.Problem("cc-director.exe not found on PATH or in standard install location", statusCode: 500);

            // The spawned cc-director.exe runs on the Gateway's OWN machine, so it registers as a Local-tenant
            // entry; scope the before/after diff to Local (this route is refused on hosted).
            var beforeIds = registry.ListDirectors(TenantId.Local).Select(d => d.DirectorId).ToHashSet();

            try
            {
                // --skip-workspace-picker so the spawned Director never blocks on the
                // workspace-selection modal at startup (the whole point of a programmatic
                // spawn is to skip user interaction).
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                };
                psi.ArgumentList.Add("--skip-workspace-picker");
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                return Results.Problem("failed to start director: " + ex.Message, statusCode: 500);
            }

            // Poll for new director registration
            var deadline = DateTime.UtcNow.AddMilliseconds(body.TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                var newId = registry.ListDirectors(TenantId.Local).Select(d => d.DirectorId).FirstOrDefault(id => !beforeIds.Contains(id));
                if (newId is not null)
                {
                    // MTR-01: this route ShellExecutes a cc-director.exe on the GATEWAY's own machine, so the
                    // newly-registered instance is always a Local-tenant entry - resolve it within Local, not by
                    // a bare cross-tenant scan.
                    var d = registry.Get(TenantId.Local, newId)!;
                    return Results.Json(new { directorId = d.DirectorId, pid = d.Pid });
                }
            }

            return Results.Problem("director did not register within timeout", statusCode: 504);
        });
    }

    // Automatic session roles (chunk 1): compute each session's role from the assembled fleet, then stamp
    // the presentation fold. Role: a session controlled by a session that is STILL ALIVE in the roster is a
    // Worker (this wins even if it also controls sub-workers - nesting keeps the Worker label); a non-worker
    // that controls at least one LIVE worker is a Manager; everything else is Standalone. The fold
    // (EffectiveColor/StateLabel/TriageBucket) then reads SessionRole to suppress a live Worker's red, so it
    // must run AFTER the role is known. NeedsYouSince keys off the final EffectiveColor, so it is stamped
    // here too (a suppressed Worker is not "red", so it never enters the needs-you clock).
    // Gateway Cleanup mission (Wave 4b): map a stored Mission to the wire MissionDto - the SAME contract the
    // Director's /missions routes return, so a client cannot tell a Gateway-native mission from a Director one.
    private static MissionDto ToMissionDto(Core.Sessions.Mission m) => new()
    {
        MissionId = m.MissionId,
        MissionName = m.MissionName,
        Why = m.Why,
        WhyUpdatedAt = m.WhyUpdatedAt,
        State = Core.Sessions.MissionStates.Normalize(m.State) ?? Core.Sessions.MissionStates.Active,
        StateChangedAt = m.StateChangedAt,
    };

    /// <summary>
    /// Drive a mission's workflow run to <paramref name="terminal"/> when its mission ends. Returns a
    /// sentence for the caller when there is something to say, or null when the outcome speaks for itself.
    ///
    /// Walks created -> active -> terminal where needed, because the run store enforces its transition
    /// table and refuses to jump straight from created to succeeded. That is the ordinary case here, not an
    /// edge one: no mission run on this fleet has ever left created.
    ///
    /// Never throws. Ending the mission is the primary fact and is already persisted by the time this runs;
    /// a run that will not advance is reported, not escalated into a failed request that would leave the
    /// caller believing the ending did not happen when it did.
    /// </summary>
    private static string? EndMissionRun(Workflows.WorkflowRunStore workflowRuns, Guid missionId, string terminal)
    {
        try
        {
            var run = workflowRuns.List(missionId: missionId, limit: 1).FirstOrDefault();
            if (run is null)
                return "This mission has no workflow run of its own, so there was no run to end.";

            if (WorkflowRunStatus.Terminal.Contains(run.Status, StringComparer.Ordinal))
                return null;   // already ended; nothing to say

            // The table allows created -> active and active -> succeeded/abandoned, but not created ->
            // succeeded. Step through when we have to.
            if (string.Equals(run.Status, WorkflowRunStatus.Created, StringComparison.Ordinal)
                && !string.Equals(terminal, WorkflowRunStatus.Abandoned, StringComparison.Ordinal))
            {
                workflowRuns.Patch(run.Id, new PatchWorkflowRunRequest { Status = WorkflowRunStatus.Active });
            }

            workflowRuns.Patch(run.Id, new PatchWorkflowRunRequest { Status = terminal });
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayEndpoints] EndMissionRun({missionId} -> {terminal}) FAILED: {ex.Message}");
            return "The mission was ended, but its workflow run could not be closed with it - "
                 + "it will still appear among the running workflow runs.";
        }
    }

    /// <summary>
    /// The hosted refusal payload for POST /shutdown (production-readiness B2). Validated on construction, so a
    /// blank field fails the Gateway at startup. The primitive reads <see cref="GatewayHostedMode.IsHosted"/>
    /// DIRECTLY, never an optional argument that fails OPEN when a caller forgets it. 404 rather than 403: on
    /// hosted this route does not exist as a concept, and 403 would imply some credential could reach it - none
    /// can, because a process-wide shutdown of shared infrastructure has no per-tenant meaning.
    /// </summary>
    /// <summary>
    /// The real force-kill used by DELETE /directors/{id} when no test seam is injected: resolve the Director's
    /// process by its pid on THIS machine and end its whole tree. Byte-identical to the pre-seam inline body.
    /// Only ever reached on self-host - the hosted branch refuses before this is called (a client-supplied pid
    /// resolved against the shared host's local process table could name any process on it).
    /// </summary>
    private static bool DefaultForceKillProcessTree(int pid)
    {
        var proc = Process.GetProcessById(pid);
        proc.Kill(entireProcessTree: true);
        return true;
    }

    private static Tenancy.HostedDenial ShutdownHostedDenial() => new(
        family: "gateway-shutdown",
        message: "shutdown is not available on the hosted Gateway",
        reason: "POST /shutdown stops the whole Gateway process, which on shared hosted infrastructure would " +
                "take the Gateway down for every tenant at once - it is the self-host self-update helper's " +
                "local control, has no per-tenant dimension, and carries no owner check, so on hosted no " +
                "credential may reach it",
        unDenyInstruction: "do NOT simply remove this deny: a hosted process-lifecycle control needs a scoped, " +
                "authorized meaning first (per-deployment operator authorization, never a per-tenant device key), " +
                "because a shared Gateway must never let one tenant end the process for all of them",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The ROLE UNIVERSE for a fold that runs OUTSIDE the /sessions roster loop: the CALLER'S TENANT's
    /// tunnel-connected Directors' pushed rosters, grouped by Director.
    ///
    /// The roster handler builds its universe inline as it walks the Directors it already pulled. The two
    /// other folding routes (/exes/list and GET /sessions/{sid}) have no such loop, and both need the whole
    /// tenant for the same reason: a session's role depends on whether its controller is alive, and the
    /// controller may live on a Director this route was never otherwise interested in. /exes/list is the
    /// sharp case - it is a LOCAL-MACHINE page, but a Worker's Manager can be on another machine entirely, so
    /// the universe spans every machine. Every one of those Directors is still in the caller's OWN tenant (a
    /// Worker and its Manager belong to the same account), so spanning machines does NOT mean spanning tenants.
    ///
    /// Hosted Multi-Tenancy (audit H1): the universe is <see cref="DirectorRegistry.ListDirectors(TenantId)"/>,
    /// the caller's partition, NOT the fleet-global <see cref="DirectorRegistry.ListDirectors()"/>. Two tenants
    /// can each own a Director with the SAME id (the registry key is (tenant, id), and the id is client-chosen),
    /// so the fleet-global list projects to bare ids in which one tenant's ids appear beside another's. Reading
    /// the caller's cache under that fleet-wide id set means ANOTHER tenant's registered Director id decides
    /// which of the caller's own cached rosters this fold surfaces - a cross-tenant coupling. Bounding the
    /// universe to the caller's own registered Directors removes it: the tenant scope on the push-store read
    /// keeps another tenant's DATA out, and this scope keeps another tenant's ID SET out, so the fold is
    /// correct by construction rather than by the push store's key being the sole boundary.
    ///
    /// Returns copies (the push store hands out deep copies), so stamping the result never writes through
    /// to the cache.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<SessionDto>> FleetByDirector(
        DirectorRegistry registry, Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale,
        TenantId tenant)
    {
        var byDirector = new Dictionary<string, IReadOnlyList<SessionDto>>(StringComparer.Ordinal);
        if (pushedSessions is null) return byDirector;
        foreach (var d in registry.ListDirectors(tenant))
        {
            var cached = pushedSessions.TryGetFresh(tenant, d.DirectorId, streamStale);
            if (cached is not null) byDirector[d.DirectorId] = cached;
        }
        return byDirector;
    }

    /// <summary>
    /// Resolve a request's tenant for a session READ (Hosted Multi-Tenancy, session-serving PR1). Null means
    /// the caller must be DENIED (403): on the hosted Gateway an authenticated request whose device key has no
    /// bound tenant is refused, NEVER served the Local partition (which would be a wrong-tenant read waiting to
    /// happen). Self-host is always Local - behavior unchanged.
    ///
    /// GATED ON <see cref="GatewayHostedMode.IsHosted"/> ITSELF, never on whether a boundary was passed in
    /// (tenant-boundary hardening, release 2026-07-31, finding CR-7 - the same shape
    /// <c>GatewayDictationEndpoint.ResolveTenant</c> already carries). Deciding on the argument fails OPEN:
    /// the boundary is a SECURITY argument, and this resolver used to answer <see cref="TenantId.Local"/>
    /// whenever it was absent, so ONE forgotten argument at any of the dozens of call sites that thread it
    /// silently collapsed every hosted tenant into the Local partition - with nothing failing loud to say so.
    /// On hosted, a missing boundary and a boundary that is not hosted-wired both resolve to null, and null
    /// is a REFUSAL. The second defence is that the boundary parameter is REQUIRED at every Map signature
    /// that threads it here, so omitting it is a compile error rather than a runtime downgrade.
    /// </summary>
    internal static TenantId? ResolveReadTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
    {
        if (!GatewayHostedMode.IsHosted)
            return boundary is null ? TenantId.Local : boundary.ResolveRequestTenant(ctx);
        if (boundary is null || !boundary.IsHosted)
            return null;
        return boundary.ResolveRequestTenant(ctx);
    }

    /// <summary>
    /// MTR-01 (Codex round 1): the answer for the legacy same-machine HTTP discovery plane (register /
    /// heartbeat / doorbell / unregister) when this is the hosted Gateway. That plane is a self-host-only
    /// concept - hosted Directors ride the tunnel - and every entry it writes is Local-keyed, so leaving it
    /// reachable on hosted is the Local-shadow registration / event-ring injection path. 403, explicit.
    /// </summary>
    private static IResult LegacyDiscoveryPlaneUnavailable()
        => Results.Json(new { error = "the same-machine HTTP discovery plane is not available on the hosted Gateway" },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// MTR-01: resolve a per-director route's target Director IN THE REQUEST'S OWN TENANT. Every client-serving
    /// <c>/directors/{id}/...</c> route (and the <c>/interrupted</c> plane's by-id legs) resolves its Director
    /// through this, so a client-supplied id can only ever name a Director the caller's authenticated device key
    /// owns. The registry has no bare-id accessor anymore, so there is no way to reach another tenant's entry.
    ///
    /// On success returns true and hands back the caller's own Director; on failure returns false and the
    /// <see cref="IResult"/> the route must return unchanged:
    ///   - 403 when the request has no bound tenant (deny-by-default, NEVER the Local partition);
    ///   - 404 when the id is not in the caller's tenant (NEVER another tenant's freshest match).
    /// Self-host is unchanged: there the request tenant is always Local and the registry holds the one tenant's
    /// Directors, so this is an ordinary present/absent lookup.
    /// </summary>
    private static bool TryResolveOwnedDirector(
        HttpContext ctx, Tenancy.HostedTenantBoundary? boundary, DirectorRegistry registry, string id,
        out DirectorDto director, out IResult error)
    {
        director = null!;
        var reqTenant = ResolveReadTenant(ctx, boundary);
        if (reqTenant is null)
        {
            error = Results.Json(new { error = "no tenant is bound to this request" },
                statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        var found = registry.Get(reqTenant.Value, id);
        if (found is null)
        {
            error = Results.NotFound(new { error = "director not found" });
            return false;
        }

        director = found;
        error = null!;
        return true;
    }

    /// <summary>
    /// THE route-facing session locator (issue #1869). Every per-session HTTP route resolves its session
    /// through this, and it takes the REQUEST - so the tenant comes from the caller's authenticated device
    /// key and there is no tenant argument for a route to get wrong.
    ///
    /// This exists because the read path was made tenant-aware while the command path was not: twenty-three
    /// per-session routes passed a literal <see cref="TenantId.Local"/> straight into
    /// <see cref="LocateSessionAsync"/>. On hosted, where the request's tenant is a real account, that read
    /// the empty Local partition and returned "session not found across any director" - so prompt, interrupt,
    /// escape, buffer, summary, git, wingman, role, hold and delete ALL 404'd for a correctly enrolled
    /// Director whose sessions the roster was listing perfectly. You could see everything and do nothing, and
    /// because /buffer was among them the terminal view was dead too.
    ///
    /// It was INVISIBLE on self-host, because there the request's tenant genuinely IS Local, so every test and
    /// every developer machine agreed with the bug. Only driving a real Director against the hosted box found
    /// it. That is why this is a separate entry point rather than twenty-three corrected arguments: a fixed
    /// argument can be got wrong again by the next route, and would be just as invisible.
    ///
    /// DENY BY DEFAULT: a request whose device key resolves to no tenant locates NOTHING. It does not fall
    /// back to Local, and the caller gets its route's ordinary not-found answer, which is the truthful one -
    /// a caller with no tenant owns no sessions. The refusal is logged distinctly so it is never confused with
    /// an ordinary miss.
    ///
    /// The <see cref="LocateSessionAsync"/> primitive still takes an explicit tenant. Its remaining callers,
    /// named exactly rather than waved at:
    ///  - the voice sweep in GatewayHost, a background pass with no request, pinned to Local until the voice
    ///    state it mutates is partitioned (converting it first would ARM a cross-tenant audio path, not close
    ///    one);
    ///  - <c>SessionVerbClient.ResolveAsync</c>, also pinned to Local - and it is NOT purely background: it is
    ///    reached from the wingman voice endpoint's request handling, so those voice reads stay inert on
    ///    hosted rather than working. That is deliberate and it is the same precondition as the sweep, but it
    ///    is a REMAINING GAP, not a solved case, and it is booked as its own work;
    ///  - the dictation completion path, DELIBERATELY LEFT LOCAL. Tenant-scoping only its /complete leg was
    ///    tried in this pull request and REMOVED in review: it would have activated a route whose upload
    ///    store is unpartitioned - one global root keyed solely by a caller-supplied upload id, with no
    ///    tenant on the record, and sibling routes (upload, chunk, ack, abandon) that authorize a device but
    ///    never check whose upload it is. Reaching the route is not the boundary; a shared identifier space
    ///    is. Booked as issue #1884, with the same precondition as voice: partition the state first.
    ///
    /// This does NOT make the mistake impossible for the next route, and saying so would overstate it: the
    /// primitive is still internal and a new route could call it with any tenant it liked. What it does is
    /// remove the tenant argument from the path a route would naturally take, and make the omission visible -
    /// review caught a route that had been left on the primitive precisely because a test existed that would
    /// have covered it. Enforcing a route-versus-background split in the type system is follow-up work.
    /// </summary>
    internal static Task<(DirectorDto? director, SessionDto? session)> LocateSessionForRequestAsync(
        HttpContext ctx, Tenancy.HostedTenantBoundary? boundary,
        DirectorRegistry registry, string sid,
        Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale,
        SessionOwnerCache? owners = null)
    {
        var tenant = ResolveReadTenant(ctx, boundary);
        if (tenant is null)
        {
            FileLog.Write($"[GatewayEndpoints] LocateSessionForRequestAsync: DENIED for sid={sid} - the authenticated device key resolves to no tenant, so nothing is located (never the Local partition)");
            return Task.FromResult<(DirectorDto?, SessionDto?)>((null, null));
        }

        return LocateSessionAsync(registry, sid, pushedSessions, streamStale, tenant.Value, owners);
    }

    /// <summary>
    /// THE fold. Resolve every session's role from the WHOLE fleet, then stamp the presentation fold
    /// (EffectiveColor / StateLabel / TriageBucket / NeedsYouSince) onto the response set.
    ///
    /// TWO LISTS, AND THE DIFFERENCE IS THE WHOLE POINT (defect 13). <paramref name="roleUniverse"/> is the
    /// UNFILTERED fleet - every session the Gateway can see. <paramref name="toStamp"/> is only what this
    /// response will return. They differ whenever a caller filters, and the role MUST be resolved from the
    /// universe: "is my controller alive?" is a question about sessions the caller may have filtered out.
    /// Resolving it from the filtered set let `?statusColor=red` drop a WORKING controller out of the
    /// liveness set, reclassify its Worker as Standalone, and un-suppress a red the human should never have
    /// been shown - a worker nagging the human because of a query parameter.
    ///
    /// <paramref name="toStamp"/> entries must APPEAR IN <paramref name="roleUniverse"/> (matched by session
    /// id); references or copies both work, and one that is absent fails loud. This used to require by-
    /// REFERENCE entries, with a copy silently yielding a null SessionRole - see the note at the
    /// FleetRoleResolver.Stamp call below for why that requirement was removed rather than documented.
    ///
    /// INTERNAL, not private, and called by exactly three routes - the roster, /exes/list and
    /// GET /sessions/{sid}. Those three used to fold independently (or not at all), which is how they came
    /// to disagree; there is one implementation because there must only ever be one answer.
    /// </summary>
    internal static void StampFleetRolesAndFold(
        List<SessionDto> roleUniverse,
        IReadOnlyList<SessionDto> toStamp,
        Func<TenantId, string, bool, DateTime?>? needsYouStampFor = null,
        Snooze.SnoozeRegistry? snoozeRegistry = null,
        TenantId? tenant = null,
        Fleet.HandRaiseRegistry? handRaises = null)
    {
        if (roleUniverse is null) throw new ArgumentNullException(nameof(roleUniverse));
        if (toStamp is null) throw new ArgumentNullException(nameof(toStamp));

        // Load-test Stage 0 (issue #1173): time the whole fold pass. This method is the shared hot path -
        // the roster, the display sweep, and every accepted hub push all run it - so its duration under
        // load is one of the numbers the load-test baseline exists to capture.
        var foldStart = Stopwatch.GetTimestamp();

        var all = toStamp;

        // Snooze Length mission: an EXPIRED snooze must read as "needs you" again. The registry is the
        // source of truth for the timer; the cleanest fold (issue #1177 keeps the Gateway the single
        // fold, decision #6) is to override OnHold=false on this aggregated DTO copy BEFORE the color /
        // label / triage are computed, so SessionOrdering.Classify puts the session straight back into
        // NeedsYou with no new classification logic. This is a pure, continuous overlay: while a snooze
        // is expired every read reports the session as un-held, so it never flickers back to "Snoozed"
        // between the moment it expires and the moment its Director confirms the clear. A DEAD Director's
        // session still carries its last-known OnHold=true in the cached roster; this overlay is exactly
        // what surfaces it anyway - the dead-man's-switch.
        // THE GATEWAY OWNS HOLD. The registry is not consulted as an overlay on a Director's answer any
        // more - it IS the answer. Whatever a Director reported in SessionDto.HoldState is overwritten
        // here, unread, because a Director does not decide hold and its copy is a display mirror this
        // Gateway wrote in the first place.
        //
        // This one assignment is what makes every surface agree by construction: the roster, /exes/list
        // and GET /sessions/{sid} all fold here, and the fold is the only place hold is decided. It
        // replaces three workarounds that existed solely because the truth used to live on the Director -
        // a read-time OnHold=false overlay, a tunnel round-trip to ask a Director for its hold, and a
        // nudge-write to beg it to change. All three are gone.
        //
        // An elapsed snooze reads None straight out of HoldStateFor, so "expired" needs no special case:
        // the owner asked for N minutes of quiet and got them. SnoozeExpired is display metadata, not a
        // hold state - it says "this one JUST came back BECAUSE its timer ran out", which the clients render
        // as a distinct "Snooze ended" badge and the phone announces once.
        var nowUtc = DateTime.UtcNow;
        // ONE SET-BASED READ FOR THE WHOLE FOLD (issue #2323, read-model epic #1159). This used to be three
        // database reads PER SESSION - HoldStateFor and IsExpired in the loop just below, and SnoozeUntilFor
        // in the second loop further down - each one taking the registry's process-wide monitor, renting its
        // own pooled context and running its own query. The 31 July load-test baseline measured exactly that
        // (1,032 reads for 30 roster polls plus 13 sweeps over 8 sessions, no remainder) and named that
        // monitor as the resource that gave first, at roughly five concurrent viewers over 800 sessions.
        //
        // BOTH LOOPS READ FROM THIS ONE SNAPSHOT, and that is not tidiness. Batching only the first loop
        // would remove two reads of three and the load test would report a two-thirds improvement that reads
        // like success - the most expensive kind of false green, because it would be published as the
        // headline number. If a future change needs a snooze fact in this method, take it from `holds`.
        //
        // It is also more consistent than what it replaces: the two loops used to read the store at different
        // instants, so a snooze written between them could be visible to one and not the other. One snapshot
        // and one `nowUtc` mean the whole fold answers as of a single moment.
        var holds = snoozeRegistry is null
            ? Snooze.SnoozeHoldSnapshot.Empty
            : snoozeRegistry.HoldSnapshotFor(all.Select(s => s.SessionId));
        if (snoozeRegistry is not null)
            foreach (var s in all)
            {
                if (string.IsNullOrEmpty(s.SessionId)) continue;
                s.HoldState = holds.HoldStateFor(s.SessionId, nowUtc);
                // Expiry is a REGISTRY fact, not a Director one, and it is ASSIGNED both ways every fold -
                // never OR-ed in. The DTO reaching this fold can already carry SnoozeExpired=true (the
                // roster re-serves the store's folded clones), so a one-way "set true when
                // expired" would latch the badge on forever: it never wrote false, so a session that left
                // needs-you by any route OTHER than timer expiry - work deleting the entry (the working
                // edge), a re-snooze arming a fresh clock, an owner turn - kept a stale badge it never
                // earned. Assigning = IsExpired makes the badge mean EXACTLY one thing, both directions:
                // true only while an armed entry's clock has elapsed, false the instant that stops being so.
                s.SnoozeExpired = holds.IsExpired(s.SessionId, nowUtc);
            }

        // Defect 5: the role resolution moved to Fleet.FleetRoleResolver so this roster read and the
        // FleetRoleObserver (which pushes the role down to the owning Director's desktop) share ONE
        // implementation. Two copies would be two authorities, and when they drifted the desktop and the
        // phone would disagree again - which IS defect 5. Behaviour here is unchanged: every branch still
        // assigns, so an inbound role never survives this pass.
        //
        // Defect 13: resolved across the ROLE UNIVERSE, never the filtered response set.
        //
        // This passes BOTH lists, so the resolver stamps toStamp by SESSION ID rather than relying on its
        // entries being the same OBJECTS as the universe's. That by-reference requirement used to be a
        // comment on this method, and a comment is exactly the wrong place for it: an equal-but-copied DTO
        // satisfied the type system, returned a null SessionRole, and folded from it SILENTLY - this
        // mission's own defect shape (a consumer reading a value production never put there), pre-loaded for
        // the next caller. The overload makes it structurally impossible instead: references or copies both
        // work, and a session absent from the universe fails loud.
        Fleet.FleetRoleResolver.Stamp(roleUniverse, all);

        foreach (var s in all)
        {
            var effectiveColor = SessionOrdering.EffectiveColor(s);
            s.EffectiveColor = effectiveColor;
            // The "Dumb Clients" palette slice: resolve the colour NAME to its pixel HEX through the ONE
            // canonical map, right here beside the name, so the /sessions consumers (the web phone and the
            // Cockpit) paint that hex verbatim and carry no name->hex table that can drift. The DESKTOP does
            // NOT receive this hex - it is held to the SAME canonical values at compile time (StatusPalette
            // references SessionColorPalette) and by the agreement tests (approved Fork B: no pushed hex).
            // So this stamp is for the /sessions wire only; the display-state push still carries the name.
            //
            // FAIL LOUD on a name the canonical map does not know. HexFor returns the magenta sentinel for an
            // unknown name, and a valid-looking #FF00FF would otherwise sail through the web unlogged - a
            // silent magenta, the exact class of quiet failure this mission ends. A fold colour the palette
            // does not know is a bug (the fold learned a name nobody taught the palette), so say so here.
            if (!SessionColorPalette.Knows(effectiveColor))
                FileLog.Write($"[GatewayEndpoints] UNKNOWN FOLD COLOUR '{effectiveColor}' for session " +
                              $"{s.SessionId} - not in SessionColorPalette; stamping the magenta BROKEN sentinel. " +
                              "The fold emitted a colour name the canonical palette does not know; see " +
                              "docs/new_architecture/session-state.html.");
            s.EffectiveColorHex = SessionColorPalette.HexFor(effectiveColor);
            s.StateLabel = SessionOrdering.StateLabel(s);
            // Which model this session is running, folded to finished words (issue internal#1340). The raw
            // CurrentModel keeps riding beside it for the statistics dimension; this is the DISPLAY, and it
            // is folded here so no client has to work out for itself whether a missing model means "the
            // first turn has not finished yet" or "this agent can never report one" - two absences that mean
            // opposite things and that four clients would have rendered four ways.
            s.ModelDisplay = ModelDisplayFold.For(s);
            // The words for a prompt that did not go (issue internal#811), folded here beside the colour and
            // the label so every client renders one sentence it did not compose. Null on a session that has
            // not lost anything, which is almost all of them.
            s.PromptDeliveryNotice = SessionOrdering.PromptDeliveryNotice(s);
            // THE ORPHAN'S EXPLANATION (owner's orphan ruling, 2026-07-09), stamped HERE because this is the
            // only place that holds the whole fleet - naming the supervisor means finding it, and it may be
            // a session on another machine entirely. The lookup deliberately searches the ROLE UNIVERSE and
            // not the response set: a caller filtering the roster must not be able to turn "your supervisor
            // exited" into "your supervisor never existed" and lose its name from the sentence.
            //
            // A miss is a real and ordinary answer, not a failure - a supervisor that exited long enough ago
            // has left the roster altogether - so the notice simply drops the name and still explains itself.
            s.SupervisorLostNotice = SessionOrdering.SupervisorLostNotice(
                s,
                string.IsNullOrEmpty(s.ControllerSessionId)
                    ? null
                    : roleUniverse.FirstOrDefault(u => string.Equals(u.SessionId, s.ControllerSessionId, StringComparison.Ordinal)));
            s.TriageBucket = SessionOrdering.Classify(s) switch
            {
                SessionOrdering.TriageBucket.NeedsYou => "needsYou",
                SessionOrdering.TriageBucket.OnHold => "onHold",
                _ => "active",
            };
            if (needsYouStampFor is not null)
            {
                var isRed = string.Equals(effectiveColor, "red", StringComparison.OrdinalIgnoreCase);
                // MTR-10 Gap C: the needs-you clock is partitioned per tenant. The tenant is the OWNING tenant
                // of the fold pass - the roster's request tenant, the display push's ambient tenant. Only the
                // needs-you callers pass it; the dev/diagnostic callers pass no needsYouStampFor and no tenant,
                // so the null-to-Local resolution here is never reached for them.
                s.NeedsYouSince = needsYouStampFor(tenant ?? TenantId.Local, s.SessionId, isRed);
            }
            // The armed-snooze deadline, so a client can show "Snoozed - wakes in Xh". Taken from the SAME
            // snapshot the hold state above came from (the registry is the sole timer owner); null when there
            // is no running clock (no snooze, or a deferred one that has not landed). Folded HERE so the
            // roster, the observer that pushes this down to the desktop, and the single-session read all emit
            // the same deadline.
            //
            // THIS IS THE SECOND LOOP, and it is why the fix had to be a snapshot rather than a batched first
            // loop: this read is a third of the fold's snooze database traffic and it lives here, out of sight
            // of the loop above.
            s.SnoozeUntil = holds.SnoozeUntilFor(s.SessionId);

            // THE RAISED HAND (issue #2662), stamped in the same pass as everything else so every surface
            // gets one answer. Read as "raised AND still working": a worker that has STOPPED has its hand
            // lowered here, by derivation, with no sweep and nothing to remember - stopping already carries
            // its own cue to the supervisor, and a latch somebody had to clear is the one that would still be
            // up next week.
            //
            // NOT read by the colour, the label or the triage bucket, and it must never be. This is a signal
            // between two sessions; giving it a branch in those arms would put a worker's problem back in the
            // owner's queue, which is precisely what the supervised rule exists to stop.
            var raised = handRaises is null || string.IsNullOrEmpty(s.SessionId)
                ? null
                : handRaises.Get(tenant ?? TenantId.Local, s.SessionId);
            s.NeedsManager = raised is not null && SessionOrdering.IsWorkingSession(s);
            s.NeedsManagerReason = s.NeedsManager ? raised!.Reason : null;
        }

        Diagnostics.LoadTestMetrics.FoldDurationMs.RecordSince(foldStart);
    }

    // Gateway Cleanup CUT RESTORATION (SB-4a): map a tunnel command's null-or-failed result to the faithful
    // HTTP status the old REST route returned. A null result (Director not tunnel-connected) is 502; a typed
    // failure preserves the executor's BadRequest/NotFound as 400/404 so the repos/handover-management contract
    // - which the consumers and the re-added tests assert - is byte-identical to the pre-cut REST surface. Any
    // other status collapses to 502. Callers use this only on the not-Ok path (sr is null OR !sr.Ok).
    //
    // Stable Release (v1.3.0), Tier 1 item 1: the two Gateway-synthesized outcomes get their own arms, and both
    // carry the message in the BODY. Without these arms they would fall into the bare collapse below - which
    // sends no body at all - and the explanation the whole item exists to deliver would be silently dropped one
    // line before reaching the caller. The Director-sent statuses above and the collapse below are untouched, so
    // the byte-identical pre-cut contract still holds for every status that existed before.
    // Stable Release (v1.3.0), Tier 1 item 1: the message-preserving twin of MapDirectorFailure, for the many
    // endpoints that answer a failed tunnel command with a BARE 502 carrying no body at all.
    //
    // The router now explains every dropped command, but an endpoint that throws the explanation away leaves the
    // user staring at a naked 502 - so the fix would compute a perfect message nobody ever reads. This carries
    // the body for the three outcomes that have something to say, and changes NOTHING else:
    //   - not connected     -> 502 (unchanged status) + "the Director is offline"
    //   - timed out         -> 504 + the router's message
    //   - dropped mid-flight-> 502 (unchanged status) + the router's message
    //   - any other status  -> the exact bare 502 it returns today, byte-for-byte
    // Deliberately NOT MapDirectorFailure: that maps BadRequest/NotFound to 400/404, and these call sites ship
    // a 502 for those today. Changing that is a contract change and belongs to a different piece of work.
    /// <summary>
    /// The settings legs' failure mapping. <see cref="TunnelFailure"/> words the two GATEWAY-synthesized
    /// outcomes (no tunnel, timeout, mid-command drop) well, so those are handed straight to it. What it does
    /// NOT do is carry a DIRECTOR-sent failure's message: its default branch collapses every one to a bare 502
    /// with no body. That is exactly the trap item 1 paid for - a command that computes a perfect explanation
    /// which no endpoint ever shows a human. The settings verbs return real, actionable messages ("request body
    /// must be a JSON object"; a refused gateway patch saying nothing was written), so these legs map the
    /// Director's own status to its HTTP equivalent and carry its words through to the person who typed the
    /// edit. Scoped to these two routes deliberately: the other legs' bare-502 behaviour is not this item's to
    /// change.
    /// </summary>
    private static IResult DirectorAnswerFailure(DirectorCommandResult? sr, string? machineName)
    {
        if (sr is null
            || sr.Status is DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped)
            return TunnelFailure(sr, machineName);

        var status = sr.Status switch
        {
            DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
            DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
            DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
            DirectorCommandStatus.Locked => StatusCodes.Status423Locked,
            _ => StatusCodes.Status502BadGateway,
        };
        return Results.Json(new { error = sr.Error }, statusCode: status);
    }

    private static IResult TunnelFailure(DirectorCommandResult? sr, string? machineName = null)
    {
        if (sr is null)
        {
            var offline = string.IsNullOrWhiteSpace(machineName)
                ? "The Director is not connected right now, so the command was not delivered."
                : $"The Director on {machineName} is not connected right now, so the command was not delivered.";
            return Results.Json(new { error = offline }, statusCode: StatusCodes.Status502BadGateway);
        }
        return sr.Status switch
        {
            DirectorCommandStatus.Timeout => Results.Json(new { error = sr.Error },
                statusCode: StatusCodes.Status504GatewayTimeout),
            DirectorCommandStatus.TunnelDropped => Results.Json(new { error = sr.Error },
                statusCode: StatusCodes.Status502BadGateway),
            // A DIRECTOR-SENT failure (BadRequest / NotFound / Conflict / Locked / a plain Failed). The
            // Director computed a real explanation and this branch used to drop it on the floor, returning a
            // bodyless 502 - the human got a bare status they could not act on, and the words that would have
            // told them what to do were discarded one hop from being shown.
            //
            // The STATUS stays 502, byte-identical, for every one of these. That is deliberate: these legs
            // ship a 502 for a Director BadRequest/NotFound today, and mapping them to 400/404 (what
            // MapDirectorFailure does) would change a shipped contract. This carries the words and moves
            // nothing else.
            _ => Results.Json(new { error = sr.Error }, statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private static IResult MapDirectorFailure(DirectorCommandResult? sr)
    {
        // The Director is not tunnel-connected. The status stays 502 - no contract moves - but it now says why
        // instead of arriving as a silent bare status the user cannot act on.
        if (sr is null)
            return Results.Json(new { error = "The Director is not connected right now, so the command was not delivered." },
                statusCode: StatusCodes.Status502BadGateway);
        return sr.Status switch
        {
            DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = sr.Error ?? "bad request" }),
            DirectorCommandStatus.NotFound => Results.NotFound(new { error = sr.Error ?? "not found" }),
            DirectorCommandStatus.Timeout => Results.Json(new { error = sr.Error ?? "The Director did not answer in time." },
                statusCode: StatusCodes.Status504GatewayTimeout),
            DirectorCommandStatus.TunnelDropped => Results.Json(new { error = sr.Error ?? "The connection to the Director dropped while the command was being sent." },
                statusCode: StatusCodes.Status502BadGateway),
            // Issue #2190: a Conflict or a Locked is the CALLER's situation, not a broken gateway, and the
            // Director wrote a real sentence for each. Both used to land in the bodyless 502 below, which
            // told the user the gateway was broken and gave a retry-implying 5xx for something retrying
            // cannot fix.
            DirectorCommandStatus.Conflict => Results.Json(new { error = sr.Error ?? "That conflicts with the session's current state." },
                statusCode: StatusCodes.Status409Conflict),
            DirectorCommandStatus.Locked => Results.Json(new { error = sr.Error ?? "This session is on hold." },
                statusCode: StatusCodes.Status423Locked),
            // Anything else really is a server-side failure - but it must still say so in words. A bodyless
            // 502 is exactly the bare status a person cannot act on (issue #2189).
            _ => Results.Json(new { error = sr.Error ?? "The command failed on the machine running this session." },
                statusCode: StatusCodes.Status502BadGateway),
        };
    }

    /// <summary>
    /// Issue #2188/#2189: the ONE answer for "we could not resolve this session", and the reason no route
    /// hand-rolls <c>Results.NotFound(new { error = "session not found across any director" })</c> any more.
    ///
    /// That single line was the whole defect. It was returned for two completely different situations:
    ///  - the session exists and its Director simply has not pushed recently (transient, retryable), and
    ///  - no Director in this tenant has ever pushed this session id (permanent).
    ///
    /// A person who attached an image during a ten-second push gap was told their live session did not
    /// exist, in a message the client then reduced to "error 404". This helper asks the store which of the
    /// two it actually is, and answers accordingly:
    ///  - stale Director  -> 503 + Retry-After, <c>retryable: true</c>, and a sentence that names the delay
    ///                       and tells the user to try again.
    ///  - unknown session -> 404, <c>retryable: false</c>, and a sentence that says the session is gone.
    ///
    /// Both bodies carry <c>error</c>, <c>code</c> and <c>retryable</c>, which is the shape the browser
    /// client reads to build the sentence it shows and to decide whether to retry once on its own.
    /// </summary>
    private static IResult SessionUnavailable(
        HttpContext ctx,
        Tenancy.HostedTenantBoundary? tenantBoundary,
        Streaming.PushedSessionStore? pushedSessions,
        string sid)
    {
        var tenant = ResolveReadTenant(ctx, tenantBoundary);
        var known = tenant is null || pushedSessions is null
            ? null
            : pushedSessions.TryLocateIgnoringFreshness(tenant.Value, sid);

        if (known is not null)
        {
            var (directorId, pushAge) = known.Value;
            var seconds = pushAge == TimeSpan.MaxValue ? -1 : (int)Math.Round(pushAge.TotalSeconds);
            var delay = seconds < 0 ? "in a while" : $"for {seconds} seconds";
            FileLog.Write($"[GatewayEndpoints] session {sid} UNAVAILABLE (retryable): owning director={directorId} "
                + $"has not pushed {delay}; answering 503");
            ctx.Response.Headers.RetryAfter = "5";
            return Results.Json(new
            {
                error = $"The machine running this session has not reported in {delay}. The session is still "
                    + "there - this usually clears within a few seconds. Try again.",
                code = "director_stale",
                retryable = true,
                pushAgeSeconds = seconds,
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        FileLog.Write($"[GatewayEndpoints] session {sid} NOT FOUND: no director in this tenant has pushed it; answering 404");
        return Results.Json(new
        {
            error = "That session could not be found. It may have been closed, or it may belong to a "
                + "machine that is no longer signed in.",
            code = "session_not_found",
            retryable = false,
        }, statusCode: StatusCodes.Status404NotFound);
    }

    // Locate the Director that owns a session. Every session endpoint calls this first,
    // so it fans out to all Directors in parallel rather than scanning them one-by-one:
    // total latency is bounded by the slowest single lookup (~the client timeout) instead
    // of summing one timeout per Director. Exactly one Director should own a given sid.
    // Issue #1229: build the broadcast scope for a session from the Gateway's aggregated view. The
    // group id and repository come from the session record; the machine comes from the owning Director
    // (a Director-local session record leaves MachineName empty). This is the ground truth the Hub keys
    // its who-may-reach-whom decision on - never a role/mission claim carried in the request body.
    private static BroadcastScope BuildBroadcastScope(DirectorDto director, SessionDto session)
    {
        var machine = string.IsNullOrWhiteSpace(session.MachineName) ? (director.MachineName ?? "") : session.MachineName;
        return new BroadcastScope(session.MissionId?.ToString(), session.GroupId, session.RepoPath, machine);
    }

    // Gateway Cleanup mission, Phase 2: the idle-wait poll (single prompt AND fanout broadcast) reads the
    // owning session snapshot / terminal buffer over the tunnel (snapshot / buffer verbs). Tunnel-only: there
    // is no HTTP arm.
    // Shared by both poll sites so there is one tunnel-branch to prove.
    private static async Task<SessionDto?> SnapshotTunnelFirstAsync(
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        DirectorDto director, string sid, CancellationToken ct)
    {
        var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "snapshot", sid, null, ct, machineName: director.MachineName);
        return sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(sr) : null;
    }

    private static async Task<BufferResponse?> BufferTunnelFirstAsync(
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        DirectorDto director, string sid, int lines, long? since, CancellationToken ct)
    {
        var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "buffer", sid,
            new BufferRequest { Lines = lines, Raw = false, Since = since }, ct,
            machineName: director.MachineName);
        return sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<BufferResponse>(sr) : null;
    }

    // Gateway Cleanup mission (post-cut): the pushed stream cache is the ONLY session locator. A Director with
    // no fresh push is not connected to the tunnel, so its sessions are unreachable and location returns null -
    // the same not-found the old HTTP-pull fallback produced when a Director was down. Kept Task-returning so
    // the many `await LocateSessionAsync(...)` call sites (and SessionVerbClient.ResolveAsync) are unchanged.
    /// <summary>
    /// Issue #2188: how much LATER than the roster's freshness cut a session may still be acted on.
    ///
    /// A Director re-pushes its full snapshot every <c>staleAfterSeconds / 2</c>, so ONE missed tick is
    /// ordinary jitter, not an outage - and it must not make a live session unusable. The observed failure
    /// was exactly this: two pushes ten seconds apart went missing, the pushed cache aged past the twenty
    /// second cut, and for about ten seconds every action on fourteen live sessions was refused as
    /// "session not found across any director". The very next prompt, once a push landed, returned 200.
    ///
    /// This is a defined tolerance, NOT a fallback that hides an outage. The tunnel send is still the
    /// authority: if the Director is genuinely gone, <c>DirectorCommandRouter.TrySendAsync</c> returns null
    /// and the existing 502 answers. All this does is stop a one-cycle gap from being reported to the user
    /// as a deleted session.
    ///
    /// Deliberately NOT applied to the roster read (<c>GET /sessions</c>), which no longer withholds anything
    /// for age at all - it serves what it last knew and says how old that is. Acting on a session is allowed to
    /// be more tolerant than the old presentation cut was, because the action itself proves reachability and a
    /// refusal costs the user real work.
    /// </summary>
    internal static readonly TimeSpan LocateGrace =
        TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds / 2.0);

    internal static Task<(DirectorDto? director, SessionDto? session)> LocateSessionAsync(
        DirectorRegistry registry, string sid,
        Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale,
        TenantId tenant,
        SessionOwnerCache? owners = null)
    {
        if (pushedSessions is not null)
        {
            var located = pushedSessions.TryLocate(tenant, sid, streamStale + LocateGrace);
            if (located is not null)
            {
                var (directorId, pushedSession) = located.Value;
                // Issue #1847: resolve the owning Director IN THE SAME TENANT the session was located under.
                // The pushed store is already tenant-scoped, but the registry lookup used to be by bare id, so
                // once the registry could hold that id for more than one tenant, this line could stamp ANOTHER
                // tenant's machine name, operating system user and version onto the session being served here.
                var owner = registry.Get(tenant, directorId);
                if (owner is not null)
                {
                    FileLog.Write($"[GatewayEndpoints] LocateSessionAsync: sid={sid} located=pushed-cache, director={directorId}");
                    owners?.Remember(tenant, sid, directorId);
                    return Task.FromResult<(DirectorDto?, SessionDto?)>((owner, pushedSession));
                }
            }
        }

        FileLog.Write($"[GatewayEndpoints] LocateSessionAsync: sid={sid} not found in the pushed cache (owning Director not connected)");
        return Task.FromResult<(DirectorDto?, SessionDto?)>((null, null));
    }

    // Build the externally-reachable base URL for a Director's web UI.
    //
    // Priority:
    //   1. If the Director registered a TailnetEndpoint that is actually routable
    //      for THIS caller, trust it. A same-machine Director registers a loopback
    //      endpoint (http://127.0.0.1:<port>) which IS its control endpoint but is
    //      useless to a remote caller, so a loopback endpoint is honored only when
    //      the caller is itself on loopback.
    //   2. Else if the caller reached the Gateway over a non-loopback host
    //      (e.g. https://<host>.<tailnet>.ts.net/), mirror that host
    //      and the request scheme onto the Director's own Control API port.
    //      Tailscale Serve maps each Director port to the same number under
    //      HTTPS, so https://<tailnet>:<port>/ resolves correctly.
    //   3. Else fall back to the raw ControlEndpoint (loopback case).
    //
    // Without (2), ViewUrl returns http://127.0.0.1:<port>/... which is
    // unreachable from a phone or any non-loopback client.
    internal static string DeriveDirectorBaseUrl(HttpContext ctx, DirectorDto d)
    {
        var requestHost = ctx.Request.Host.Host;
        var callerIsLoopback = string.IsNullOrEmpty(requestHost)
                         || requestHost == "localhost"
                         || requestHost == "127.0.0.1"
                         || requestHost == "::1";

        // 1. Honor an explicitly registered endpoint, but never feed a loopback
        //    endpoint to a non-loopback caller (that is the phone-gets-127.0.0.1 bug).
        if (!string.IsNullOrEmpty(d.TailnetEndpoint)
            && Uri.TryCreate(d.TailnetEndpoint, UriKind.Absolute, out var tailnetUri)
            && (callerIsLoopback || !tailnetUri.IsLoopback))
        {
            return d.TailnetEndpoint.TrimEnd('/');
        }

        // 2. Remote caller: mirror the public host + scheme onto the Director's port.
        if (!callerIsLoopback
            && Uri.TryCreate(d.ControlEndpoint, UriKind.Absolute, out var controlUri)
            && controlUri.Port > 0)
        {
            return $"{ctx.Request.Scheme}://{requestHost}:{controlUri.Port}";
        }

        return (d.ControlEndpoint ?? "").TrimEnd('/');
    }

    // The Gateway's own externally-reachable base URL, exactly as THIS caller reached
    // it (scheme + host + optional port). It is the root of every session link.
    internal static string DeriveGatewayBaseUrl(HttpContext ctx)
    {
        return $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";
    }

    /// <summary>
    /// The link that opens one session, rooted on THIS GATEWAY'S OWN ORIGIN.
    ///
    /// WHY IT NO LONGER COMES FROM THE DIRECTOR - and this is a reversal, not a refactor. The link
    /// used to be built on a Director's own base URL, taken from its registered tailnet or control
    /// endpoint, because the Director served the session view itself. Phase 5 of the
    /// remove-the-network-port mission deleted that listener and deliberately registers an EMPTY
    /// control endpoint, so the derivation produced an empty base and emitted a RELATIVE
    /// "/sessions/{id}/view?gw=..." - a path with no origin, pointing at a route that no longer
    /// exists anywhere. Independent inspection found it; the aggregation tests hid it by assigning a
    /// fake base URL to the Director before asserting.
    ///
    /// It also follows directly from the standing law that the Gateway owns every ruling and the
    /// client is dumb: where a session can be opened is a verdict, and a verdict is the Gateway's to
    /// make. Deriving it from a Director's self-reported address was the client-side guess wearing a
    /// server-side hat, which is exactly why it kept working right up until the address went away.
    ///
    /// A DIRECTOR-SUPPLIED VALUE IS NOT PREFERRED - IT IS IGNORED. There is no branch that trusts one
    /// when present. A Director old enough to still supply a link supplies one to its own port, which
    /// is a dead door on a current fleet, and the whole point of this mission is that there is one
    /// door. Preferring the Director's value "when it has one" would keep exactly the case that
    /// breaks.
    ///
    /// The route is the Cockpit's canonical one. The Gateway serves /sessions/{id}, which redirects
    /// into the session view - so the link works from a phone, a desktop or a notification without
    /// any client needing to know how the route is spelled.
    /// </summary>
    internal static string GatewaySessionLink(string gatewayBaseUrl, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return "";
        var root = (gatewayBaseUrl ?? "").TrimEnd('/');
        // No origin means no link, rather than a relative path that renders as a working link and
        // resolves against whatever page happens to be showing it. An empty string is an honest
        // "there is nowhere to send you"; "/sessions/abc" is a lie a client will happily follow.
        return root.Length == 0 ? "" : $"{root}/sessions/{sessionId}";
    }


    private static string? ResolveDirectorExe()
    {
        var names = new[] { "cc-director.exe", "cc-director" };

        // 1) Same directory as the running gateway (production: same install dir)
        var gatewayDir = AppContext.BaseDirectory;
        foreach (var name in names)
        {
            var candidate = Path.Combine(gatewayDir, name);
            if (File.Exists(candidate)) return candidate;
        }

        // 2) Dev-build layout: when the gateway is running from
        //    src/CcDirector.Gateway/bin/<config>/<tfm>/, the freshly-built director sits at
        //    src/CcDirector.Avalonia/bin/<config>/<tfm>/cc-director.exe . Walk up four
        //    levels to find a sibling Avalonia/bin/<config>/<tfm>/.
        var dir = new DirectoryInfo(gatewayDir);
        // gatewayDir = .../src/CcDirector.Gateway/bin/<config>/<tfm>/
        // parent[0]  = .../src/CcDirector.Gateway/bin/<config>/
        // parent[1]  = .../src/CcDirector.Gateway/bin/
        // parent[2]  = .../src/CcDirector.Gateway/
        // parent[3]  = .../src/
        if (dir.Parent?.Parent?.Parent?.Parent is { } srcRoot)
        {
            var tfm = dir.Name;
            var cfg = dir.Parent.Name;
            var avaloniaCandidate = Path.Combine(srcRoot.FullName, "CcDirector.Avalonia", "bin", cfg, tfm);
            foreach (var name in names)
            {
                var candidate = Path.Combine(avaloniaCandidate, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3) Standard install location (only used when nothing better was found)
        var bin = CcStorage.Bin();
        foreach (var name in names)
        {
            var candidate = Path.Combine(bin, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    internal sealed record GatewayEvent(string Type, string Id);

    /// <summary>One-line-safe log form of a caller-supplied string (reason fields etc.).</summary>
    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var oneLine = s.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "...";
    }

    /// <summary>
    /// The <c>settings-put</c> verb payload. Mirrors the Director-side <c>SettingsPutRequest</c>: the settings
    /// patch travels as an opaque JSON object under one property, so the command envelope stays well-formed
    /// without the Gateway modelling the Director's config keys.
    /// </summary>
    private sealed class SettingsPutPayload
    {
        public JsonNode? Settings { get; set; }
    }
}
