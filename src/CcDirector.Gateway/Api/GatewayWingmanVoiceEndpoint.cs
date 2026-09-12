using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Services;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Voice;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The wingman-voice surface for the Cockpit's Voice tab (issue #531). Two shapes, both
/// backed by the gateway's one persistent, configured wingman session (the warm brain) via
/// <see cref="WingmanTranslator"/> - never <c>--print</c>:
///
///   POST /sessions/{sid}/wingman/voice-turn  { text }
///        Drive ONE turn of the working session: send the text into the session (on its
///        owning Director), wait for the turn to finish, read the agent's reply, then have
///        the wingman translate it into a faithful, speakable summary. Returns
///        { reply, spoken, replySeconds }. The Voice tab shows the spoken summary silently;
///        the person taps to hear it.
///
///   POST /wingman/ask-direct  { text }
///        The direct-to-wingman path: the person talks to the wingman itself, NOT the
///        working session. Returns { spoken }.
///
/// This is the text-and-voice-shared pipeline: the Text tab and the Voice tab both call
/// /wingman/voice-turn with the person's message (typed, or spoken-then-transcribed), so the
/// text tab proves the translation and the voice tab inherits it unchanged.
/// </summary>
internal static class GatewayWingmanVoiceEndpoint
{
    /// <summary>How long to wait for one working-session turn to finish before giving up.</summary>
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Let the transcript flush after the session goes quiet before reading the reply.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2.5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    // The old per-call TtsMaxChars = 4000 is gone (issue #1612). Its comment claimed "hosted speech
    // endpoints accept bounded input and spoken summaries are short" - both halves are now disproved:
    // the provider accepts 12,000+ characters (measured), our own metered API has no cap at all, and
    // the wingman was writing four-minute essays that the cap was quietly hiding. The runaway guard
    // now lives in Wingman.NarrationText, which tells the listener when it fires.

    /// <summary>
    /// The one HTTP client for the speech leg (issue: hosted-AI client behaviour).
    ///
    /// This used to be <c>using var http = new HttpClient()</c> INSIDE the request handler - a fresh
    /// client, and therefore a fresh connection pool, for every single synthesis. Disposing a client
    /// leaves its socket in TIME_WAIT, so a burst of turns churns through ports and the pool never gets
    /// to reuse a warm TLS connection to the proxy. One shared, never-disposed static is the pattern the
    /// rest of this codebase already uses for hosted calls (AiModelsEndpoint, CarModeChat,
    /// HostedInferenceBrain, ...), and it is safe here because the credential rides on the REQUEST
    /// (TtsSynthesis sets a per-request Authorization header), never on the client's default headers -
    /// so concurrent turns for different keys cannot bleed into each other.
    ///
    /// Timeout is INFINITE on purpose: <see cref="TtsSynthesis"/> owns the deadline (a 15-second
    /// per-attempt cap on a linked CancellationTokenSource, plus one retry). Leaving the client's own
    /// 100-second default in place would create a SECOND, slower deadline authority racing the first -
    /// exactly the kind of ambiguity that made the 2026-07-15 stall so hard to read. One timeout, one
    /// owner. Callers must go through TtsSynthesis, which always supplies the bound.
    /// </summary>
    private static readonly HttpClient SharedTtsHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>413 with a reason the client can show and a support engineer can read.</summary>
    private static IResult TooLarge(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status413PayloadTooLarge);

    // The /wingman/utterance upload family is UN-DENIED and tenant-partitioned (issue #1884): see
    // MapUtteranceRoutes, where each leg is opened through a DictationTenantGate over the voice-turn store.
    // The prior hosted refusal (issue #1896: UtterancePrefix / UtteranceRefusalMessage / UtteranceDenial) is
    // removed because the store is now partitioned by tenant - a hosted account tenant reads only its own
    // base/tenants/<id>/ partition and never the shared root, and a request whose tenant does not resolve is
    // refused (403) at the gate rather than served the shared root.

    /// <summary>
    /// Read a request body into memory, giving up the moment it exceeds <paramref name="max"/>.
    ///
    /// The point is the giving up. <c>CopyToAsync(ms)</c> - what these routes used to do - is unbounded
    /// by construction: it faithfully buffers whatever arrives, so the sender chooses how much of this
    /// machine's memory to use. Here the ceiling is ours: nothing beyond <paramref name="max"/> is ever
    /// retained, and the read stops rather than continuing to drain a body we have already refused.
    ///
    /// Returns null when the body is over the cap (the caller answers 413). Never trusts Content-Length -
    /// that is the client's claim about the body, not the body.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0) return ms.ToArray();
            if (ms.Length + read > max) return null;   // over: stop now, keep nothing
            ms.Write(buffer, 0, read);
        }
    }

    /// Maps the wingman voice surface, including the un-denied, tenant-partitioned /wingman/utterance upload
    /// family (issue #1884): each utterance leg opens the caller's own partition through a
    /// <see cref="DictationTenantGate"/> over the voice-turn store and is refused (403) when no tenant resolves
    /// on hosted. The rest of the voice surface (voice-turn, tts, transcribe, explain, ask, menu) is unchanged.
    public static void Map(
        IEndpointRouteBuilder app,
        DirectorRegistry registry,
        Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> brainProvider,
        KeyVault vault,
        WingmanVoiceService voice,
        TenantSettingsResolver tenantSettings,
        // REQUIRED AND NON-NULLABLE (finding I1-01), and moved AHEAD of the optional tail so it cannot sit
        // in a defaulted position: a forgotten boundary must be a compile error, never a silent default.
        // Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary,
        // One session's stored conversation as the widget list the wingman reads (turn-push mission, phase 3).
        // Null answers "nothing stored yet", which is a wait rather than a failure. The caller enters the
        // tenant's scope and hands over the finished list.
        Func<TenantId, string, History.StoredConversation?>? conversationReader = null,
        Streaming.PushedSessionStore? pushedSessions = null,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = null,
        SessionOwnerCache? owners = null,
        TimeSpan? streamStale = null,
        Func<string>? instructionsProvider = null,
        HttpClient? ttsHttpClient = null,
        TimeSpan? ttsDeadline = null,
        Voice.VoiceUploadStore? uploadStore = null,
        Transcription.TranscriptionHistoryLog? history = null,
        Transcription.TranscriptionAudioArchive? audioArchive = null,
        Transcription.TranscriptStore? transcripts = null,
        // Inspection finding I2-03: the Gateway records what each utterance upload transcribed, so the prompt
        // route can verify a spoken claim instead of trusting a nonblank id. Null means nothing is recorded.
        Voice.SpokenClaimRegistry? spokenClaims = null,
        // The transcription service the utterance and one-shot routes call. Null builds the production one
        // over the vault; a caller (the host, a test) may hand in its own so the routes can be driven end to
        // end against a faked provider - the routes are what is under test, not the provider behind them.
        Transcription.GatewayTranscriptionService? transcriptionService = null)
    {
        // The speech transport: the shared static in production, an injected stub in a test. This is the
        // same seam WingmanVoiceService already exposes for its narration leg (ttsHttpClient) and it
        // exists for the same reason - the upstream base URL is a compile-time const, so without it the
        // status mapping below could only be proven by calling the real provider over the internet.
        var ttsHttp = ttsHttpClient ?? SharedTtsHttp;

        // The account's spoken language comes from the per-tenant resolver this endpoint family already
        // holds, read at CALL time so a change on the Language tab applies to the next spoken answer
        // (issue #1008). Every generator on this translator - narration, direct reply, product help -
        // picks it up from the tenant it already had to have.
        var translator = new WingmanTranslator(
            brainProvider, tenantSettings.SpokenLanguage, instructionsProvider: instructionsProvider);

        // Post-cut: resolve the owning Director once (from the push store) and reach its session verbs
        // (turns / buffer / prompt) through the tunnel-only SessionVerbClient, so the wingman voice surface
        // never HTTP-dials the Director.
        var stale = streamStale ?? TimeSpan.FromSeconds(GatewayConfig.DefaultStreamStaleAfterSeconds);
        Task<SessionVerbClient?> ResolveRouteAsync(TenantId tenant, string sid) =>
            SessionVerbClient.ResolveAsync(sid, tenant, registry, pushedSessions, stale, owners, sendCommand);

        // The session's title, which the wingman speaks before the summary so a listener who cannot
        // see the screen knows which session is talking (WingmanTranslator.FidelityPrompt v5.2). Same
        // push-store read as ResolveRouteAsync above - no dial. Null (unknown session, or no name) is
        // the honest answer and simply means no title is spoken; see GatewayHost.ResolveSessionTitle.
        string? SessionTitle(TenantId tenant, string sid)
        {
            var name = pushedSessions?.TryLocate(tenant, sid, stale)?.Session.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        // The single Gateway owner of speech-to-text (issue #839): both batch transcribe paths below
        // (the resumable /wingman/utterance/complete and the one-shot /wingman/transcribe) go through
        // it, so they resolve the mode + key and pick the hosted endpoint exactly the same way every
        // other batch caller does - no second resolver.
        var transcription = transcriptionService ?? new Transcription.GatewayTranscriptionService(vault, history: history, audioArchive: audioArchive, transcripts: transcripts);

        // Which voice sessions have a ready, playable spoken summary right now (the phone's list
        // shows a play button on these and can play without entering).
        app.MapGet("/wingman/voice/ready", (HttpContext ctx) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(new { sids = voice.ReadySessionIds(reqTenant.Value) });
        });

        // The precomputed spoken summary for a session (instant on entry - no re-read needed).
        app.MapGet("/sessions/{sid}/wingman/voice", (string sid, HttpContext ctx) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var v = voice.Get(reqTenant.Value, sid);
            return v is null
                ? Results.Json(new { ready = false })
                : Results.Json(new { ready = true, spoken = v.Spoken, reply = v.Reply, generatedAt = v.AtUtc });
        });

        // The precomputed audio for a session - streamed so the list can play it with one tap.
        app.MapGet("/sessions/{sid}/wingman/voice/audio", (string sid, HttpContext ctx) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var audio = voice.GetAudio(reqTenant.Value, sid);
            return audio is { Length: > 0 }
                ? Results.Bytes(audio, voice.GetAudioContentType(reqTenant.Value, sid) ?? "audio/mpeg", enableRangeProcessing: true)
                : Results.Json(new { error = "no voice ready for this session" }, statusCode: StatusCodes.Status404NotFound);
        });

        // Turn voice off for a session (issue #859): unmark it as a voice session so the gateway
        // STOPS spending the per-turn Opus translation + hosted text-to-speech on it. This is the
        // counterpart to the marking that POST /sessions/{sid}/wingman/explain performs on entry; the
        // phone's "Turn voice off" calls it alongside the Director's /voice-mode { enabled:false }.
        // Gateway-side only and read-only - it clears the voice marker + cached clip and sends nothing
        // into the session. Idempotent: stopping a session that was not a voice session is a no-op 200.
        app.MapPost("/sessions/{sid}/wingman/voice/stop", (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice/stop sid={sid}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            voice.Unmark(reqTenant.Value, sid);
            return Results.Json(new { stopped = true });
        });

        // IS THIS TENANT IN VOICE MODE? The one place a client asks, instead of deriving it. The client is
        // dumb by law here (CLAUDE.md rule 7): it renders this verdict, it does not compute one. Clients used
        // to answer this question for themselves by scanning the roster for any session that happened to be
        // marked, which is a DIFFERENT question - "is at least one session on voice" is true the moment one
        // session is on, and stays true while nine others are off. That wrong answer is what drove the old
        // one-button switch to offer only OFF forever, so a session created later could never be switched on.
        app.MapGet("/sessions/voice-mode/all", (HttpContext ctx) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(new { enabled = tenantSettings.VoiceModeAll(reqTenant.Value) });
        });

        // Turn voice mode on or off for EVERY session at once (issue #1765). The fleet-wide counterpart
        // to the per-session pair the phone and Cockpit fire (POST /voice-mode on the owning Director,
        // plus mark/unmark here): ONE call, and the Gateway walks the whole roster so the caller never
        // loops it itself. The person's own use is "as I leave the house, put my whole fleet on voice;
        // when I get home, take it all off". Each session gets the SAME two effects the single path gives:
        //   - the owning Director's voice-mode flag is set over the tunnel (ViewMode = Voice, so the roster
        //     flag flips and the session persists as a voice session across navigation), and
        //   - the Gateway voice marker is set (enable) or cleared (disable), so the per-turn narration
        //     starts or stops.
        // A session whose owning computer is not tunnel-connected is SKIPPED and reported, never failing
        // the whole batch - the same "that computer is offline" story the single path tells. The
        // Director-side writes run concurrently (each is its own tunnel round-trip); the Gateway-side
        // mark/unmark is then applied SEQUENTIALLY so the persisted voice-session file is written on one
        // thread and never raced. Idempotent: enabling a session already on, or disabling one already off,
        // is a harmless no-op. This endpoint sends NO prompt into any session - it only sets voice marking,
        // exactly like the single-session /voice-mode and /wingman/voice/stop it fans out to.
        //
        // IT IS ALSO THE SWITCH, not only the fan-out (owner, 2026-07-24). It PERSISTS the tenant's intent
        // first, then fans it out. Without the persisted intent, "the fleet is in voice mode" was a fact
        // nobody held: clients inferred it by checking whether any session happened to be marked, so a
        // session created a minute later was never told and quietly never joined the voice queue - and a
        // client could not honestly tell you which state you were in. The flag is the intent; the fan-out
        // below and the sweep in GatewayHost are how the intent reaches sessions, the ones here now and the
        // ones that appear later.
        app.MapPost("/sessions/voice-mode/all", async (VoiceModeAllRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var enabled = req?.Enabled ?? true;
            FileLog.Write($"[GatewayWingmanVoice] voice-mode/all requested: enabled={enabled}");

            // Persist the intent BEFORE the fan-out. The fan-out can partly fail (an offline computer is
            // skipped), and the intent must survive that - those sessions are meant to be on voice, and the
            // sweep puts them on it when their computer comes back. Recording the intent only on a fully
            // successful fan-out would silently downgrade "my fleet is on voice" to "the reachable part of
            // my fleet is on voice", which is the exact class of quiet gap this change exists to close.
            tenantSettings.SetVoiceModeAll(reqTenant.Value, enabled, DateTime.UtcNow);

            // The machine each Director runs on, so a skipped session names where it lives in plain English.
            // Hosted Multi-Tenancy (audit H1, gap audit-b): scope this to the caller's OWN partition, not the
            // fleet-global ListDirectors(). Two tenants can each own a Director with the SAME id (the registry
            // key is (tenant, id)), so the fleet-global list can hold duplicate DirectorIds and ToDictionary
            // would throw on the collision - a 500 in which one tenant's Director denies another tenant's whole
            // voice-mode/all toggle. It would also map the caller's id to ANOTHER tenant's machine name.
            // ListDirectors(tenant) yields ids unique within the partition (no duplicate-key throw) and only this
            // tenant's machine names - which is all the fan-out below, itself tenant-scoped, ever looks up.
            var machineByDirector = registry.ListDirectors(reqTenant.Value)
                .ToDictionary(d => d.DirectorId, d => d.MachineName, StringComparer.Ordinal);

            // Every session the Gateway can see right now, de-duplicated by id. A session belongs to exactly
            // one Director, but the guard keeps a duplicated roster entry from being toggled (and counted) twice.
            // Hosted Multi-Tenancy: voice-mode/all is a fleet fan-out WRITE (voice family), scoped to the
            // caller's tenant resolved from its authenticated device key - so it toggles only the requester's
            // own sessions, never another tenant's, and a request with no bound tenant is denied above. FleetByDirector
            // itself now builds its Director universe from ListDirectors(reqTenant) (audit H1 Codex residual), so a
            // cross-tenant duplicate id cannot even enter this fold's universe, matching machineByDirector above.
            var byDirector = GatewayEndpoints.FleetByDirector(registry, pushedSessions, stale, reqTenant.Value);
            var targets = new List<(string DirectorId, string Machine, string Sid, string? Name)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (directorId, sessions) in byDirector)
                foreach (var s in sessions)
                    if (!string.IsNullOrEmpty(s.SessionId) && seen.Add(s.SessionId))
                        targets.Add((directorId, machineByDirector.GetValueOrDefault(directorId, ""), s.SessionId, s.Name));

            // Set the Director-side flag on every session concurrently: each is an independent tunnel
            // round-trip, so the batch finishes in the slowest single session's time, not the sum.
            var sends = await Task.WhenAll(targets.Select(async t =>
            {
                var result = await DirectorCommandRouter.TrySendAsync(
                    sendCommand, t.DirectorId, "voice-mode", t.Sid, new { enabled }, ct,
                    machineName: string.IsNullOrEmpty(t.Machine) ? null : t.Machine);
                return (Target: t, Result: result);
            }));

            // Apply the Gateway-side mark/unmark sequentially (one thread writes the persisted set), and
            // build the per-session report so the caller sees exactly what changed and what was skipped.
            var sessionResults = new List<object>(sends.Length);
            var changed = 0;
            foreach (var (t, result) in sends)
            {
                var ok = result is { Ok: true };
                if (ok)
                {
                    if (enabled) voice.Mark(reqTenant.Value, t.Sid); else voice.Unmark(reqTenant.Value, t.Sid);
                    changed++;
                }
                var reason = ok
                    ? null
                    : result is null
                        ? string.IsNullOrEmpty(t.Machine)
                            ? "This session's computer looks offline."
                            : $"{t.Machine} looks offline."
                        : DirectorCommandRouter.DescribeFailure(result);
                sessionResults.Add(new { sessionId = t.Sid, name = t.Name, ok, reason });
            }

            FileLog.Write($"[GatewayWingmanVoice] voice-mode/all done: enabled={enabled}, total={sends.Length}, changed={changed}, skipped={sends.Length - changed}");
            return Results.Json(new
            {
                enabled,
                total = sends.Length,
                changed,
                skipped = sends.Length - changed,
                sessions = sessionResults,
            });
        });

        // Resumable, idempotent piece-by-piece upload store (the same one the native app path uses):
        // chunks land on disk under a stable upload id and survive between retry attempts, so the
        // phone can keep re-sending pieces until the whole recording is through. In production the host
        // owns this instance and passes it; when omitted it defaults to a fresh one over the same root and
        // the same explicitly-named partition. The partition cannot be omitted - the store has no
        // constructor that would choose one silently - so this line states the scope it runs in.
        var uploads = uploadStore
            ?? new VoiceUploadStore(CcDirector.Core.Storage.CcStorage.VoiceTurnUploads(), CcDirector.Core.Tenancy.TenantId.Local);

        // ===== Resumable transcription upload (issue #531: drive-safe, keeps trying) =====
        // The phone records locally (works offline), then ships the recording in pieces here and
        // keeps retrying until every piece lands - no user buttons. When all pieces are in, the
        // assembled audio is transcribed, the validated dictionary correction is applied (the same
        // engine every other surface uses; raw is returned in local mode or on any cleanup error),
        // and the corrected text is returned.
        //   POST   /wingman/utterance/upload                  -> { upload_id }
        //   PUT    /wingman/utterance/{id}/chunk/{i}           -> { ok }   (idempotent)
        //   POST   /wingman/utterance/{id}/complete {total}    -> { transcript } | 409 { missing }
        //
        // UN-DENIED, TENANT-SCOPED (issue #1884). The utterance upload family used to be refused in whole on
        // hosted because the store keyed staged audio and the assembled transcript SOLELY by a caller-chosen
        // upload id under one shared root. It is now PARTITIONED like the dictation family: each of the three
        // legs resolves the request's tenant from the authenticated device key and works only inside that
        // tenant's partition of the voice-turn store, so an upload id from another account simply does not
        // exist here and `complete` can only ever return the caller's own words. Resolution is server-side and
        // FAIL-CLOSED: on hosted a request whose key has no bound tenant is refused (403), never served the
        // shared/Local root. Self-host resolves Local throughout and is byte-identical to before.
        //
        // The three legs are handed a <see cref="DictationTenantGate"/> over the voice-turn store - the same
        // gate the dictation family uses - and NOTHING ELSE. The unscoped store is not in scope inside
        // MapUtteranceRoutes, so a leg cannot be written that touches the shared root: the only way to obtain a
        // store there is TryOpen, which cannot return an unscoped one. The boundary is passed through with the
        // null-forgiving operator because it is optional on this endpoint's test seams; the gate's own
        // resolution refuses on hosted when it is null or not hosted-wired, so a missing boundary is a refusal,
        // never a downgrade to the shared root.
        var utteranceGate = new DictationTenantGate(uploads, tenantBoundary!);
        MapUtteranceRoutes(app, utteranceGate, transcription, spokenClaims);

        // Text-to-speech for the mobile Voice screen + Cockpit: turn the wingman's spoken summary into
        // natural-sounding audio (the browser's own voice is robotic). Returns audio/mpeg bytes the
        // page plays in an <audio> element. DevThrottle routes to the hosted proxy's
        // provider-compatible /audio/speech. The voice is the user's choice (TtsVoiceConfig); a
        // request Voice overrides it. The credential comes from the gateway key vault.
        app.MapPost("/wingman/tts", async (WingmanTtsRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var mode = TranscriptionModeConfig.Get();
            var tts = TranscriptionEndpointResolver.ResolveTts(mode);
            var key = vault.Get(tts.KeyName);
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { error = "no DevThrottle account key configured in the gateway vault - sign in to DevThrottle" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            // Read-aloud of caller-supplied text. It is metered and the caller pays, so there is no
            // product limit here - only the runaway guard, which announces itself when it fires
            // (issue #1612). This used to cut at 4000 characters, silently and mid-word, to satisfy
            // OpenAI's limit long after we stopped calling OpenAI.
            // ONE decision, made by the one decider (issue #1031). The language and the voice arrive together
            // in an utterance this route cannot have built without a language; a caller-supplied voice is an
            // AUDITION (the Language tab offering a voice before it is chosen) and rides through the same
            // factory. Nothing here reads a language setting or picks a voice - that is the whole point.
            //
            // AND THE CALLER'S VOICE IS NO LONGER TAKEN ON TRUST (Gateway audit, finding C2). This route forwards
            // req.Voice into that factory, which is how a French account could be handed af_bella by a stale or
            // hand-written caller and be obeyed - French words in an American voice, audio playing, nothing
            // failing. The factory now refuses a voice belonging to another language, and a refusal is the
            // CALLER'S mistake, so it comes back as a 400 saying which language the voice belongs to rather than
            // as a 500.
            SpokenUtterance requested;
            try
            {
                requested = tenantSettings.Utterance(reqTenant.Value, mode, req.Text, req.Voice);
            }
            catch (ArgumentException ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] tts REFUSED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            var spoken = Wingman.NarrationText.LimitForSpeech(requested, out var wasCut);
            if (wasCut)
                FileLog.Write($"[GatewayWingmanVoice] tts text EXCEEDED {Wingman.NarrationText.MaxChars} chars " +
                              $"({req.Text.Length}) - spoken text cut and the listener told");
            // The ENGINE, resolved on its own from the tenant and the transcription mode. It never sees the
            // language: a language selects a voice inside the one engine, never the engine
            // (devthrottle_internal#547). A caller may name a model explicitly - that is a model choice made
            // by a person, not derived from a language.
            var model = string.IsNullOrWhiteSpace(req.Model) ? tenantSettings.TtsModel(reqTenant.Value, mode) : req.Model.Trim();
            var url = tts.BaseUrl.TrimEnd('/') + "/audio/speech";
            // Time the read-aloud call (request -> done) so its speed is in the log, matching the
            // narration path (WingmanVoiceService) and transcription's transcribeMs.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // READ THE LANGUAGE BEFORE SPEAKING (audit finding C1). This sink consumed only the text, the voice
            // and the length, so an utterance whose language had been forced to null by a route that bypassed the
            // factory still reached synthesis as ordinary audio - the invariant was claimed and never checked
            // where it mattered. Reading it here makes a missing language a loud failure at the sink, before a
            // provider call is billed, and gives the log an ASCII fact about which language was spoken.
            var spokenLanguage = spoken.LanguageCode;
            try
            {
                // Per-attempt deadline derived from the text length + one retry (TtsSynthesis), so a
                // stalled upstream voice worker fails fast and retries instead of freezing the caller,
                // while a legitimately long read still gets the time it actually needs.
                // The client is the shared static (see SharedTtsHttp) - never a per-call one.
                // preferBackup is false here: the silent-primary backup routing (issue devthrottle_internal#405) is driven by
                // the per-session narration path (WingmanVoiceService), which owns the sticky state this
                // interactive read-aloud endpoint does not carry. It still gets the proxy's own failover.
                using var resp = await TtsSynthesis.PostAsync(ttsHttp, url, key, new { model, voice = spoken.Voice, input = spoken.Text, response_format = "mp3" }, spoken.Length, preferBackup: false, ct, ttsDeadline);
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    FileLog.Write($"[GatewayWingmanVoice] tts {mode.ToConfigString()} {status}: {body[..Math.Min(200, body.Length)]}");

                    // Out of credits / monthly cap (issue #939): map to the shared 402 state by code
                    // instead of a generic "text-to-speech returned 402".
                    if (status == HostedAiHttp.PaymentRequired)
                        return HostedAiHttp.PaymentRequiredResult(HostedAiErrorMapper.Map402(body));

                    // A 429 or a 503 is the far end telling the caller to come back later, and it often
                    // says HOW MUCH later. Both used to be flattened into a bare 502 with the hint
                    // dropped on the floor: the cloud proxy deliberately forwards the upstream status and
                    // Retry-After verbatim (it was rewritten for exactly this on 2026-07-15), and this
                    // endpoint then destroyed the evidence one hop later. A client cannot back off
                    // correctly against a status that says "the gateway is broken" when the truth is
                    // "wait four seconds" - it just retries into the same wall.
                    //
                    // This is not the Gateway inventing a policy: it does NOT sleep, retry, or decide
                    // anything here. It passes the far end's own answer through, which is the only layer
                    // that knows when to come back. WingmanVoiceService already honours this header on
                    // the narration leg (via the same RetryAfterHeader); now the endpoint does too, so
                    // the two legs cannot drift.
                    //
                    // KNOWN OVERLAP: this route already answers 503 for "no key in the vault" (above).
                    // An upstream 503 now shares that status. They are distinguishable - the transient
                    // one carries Retry-After and says "rate limited or unavailable", the setup one says
                    // "sign in to DevThrottle" - and a client that reads the body cannot confuse them.
                    // Left as-is deliberately: the setup case arguably wants a different status
                    // entirely, but that is a client-visible contract change and does not belong in a
                    // fix whose whole point is to stop DESTROYING information.
                    if (status is 429 or StatusCodes.Status503ServiceUnavailable)
                    {
                        // Normalized to seconds: RetryAfterHeader accepts both wire forms (a delta and an
                        // HTTP date) and a browser reading this should not have to.
                        if (RetryAfterHeader.Parse(resp.Headers) is { } wait)
                            ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                        FileLog.Write($"[GatewayWingmanVoice] tts {status} passed through" +
                            $"{(ctx.Response.Headers.RetryAfter.Count > 0 ? $" (Retry-After {ctx.Response.Headers.RetryAfter})" : " (no Retry-After hint)")}");
                        return Results.Json(new { error = $"text-to-speech is rate limited or unavailable ({status})" },
                            statusCode: status);
                    }

                    return Results.Json(new { error = $"text-to-speech returned {status}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                // Pass the upstream content type through: speech providers usually return audio/mpeg.
                // may return audio/wav for some models - the browser must be told which so it can play it.
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
                sw.Stop();
                FileLog.Write($"[GatewayWingmanVoice] tts ok: elapsedMs={sw.ElapsedMilliseconds}, provider={mode.ToConfigString()}, chars={spoken.Length}, lang={spokenLanguage}, bytes={bytes.Length}, model={model}, voice={spoken.Voice}, type={contentType}");
                return Results.Bytes(bytes, contentType);
            }
            // TtsSynthesis exhausted its attempts: the worker never answered inside the per-attempt cap.
            // That is a GATEWAY TIMEOUT (504), not a bad gateway (502). The two used to be the same 502,
            // which is the collapse that makes an outage unreadable from the outside: "the upstream
            // returned an error" and "the upstream never answered" want different responses from a
            // client and different answers from support, so they must not share a status.
            catch (TimeoutException ex)
            {
                sw.Stop();
                FileLog.Write($"[GatewayWingmanVoice] tts TIMED OUT (elapsedMs={sw.ElapsedMilliseconds}): {ex.Message}");
                return Results.Json(new { error = "text-to-speech timed out: " + ex.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] tts FAILED: {ex.Message}");
                return Results.Json(new { error = "text-to-speech failed: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/sessions/{sid}/wingman/voice-turn", async (string sid, WingmanVoiceTurnRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}, textLen={req?.Text?.Length ?? 0}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var route = await ResolveRouteAsync(reqTenant.Value, sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            // THE FLOOR INVARIANT (issue #1777). This branch NEVER presses a key into a session. The spoken
            // words are typed as an ordinary prompt ONLY when the live screen is a CONFIDENT plain-text
            // composer: not the alternate screen, the hardware cursor VISIBLE, the cursor within the framed
            // composer's input column span, and NO menu marker or menu-ish structure anywhere on the grid.
            // Every menu, and every uncertain / unreadable / alternate-screen / hidden-cursor screen, types
            // NOTHING and presses NOTHING. (Fully hands-free voice-ANSWERING - the wingman pressing menu keys -
            // is its own later issue with per-agent picker profiles and a screen-version lock; it is not here.)
            var (kind, blockedSpoken) = await ClassifyLiveScreenAtAsync(route, sid, reqTenant.Value, translator, tenantSettings.SpokenLanguage(reqTenant.Value), ct);
            if (kind != WaitingScreenKind.PlainText)
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: FAIL CLOSED - not typing (screen is {kind})");
                return Results.Json(new { reply = "", spoken = blockedSpoken, cannotType = true, lookAtTerminal = true });
            }

            // Close the snapshot-to-send race (issue #1777, floor rescope, finding 2): RE-READ the live screen
            // immediately before the send and re-confirm it is STILL a confident plain-text composer. If it
            // changed in the meantime (now a menu / alternate screen / cursor hidden / gone), fail closed - the
            // spoken words must never land on anything but the composer they were classified against.
            var (kindNow, blockedNow) = await ClassifyLiveScreenAtAsync(route, sid, reqTenant.Value, translator, tenantSettings.SpokenLanguage(reqTenant.Value), ct);
            if (kindNow != WaitingScreenKind.PlainText)
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: screen changed before send (now {kindNow}) - fail closed, not typing");
                return Results.Json(new { reply = "", spoken = blockedNow, cannotType = true, lookAtTerminal = true });
            }

            // We are about to start a new turn, so the cached spoken summary + audio are now stale.
            // Clear them DETERMINISTICALLY here (do not rely on observing the Working state, which is
            // racy for fast turns) - the list stops showing it ready and nothing stale plays. The
            // fresh summary is stored below once the agent replies.
            voice.OnSessionWorking(reqTenant.Value, sid);

            // Snapshot the widget list BEFORE sending: gives both (a) the count for the issue #366
            // guard (only read widgets that are new after the send) and (b) the prior conversation
            // for the wingman so it can resolve references in the agent's reply. The current question
            // is appended below; BuildRecentContext is called on the pre-send snapshot so it
            // excludes the new turn and includes only what came before.
            var snapshot = conversationReader?.Invoke(reqTenant.Value, sid)?.Widgets;
            var snapshotWidgets = snapshot ?? new List<TurnWidgetDto>();
            // NULL MEANS "NO BASELINE", NOT "ZERO". If nothing is stored for this session yet, treating the
            // baseline as zero would let the whole conversation arrive a moment later and hand back an OLD
            // agent reply as the answer to the question just asked - which is issue #366 returning by a new
            // route, and is the worst thing this path can do: the person hears a confident answer to a
            // question they did not ask. The wait adopts its baseline from the first conversation it
            // actually sees instead (found in review).
            int? widgetsBefore = snapshot?.Count;
            var priorContext = WingmanTranslator.BuildRecentContext(snapshotWidgets);

            var (ok, _, sendErr) = await route.PostPromptAsync(sid, new PromptRequest { Text = req.Text, AppendEnter = true }, ct);
            if (!ok)
                return Results.Json(new { error = "send failed: " + sendErr }, statusCode: StatusCodes.Status502BadGateway);

            var reply = await WaitForReplyAsync(
                () => conversationReader?.Invoke(reqTenant.Value, sid)?.Widgets,
                async () => await ResolveRouteAsync(reqTenant.Value, sid) is not null,
                sid, widgetsBefore, ct);
            if (string.IsNullOrWhiteSpace(reply))
                return Results.Json(new { error = "the agent did not produce a reply in time" }, statusCode: StatusCodes.Status504GatewayTimeout);

            // The agent replied; now the wingman translates it. This is gateway-owned work
            // (CancellationToken.None) so navigating away does not lose the summary, and the
            // session shows YELLOW while the wingman runs, then back to red (issue #531 voice mode).
            voice.BeginGenerating(reqTenant.Value, sid);
            try
            {
                // Full context: prior exchanges from the pre-send snapshot + the current question,
                // so the wingman can resolve references like "that file" or "the bug I mentioned".
                var recentContext = string.IsNullOrWhiteSpace(priorContext)
                    ? "You: " + req.Text.Trim()
                    : priorContext + "\n\nYou: " + req.Text.Trim();
                var t = await translator.TranslateAsync(reqTenant.Value, recentContext, reply, SessionTitle(reqTenant.Value, sid), ct: CancellationToken.None);
                await voice.StoreSpokenAsync(reqTenant.Value, sid, t.Spoken, reply, CancellationToken.None);   // make it a voice session + cache audio
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: replyLen={reply.Length}, spokenLen={t.Spoken.Length}");
                return Results.Json(new { reply, spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid} translate FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman translation failed: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            finally { voice.EndGenerating(reqTenant.Value, sid); }
        });

        // Transcription (issue #531 follow-up): the phone records audio locally (survives a bad
        // connection / reload), then ships the recording here to be transcribed - the same
        // record-then-ship-then-transcribe shape the native mobile app uses. Robustness lives on
        // the CLIENT (save-first + retry the upload); this endpoint is the single transcribe step.
        // Audio arrives as a multipart 'audio' file; the raw transcript then runs through the
        // validated dictionary correction (raw is returned in local mode or on any cleanup error).
        // Returns { transcript }.
        app.MapPost("/wingman/transcribe", async (HttpContext ctx, CancellationToken ct) =>
        {
            // THE REQUESTING TENANT IS RESOLVED FIRST, AND NULL IS A REFUSAL. This is the contract
            // GatewayEndpoints.ResolveReadTenant states in its own documentation: on hosted, a key with no
            // bound tenant - and a boundary that is not hosted-wired - resolve to null, and null means the
            // caller is DENIED, never served the Local partition.
            //
            // This route used to pass that nullable STRAIGHT into TranscribeAsync, which is where the refusal
            // became the self-host tenant three layers down. All three of GatewayTranscriptionService's
            // null-means-Local substitutions then fired on a hosted request: CleanupCoreAsync read the SHARED
            // FLAT GLOSSARY, so another account's terms could alter their words; RecordHistory took the
            // injected shared history; and the transcript store wrote their transcript evidence into the Local
            // partition. Refusing here is also the first defect fixed - an unbound hosted caller should get a
            // refusal, not a transcription.
            //
            // Same shape and the same body as every sibling route on this surface (and as
            // GatewayDictationEndpoint.NoTenantResult): no new error shape is invented. Self-host is unchanged -
            // there ResolveReadTenant answers TenantId.Local for every authenticated caller, exactly as the
            // nullable did before.
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { error = "send the recording as multipart form-data with an 'audio' file" },
                    statusCode: StatusCodes.Status400BadRequest);

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.Json(new { error = "no audio in the upload" }, statusCode: StatusCodes.Status400BadRequest);

            // Bound the clip before it is copied into memory below. This route has no resume and buffers
            // the file whole, so it is the wrong door for a long recording - the resumable chunk path is
            // the right one, and saying so beats quietly allocating whatever was sent.
            if (file.Length > VoiceUploadLimits.MaxOneShotFileBytes)
            {
                FileLog.Write($"[GatewayWingmanVoice] transcribe rejected: {file.Length} bytes > {VoiceUploadLimits.MaxOneShotFileBytes} cap");
                return TooLarge($"audio is {file.Length} bytes; the limit for this endpoint is " +
                    $"{VoiceUploadLimits.MaxOneShotFileBytes}. Use the resumable upload for long recordings.");
            }

            // Both modes (byo/devthrottle) require the configured key to be present in the vault
            // (issue #887). The single transcription owner resolves this and runs the right provider.
            var routing = transcription.Resolve();
            if (routing.Key is null)
                return Results.Json(new { error = $"no key configured for transcription mode {routing.Mode.ToConfigString()}" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            byte[] bytes;
            using (var ms = new MemoryStream()) { await file.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
            var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "audio.webm" : file.FileName;
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

            // Transcribe WITH the validated dictionary correction applied (the SAME engine every other
            // surface uses; fails open to the raw transcript in local mode or on any cleanup error).
            var result = await transcription.TranscribeAsync(bytes, fileName, contentType, applyCorrection: true, ct,
                tenant: reqTenant.Value, source: "voice");
            // Out of credits / monthly cap (issue #939): map to the shared 402 state (branch by code)
            // instead of flattening it into a generic 502 - so the client shows the consistent
            // add-credits message and keeps the recording, not "transcription failed".
            if (result.Outcome == Transcription.TranscriptionOutcome.OutOfCredits)
                return HostedAiHttp.PaymentRequiredResult(HostedAiErrorMapper.MapCode(result.Code));
            if (result.Outcome != Transcription.TranscriptionOutcome.Ok)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(new { transcript = result.Text });
        });

        // Read-only "explain what's happening" (issue #531): the wingman reads the session's
        // LAST completed turn and speaks a faithful summary of it - WITHOUT sending anything into
        // the session. This is what the mobile Voice screen fires the moment you open a session,
        // so you get a spoken summary even though a normal (text) session never produced voice.
        app.MapPost("/sessions/{sid}/wingman/explain", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

            // NO LONGER 404 WHEN THE DIRECTOR IS UNREACHABLE. This route reads the conversation from the
            // Gateway's own store, so a stored conversation can be narrated whether or not the machine that
            // produced it is currently reachable - refusing here would have kept the old dependency alive in
            // the one place a person presses a button to escape it (found in review). The route is resolved
            // only so a session this Gateway has never heard of is still an honest 404.
            var route = await ResolveRouteAsync(reqTenant.Value, sid);

            voice.Mark(reqTenant.Value, sid);   // opening voice on a session makes it a voice session (kept fresh on turn-end)

            // THE CONVERSATION COMES FROM THE GATEWAY'S OWN STORE (turn-push mission, phase 3). This route is
            // the button a person presses when a narration has not appeared, so what it can honestly say
            // matters more here than anywhere: it used to send a command down the tunnel asking the Director
            // to re-read the transcript, and a failed read there arrived as a SUCCESS with no widgets, which
            // is how this route came to tell people "this session has not produced anything to summarize yet"
            // about sessions that had said plenty (issue #2561).
            //
            // There is no read to fail any more. Either the words are stored or they have not arrived yet,
            // and the second is a wait on the Director rather than anything voice can fix by trying harder.
            var stored = conversationReader?.Invoke(reqTenant.Value, sid);
            if (stored is null && route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);
            if (stored is { IsSupported: false })
            {
                // Terminal, and the person pressed a button to find out: say the thing that is true rather
                // than promising another try that cannot help.
                voice.NoteReadFailed(reqTenant.Value, sid, HostedAiState.Unavailable);
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}: this agent keeps no conversation that can be read - TERMINAL.");
                return Results.Json(new
                {
                    reply = "",
                    spoken = "This agent does not keep a conversation I can read back to you.",
                    replySeconds = 0.0,
                    retrying = false,
                });
            }
            var widgets = stored?.Widgets;
            if (widgets is null)
            {
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}: no conversation stored yet - the Director has not pushed it. Not a read failure, and not a statement about the session.");
                return Results.Json(new
                {
                    reply = "",
                    spoken = "This session's conversation has not reached here yet. It should arrive in a moment.",
                    replySeconds = 0.0,
                    retrying = true,
                });
            }

            // The read answered: clear whatever the LAST failed read recorded, so a stale retry verdict does
            // not go on masking the honest result below. VoiceDisplayFold consults the unavailable state
            // before nothingToNarrate, so without this an explain that failed once and then succeeded onto an
            // empty turn kept reporting "voice on its way" forever (found in review). Only the read's own
            // fact is cleared - the model and speech states clear where they were set.
            voice.ClearReadFailed(reqTenant.Value, sid);

            // An older agent reply is not an answer to a later user message. In that shape, read the live
            // terminal and let a positively classified failure become the narration source. The session is
            // otherwise still an ordinary wait, and the older reply stays silent.
            ScreenGridResponse? screenGrid = null;
            if (route is not null && WingmanNarrationSource.NeedsLiveScreen(widgets))
            {
                try { screenGrid = await route.GetScreenGridAsync(sid, CancellationToken.None); }
                catch (Exception ex) { FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}: terminal screen read failed: {ex.Message}"); }
            }
            var source = WingmanNarrationSource.Select(
                widgets,
                screenGrid is { HasGrid: true } ? screenGrid.Rows : null);
            var recentContext = source?.Kind == WingmanNarrationSourceKind.AgentReply
                ? WingmanTranslator.BuildRecentContext(widgets)
                : "";

            if (source is null)
            {
                // No current reply and no recognized terminal failure. Record the honest wait and return a
                // truthful canned line without calling the model.
                voice.SetNothingToNarrate(reqTenant.Value, sid, true);
                return Results.Json(new
                {
                    reply = "",
                    spoken = "This session has not produced anything to summarize yet. Ask it something and I will read the answer back to you.",
                    replySeconds = 0.0,
                    nothingYet = true,
                });
            }

            // The GATEWAY owns this work, not the page (issue #531 voice mode): run the translation
            // and synthesis on CancellationToken.None so it COMPLETES and caches even if the phone
            // navigates away or the request is abandoned mid-read - returning to the session then
            // loads the finished summary from cache instead of losing it. Mark the session generating
            // so it shows YELLOW ("not ready yet") for the duration, then back to red.
            voice.SetNothingToNarrate(reqTenant.Value, sid, false);   // there IS a text reply - clear any stale "nothing to narrate"
            voice.BeginGenerating(reqTenant.Value, sid);
            try
            {
                var t = source.Kind == WingmanNarrationSourceKind.TerminalFailure
                    ? await translator.TranslateTerminalFailureAsync(
                        reqTenant.Value, source.Content, SessionTitle(reqTenant.Value, sid), CancellationToken.None)
                    : await translator.TranslateAsync(
                        reqTenant.Value, recentContext, source.Content, SessionTitle(reqTenant.Value, sid), ct: CancellationToken.None);
                await voice.StoreSpokenAsync(
                    reqTenant.Value, sid, t.Spoken, source.Content, CancellationToken.None,
                    sourceIdentity: source.Identity);   // cache spoken + audio, ready to play
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}: source={source.Kind}, sourceLen={source.Content.Length}, spokenLen={t.Spoken.Length}");
                return Results.Json(new { reply = source.Content, spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex) when (ex is TimeoutException or HttpRequestException)
            {
                // The model leg did not answer in time (bounded timeout) or the transport failed. This is
                // the absence of an answer, not evidence the session's computer is offline - so record the
                // calm Retrying state (the phone shows "voice on its way" and the sweep keeps trying) and
                // return a benign 200, NOT the 502 the phone used to mislabel "this session's computer looks
                // offline". Before this, a stalled model hung the request the full 180s and then 502'd, which
                // is exactly the "I hit generate and nothing happens" the owner reported.
                voice.NoteRetrying(reqTenant.Value, sid);
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid} model did not answer: {ex.Message} - Retrying (audio on its way)");
                return Results.Json(new
                {
                    reply = "",
                    spoken = "Voice is taking a moment - it will keep trying.",
                    replySeconds = 0.0,
                    retrying = true,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid} FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman could not summarize: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            finally { voice.EndGenerating(reqTenant.Value, sid); }
        });

        app.MapPost("/wingman/ask-direct", async (WingmanVoiceTurnRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] ask-direct textLen={req?.Text?.Length ?? 0}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var t = await translator.AskDirectAsync(reqTenant.Value, req.Text, ct);
                return Results.Json(new { spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] ask-direct FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // The DevThrottle product/docs Q&A path (issue #472): the Cockpit Learning page posts a
        // free-text question ABOUT THE PRODUCT here and the warm brain answers it, grounded in a
        // DevThrottle system prompt. The Cockpit talks only to the Gateway, never a Director - this
        // is that Gateway endpoint. Same warm brain as ask-direct, different grounding.
        app.MapPost("/wingman/ask-devthrottle", async (WingmanVoiceTurnRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle textLen={req?.Text?.Length ?? 0}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var t = await translator.AskAboutDevThrottleAsync(reqTenant.Value, req.Text, ct);
                FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle OK: answerLen={t.Spoken.Length}, replySeconds={t.ReplySeconds:F1}");
                return Results.Json(new { spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Menu handling (issue #531): is the agent showing an on-screen menu right now, and what are
        // the options? The phone reads this on entry to render pressable option buttons and speak the
        // choices. { isMenu, question, spoken, selectionMode, submit, options:[{key,send,note,recommended}] }.
        app.MapGet("/sessions/{sid}/wingman/menu", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] menu sid={sid}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            var route = await ResolveRouteAsync(reqTenant.Value, sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);
            var menu = await DetectMenuAtAsync(reqTenant.Value, route, translator, sid, tenantSettings.SpokenLanguage(reqTenant.Value), ct);
            return Results.Json(MenuJson(menu));
        });

        // Phase 1 menu handling (issue #2193): the CHEAP "what is this session waiting on right now?" read.
        // Pure - one tunnel screen-grid read and the no-brain classifier, NO model call and no cost - which is
        // what makes it usable before every spoken reply. The richer GET /wingman/menu above cannot serve this
        // purpose: it calls the brain to extract option labels, which is both slow and billable.
        // Returns { kind: "menu"|"text"|"blocked", canType, spoken, message }.
        app.MapGet("/sessions/{sid}/wingman/waiting-screen", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            var route = await ResolveRouteAsync(reqTenant.Value, sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            var kind = await WaitingScreenReader.ClassifyAsync(route, sid, ct);
            // The pure classifier only TRIPS the menu question (issue devthrottle_internal#1195) - the
            // verdict that reaches a client comes from the model, served from the per-screen verdict cache
            // when the screen has not changed since the turn was narrated. A model "not a menu" downgrades
            // to Blocked, which keeps canType=true: typing stays allowed, no menu is announced.
            if (kind == WaitingScreenKind.Menu
                && !await WaitingScreenReader.ConfirmedMenuAsync(route, sid, reqTenant.Value, translator, ct))
                kind = WaitingScreenKind.Blocked;
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen(pure) sid={sid}: {kind}");
            return Results.Json(new
            {
                kind = WaitingScreenReader.KindWord(kind),
                // Phase 1 blocks ONLY on a confidently-recognized menu, so an unreadable screen still reports
                // canType - it keeps behaving exactly as it did before this guard existed.
                canType = kind != WaitingScreenKind.Menu,
                spoken = kind == WaitingScreenKind.Menu
                    ? SpokenPhrases.WaitingScreenMenu.In(tenantSettings.SpokenLanguage(reqTenant.Value))
                    : "",
                // The language those words are in (issue #1031), sent as a fact beside them: the client speaks
                // this notice with the BROWSER's own voice and cannot build an utterance without a language.
                spokenLanguage = tenantSettings.SpokenLanguage(reqTenant.Value).Code,
                message = kind == WaitingScreenKind.Menu ? WaitingScreenReader.MenuMessage : "",
            });
        });

        // NOTE (issue #1777, floor rescope): the raw POST /wingman/menu-press endpoint - which pressed menu
        // keystrokes into a session - was REMOVED. Nothing on this branch ever presses a key into a session.
        // Pressing menu options (fully hands-free voice-answering) is its own later issue, built with per-agent
        // picker profiles and a screen-version lock on every keypress. The GET /wingman/menu detection above
        // stays (read-only, for the announce step); it never leads to a keypress.

    }

    /// <summary>
    /// The three /wingman/utterance upload legs, full paths on the ungrouped builder now that the family is
    /// un-denied (issue #1884). Each leg opens the CALLER'S OWN partition through
    /// <see cref="DictationTenantGate"/> and works only inside it: the out-store is named <c>uploads</c> so the
    /// existing per-leg body (size caps, staging, assemble) is byte-identical, but it is now a tenant-scoped
    /// view rather than the shared root. A request whose tenant does not resolve is refused (403) by the gate
    /// before any leg body runs - on hosted that means no bound tenant, never a downgrade to Local.
    /// </summary>
    private static void MapUtteranceRoutes(IEndpointRouteBuilder app, DictationTenantGate gate,
        Transcription.GatewayTranscriptionService transcription, Voice.SpokenClaimRegistry? spokenClaims)
    {
        app.MapPost("/wingman/utterance/upload", (HttpContext ctx) =>
        {
            if (!gate.TryOpen(ctx, out var uploads, out _, out var deny)) return deny;
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var id = uploads.Register(string.IsNullOrWhiteSpace(key) ? null : key);
            return Results.Json(new { upload_id = id });
        });

        app.MapPut("/wingman/utterance/{uploadId}/chunk/{index:int}", async (string uploadId, int index, HttpContext ctx, CancellationToken ct) =>
        {
            if (!gate.TryOpen(ctx, out var uploads, out _, out var deny)) return deny;
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            // Refuse an oversized chunk BEFORE reading a byte of it, when the client declares its size.
            // This is the cheap door: no allocation, no read, and the client learns immediately.
            if (ctx.Request.ContentLength is { } declared && declared > VoiceUploadLimits.MaxChunkBytes)
            {
                FileLog.Write($"[GatewayWingmanVoice] chunk rejected: declared {declared} bytes > {VoiceUploadLimits.MaxChunkBytes} cap (upload={uploadId}, index={index})");
                return TooLarge($"chunk is {declared} bytes; the limit is {VoiceUploadLimits.MaxChunkBytes}");
            }

            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();

            // Content-Length is the client's CLAIM. It can be absent (chunked transfer-encoding) and it
            // can be wrong, so the read itself is bounded too: this stops the moment the body exceeds
            // the cap instead of faithfully buffering however much someone chooses to send.
            var bytes = await ReadBoundedAsync(ctx.Request.Body, VoiceUploadLimits.MaxChunkBytes, ct);
            if (bytes is null)
            {
                FileLog.Write($"[GatewayWingmanVoice] chunk rejected: body exceeded the {VoiceUploadLimits.MaxChunkBytes}-byte cap (upload={uploadId}, index={index})");
                return TooLarge($"chunk exceeded the {VoiceUploadLimits.MaxChunkBytes}-byte limit");
            }

            // Total across the whole upload. Excluding THIS index is what keeps a resend free: the
            // client's retry replaces chunk N, it does not add a second copy of it, and retrying is the
            // normal case on this path.
            var staged = uploads.StagedBytes(uploadId, excludeIndex: index);
            if (staged + bytes.Length > VoiceUploadLimits.MaxTotalUploadBytes)
            {
                FileLog.Write($"[GatewayWingmanVoice] chunk rejected: upload total would be {staged + bytes.Length} > {VoiceUploadLimits.MaxTotalUploadBytes} cap (upload={uploadId})");
                return TooLarge($"upload would exceed the {VoiceUploadLimits.MaxTotalUploadBytes}-byte total limit");
            }

            try { await uploads.StoreChunkAsync(uploadId, index, bytes, string.IsNullOrEmpty(sha) ? null : sha, ct); return Results.Json(new { ok = true, index }); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }
        });

        app.MapPost("/wingman/utterance/{uploadId}/complete", async (string uploadId, UtteranceCompleteRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            if (!gate.TryOpen(ctx, out var uploads, out var reqTenant, out var deny)) return deny;
            if (req is null || req.TotalChunks <= 0)
                return Results.Json(new { error = "totalChunks (>0) is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            // The configured mode's key must be present (issue #887: both modes are key-bearing). The
            // single transcription owner resolves this; check it BEFORE assembling so a no-key request
            // does not pay the reassembly cost.
            var routing = transcription.Resolve();
            if (routing.Key is null)
                return Results.Json(new { error = $"no key configured for transcription mode {routing.Mode.ToConfigString()}" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            AssembleResult assembled;
            try { assembled = await uploads.AssembleAsync(uploadId, req.TotalChunks, ct); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }

            if (assembled.Status == "unknown_upload")
                return Results.Json(new { error = "unknown upload id" }, statusCode: StatusCodes.Status404NotFound);
            if (assembled.Status == "incomplete")
                return Results.Json(new { status = "incomplete", missing = assembled.Missing }, statusCode: StatusCodes.Status409Conflict);

            var assembledAudio = assembled.Audio;
            if (assembledAudio is null || assembledAudio.Length == 0)
            {
                uploads.Delete(uploadId);
                return Results.Json(new { error = "assembled recording was empty" }, statusCode: StatusCodes.Status502BadGateway);
            }

            // Transcribe through the single owner WITH the validated dictionary correction applied (the
            // SAME engine every other surface uses; fails open to raw in local mode or on any error).
            var result = await transcription.TranscribeAsync(
                assembledAudio, "audio." + (req.Ext ?? "webm"), req.Mime ?? "audio/webm", applyCorrection: true, ct,
                tenant: reqTenant, source: "voice");
            uploads.Delete(uploadId);
            // Out of credits / monthly cap (issue #939): map to the shared 402 state (branch by code)
            // instead of flattening it into a generic 502 - so the client shows the consistent
            // add-credits message and keeps the recording, not "transcription failed".
            if (result.Outcome == Transcription.TranscriptionOutcome.OutOfCredits)
                return HostedAiHttp.PaymentRequiredResult(HostedAiErrorMapper.MapCode(result.Code));
            if (result.Outcome != Transcription.TranscriptionOutcome.Ok)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            FileLog.Write($"[GatewayWingmanVoice] utterance complete {uploadId}: mode={result.Mode}, chars={result.Text?.Length ?? 0}");

            // Capture-health persistence (issue #863): when the mobile dialog sent its measurements,
            // record the audio-loss deficit (recording wall-clock vs decoded audio duration) into the
            // same dictation session log the desktop and overlay write, tagged Source="mobile". The
            // assembled WAV byte count is the audio the server actually transcribed. Fire-and-forget.
            PersistMobileCaptureHealth(uploadId, req, assembledAudio.Length, result.Text);
            // The id the client will hand back as its spoken claim is now bound, for THIS tenant, to exactly
            // these words, once (inspection finding I2-03). The prompt route spends the claim; a replay, a
            // different text, or another account's prompt finds nothing to spend.
            spokenClaims?.Register(reqTenant, uploadId, result.Text ?? "");
            return Results.Json(new { transcript = result.Text });
        });
    }

    /// <summary>
    /// Classify the live waiting screen for the FLOOR (issue #1777): read the grid over the tunnel and run the
    /// pure, no-brain <see cref="WaitingScreenClassifier"/>. Returns the kind and, when it is not typeable, the
    /// line to speak. This is all voice-turn needs - it types only on <see cref="WaitingScreenKind.PlainText"/>
    /// and never presses - so no menu extraction (brain) happens on the send path.
    /// </summary>
    private static async Task<(WaitingScreenKind Kind, string BlockedSpoken)> ClassifyLiveScreenAtAsync(
        SessionVerbClient route, string sid, TenantId tenant, WingmanTranslator translator, SpokenLanguage language, CancellationToken ct)
    {
        // The read-and-classify step itself lives in ONE place (issue #2193) so this endpoint, the prompt
        // front door's menu guard, and the narration generator can never drift on what counts as a menu.
        // The WORDING stays here, because this path fails closed on an unreadable screen too and therefore
        // has a second thing to say; the guard refuses only on a menu and has one.
        var kind = await WaitingScreenReader.ClassifyAsync(route, sid, ct);
        // A MENU claim is the model's to make (issue devthrottle_internal#1195): the classifier trips the
        // question, the confirmed verdict decides what is SPOKEN. An unconfirmed "menu" downgrades to
        // Blocked - still not typed (this path types only on PlainText), but the person is told the honest
        // "I can't read this screen" line instead of being sent to look for a menu that is not there.
        if (kind == WaitingScreenKind.Menu
            && !await WaitingScreenReader.ConfirmedMenuAsync(route, sid, tenant, translator, ct))
            kind = WaitingScreenKind.Blocked;
        // A menu says "look at the terminal"; everything else uncertain says the generic unreadable line.
        var spoken = kind == WaitingScreenKind.Menu
            ? SpokenPhrases.VoiceTurnBlockedMenu.In(language)
            : SpokenPhrases.VoiceTurnBlockedUnreadable.In(language);
        return (kind, spoken);
    }

    /// <summary>What a session's live waiting screen turns out to be (issue #1777), so voice-turn can decide
    /// whether the person's spoken words may be typed. Only <see cref="PlainText"/> may be typed.</summary>
    private enum WaitingKind
    {
        /// <summary>A readable on-screen menu with pressable options - map the words to a choice and press.</summary>
        Menu,

        /// <summary>A confident plain-text prompt (a readable screen that is not a menu) - typing is safe.</summary>
        PlainText,

        /// <summary>A menu we could not read, an uncertain screen, or an unreadable one - NEVER type; fail closed.</summary>
        Blocked,
    }

    /// <summary>The classified live waiting screen for the read-only <c>GET /wingman/menu</c> path: for a menu,
    /// the extracted options; for a blocked screen, the plain-English reason and the line to speak. (The send
    /// path does not use this - it classifies with the pure classifier and only ever types.)</summary>
    private sealed class WaitingScreen
    {
        public WaitingKind Kind { get; init; }
        public WingmanMenu Menu { get; init; } = new() { IsMenu = false };
        public string BlockedReason { get; init; } = "";
        public string BlockedSpoken { get; init; } = "";
    }

    private static WaitingScreen Blocked(string reason, string spoken) =>
        new() { Kind = WaitingKind.Blocked, BlockedReason = reason, BlockedSpoken = spoken };

    /// <summary>
    /// Classify what a session is waiting on by reading the LIVE screen grid ONLY (issue #1777). The live grid
    /// is the sole source of the verdict - the scrollback never gets a vote, so an already-answered menu buried
    /// in history can never be extracted and pressed into whatever is actually on screen (the phantom-menu case
    /// the owner ruled out). The classifier types ONLY on a positive plain-text signal; a blank grid, an
    /// unrecognized alternate-screen app, or a menu whose options we cannot extract all fail closed. A brain
    /// failure during extraction also fails closed - never guess into a picker.
    ///
    /// DEFERRED (next phase, issue #1786): a tall non-full-screen menu whose top scrolled above the visible
    /// window needs the scrollback tail as a detection SUPPLEMENT - but only WITH validation that every
    /// extracted option label appears on the live grid. That validation does not exist yet, so the scrollback
    /// is not fed here at all.
    /// </summary>
    private static async Task<WaitingScreen> DetectWaitingScreenAtAsync(
        TenantId tenant, SessionVerbClient route, WingmanTranslator translator, string sid, SpokenLanguage language, CancellationToken ct)
    {
        // The LIVE screen grid is the ONLY read that decides the verdict (rows + cursor + alternate-screen
        // flag come from one atomic Director snapshot).
        Contracts.ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sid, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: screen-grid read threw ({ex.Message}) - fail closed");
            return Blocked("screen-grid read failed", SpokenPhrases.VoiceTurnBlockedUnreadable.In(language));
        }
        if (grid is null)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: no screen-grid answer - fail closed");
            return Blocked("no screen-grid answer", SpokenPhrases.VoiceTurnBlockedUnreadable.In(language));
        }

        // TWO anchors, fail-closed default (issue #1777, round-4): a MENU is owned by its drawn selection
        // marker (cursor-independent - the Ink picker hides the cursor); TYPING requires the VISIBLE cursor
        // inside a framed composer, on the primary screen, with no menu present. Cursor visibility is the
        // discriminator, so it is passed in alongside the alternate-screen flag.
        var kind = WaitingScreenClassifier.Classify(grid.Rows, grid.CursorRow, grid.CursorCol, grid.CursorVisible, grid.IsAlternateScreen, grid.HasGrid);

        if (kind == WaitingScreenKind.Blocked)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: BLOCKED (rows={grid.Rows?.Count ?? 0}, alt={grid.IsAlternateScreen}, hasGrid={grid.HasGrid}) - fail closed");
            return Blocked("unrecognized / unreadable screen", SpokenPhrases.VoiceTurnBlockedUnreadable.In(language));
        }

        if (kind == WaitingScreenKind.PlainText)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: plain-text prompt (positive composer signal)");
            return new WaitingScreen { Kind = WaitingKind.PlainText };
        }

        // kind == Menu: the LIVE grid carries a drawn menu. Extract it off the LIVE grid text ONLY.
        var liveRows = grid.Rows ?? new List<string>();
        var liveText = string.Join("\n", liveRows);
        WingmanMenu menu;
        try { menu = await translator.DetectMenuAsync(tenant, liveText, ct); }
        catch (Exception ex)
        {
            // Looks like a menu but the brain could not read it (unreachable / no key / error). Fail closed.
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: menu on screen, detection FAILED ({ex.Message}) - fail closed");
            return Blocked("menu on screen, detection failed", SpokenPhrases.VoiceTurnBlockedMenu.In(language));
        }

        // A menu is usable only when it has ANSWERABLE options (issue #1777, finding 4): options exist, every
        // one has a real visible label (not a bare "1."/"2."), and every label actually appears on the live
        // grid (the model did not invent options). Anything short of that fails closed.
        if (menu.IsMenu && WingmanMenuLogic.MenuHasAnswerableOptions(menu, liveRows))
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: MENU with {menu.Options.Count} answerable option(s)");
            return new WaitingScreen { Kind = WaitingKind.Menu, Menu = menu };
        }

        // Looks like a menu but no answerable options could be extracted. UNCERTAIN - the dangerous case the
        // old code fell through on. Fail closed and point the person at the terminal.
        FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: looks like a menu but no answerable options - fail closed");
        return Blocked("menu on screen, options not answerable", SpokenPhrases.VoiceTurnBlockedMenu.In(language));
    }

    /// <summary>Fetch the session's live screen and, only when it is a drawn menu, ask the warm brain to
    /// extract it. Returns IsMenu=false on any miss. Used ONLY by the read-only <c>GET /wingman/menu</c>
    /// endpoint (the phone's on-entry render / the later announce step) - it never leads to a keypress. The
    /// send path (voice-turn) uses the pure <see cref="ClassifyLiveScreenAtAsync"/> instead and only ever
    /// types on a plain-text composer.</summary>
    private static async Task<WingmanMenu> DetectMenuAtAsync(
        TenantId tenant, SessionVerbClient route, WingmanTranslator translator, string sid, SpokenLanguage language, CancellationToken ct)
    {
        var screen = await DetectWaitingScreenAtAsync(tenant, route, translator, sid, language, ct);
        return screen.Kind == WaitingKind.Menu ? screen.Menu : new WingmanMenu { IsMenu = false };
    }

    /// <summary>
    /// Persist a mobile dictation capture-health record (issue #863) into the shared dictation
    /// session log, but only when the client actually supplied its measurements. The record exists to
    /// carry the audio-loss deficit (recording wall-clock vs decoded audio duration), so the
    /// transcription-context fields a desktop record carries (dictionary counts, cleanup model) are
    /// left at their defaults here. Fire-and-forget; a logging failure never affects the response.
    /// </summary>
    private static void PersistMobileCaptureHealth(string uploadId, UtteranceCompleteRequest req, int wavBytes, string? cleaned)
        => MobileCaptureHealthLog.Persist(
            uploadId, MobileCaptureHealthLog.SurfaceOr(req.ClientSurface, "mobile"),
            req.ClientRecordedMs, req.ClientDecodedSeconds, req.ClientSourceBytes, wavBytes, cleaned);

    /// <summary>Shape a <see cref="WingmanMenu"/> for the JSON response (camelCase the phone reads).</summary>
    private static object MenuJson(WingmanMenu m) => new
    {
        isMenu = m.IsMenu,
        question = m.Question,
        spoken = m.Spoken,
        selectionMode = m.SelectionMode,
        submit = m.Submit,
        options = m.Options.Select(o => new { key = o.Key, send = o.Send, note = o.Note, recommended = o.Recommended }).ToList(),
    };

    /// <summary>How long the reply transcript must stop growing before we treat the turn as done.</summary>
    private static readonly TimeSpan ReplyStable = TimeSpan.FromSeconds(2.0);

    /// <summary>How often the wait re-checks that the owning computer is still reachable. Every poll would
    /// be wasteful and every never would be blind; this is roughly every eight seconds.</summary>
    private const int ReachabilityCheckEveryPolls = 10;

    /// <summary>
    /// Wait for the agent's reply to a spoken question by watching the Gateway's own STORED conversation
    /// (the turn-push mission, phase 3b): once a new agent text appears beyond <paramref name="widgetsBefore"/>
    /// and the conversation stops growing for <see cref="ReplyStable"/>, that is the reply.
    ///
    /// This used to poll the TRANSCRIPT down the tunnel, at 750 milliseconds, for up to three minutes - one
    /// command per poll, each making the owning Director open and parse the whole transcript file on the
    /// user's disk, all so the Gateway could notice a reply it is now simply handed. The Director pushes
    /// each finished turn, so the words arrive here on their own.
    ///
    /// THE GIVING-UP SIGNAL CHANGED, and it is better. The old loop counted forty consecutive FAILED READS
    /// to guess the Director was gone - a proxy for something it could not see, using a failure mode that no
    /// longer exists (a store read cannot fail; it just has nothing yet). So the wait now asks the question
    /// it actually means: is the owning computer still reachable? If it is not, no reply is coming and
    /// saying so at once beats waiting out three minutes of silence. If it is, the wait keeps waiting -
    /// a long turn is a long turn, and giving up on a session that is still working was never the intent.
    ///
    /// Returns the reply text, or null if none landed in time.
    /// </summary>
    /// <param name="timeout">How long to wait in all. Injected so the wait's own behaviour can be tested in
    /// milliseconds instead of the three minutes a real spoken turn is allowed.</param>
    /// <param name="pollEvery">How often to look. Injected for the same reason.</param>
    /// <param name="settleFor">How long the conversation must stop growing before a reply counts as
    /// finished. Injected for the same reason.</param>
    internal static async Task<string?> WaitForReplyAsync(
        Func<IReadOnlyList<TurnWidgetDto>?> readStoredConversation,
        Func<Task<bool>> ownerReachableAsync,
        string sid, int? widgetsBefore, CancellationToken ct,
        TimeSpan? timeout = null, TimeSpan? pollEvery = null, TimeSpan? settleFor = null)
    {
        var turnTimeout = timeout ?? TurnTimeout;
        var pollInterval = pollEvery ?? PollInterval;
        var replyStable = settleFor ?? ReplyStable;
        var deadline = DateTime.UtcNow + turnTimeout;
        var baseline = widgetsBefore;
        string? reply = null;
        var stableCount = -1;
        var stableSince = DateTime.MinValue;
        var polls = 0;
        var unreachableInARow = 0;
        while (DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(pollInterval, ct); } catch (OperationCanceledException) { return reply; }

            var widgets = readStoredConversation() ?? Array.Empty<TurnWidgetDto>();
            if (baseline is null)
            {
                // No baseline could be taken before the question was sent, because nothing was stored for
                // this session then. The first conversation we actually see becomes the baseline, so only
                // what arrives AFTER it can be the answer. Waiting one more poll costs a moment; the
                // alternative is reading out an answer to a question the person did not ask.
                if (widgets.Count == 0) continue;
                baseline = widgets.Count;
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: no conversation was stored when the question was sent, so the first one seen ({baseline} widget(s)) is the baseline for the reply");
                continue;
            }

            var lastText = widgets.Skip(baseline.Value).LastOrDefault(w => w.Kind == "Text");
            if (lastText is not null && !string.IsNullOrWhiteSpace(lastText.Content))
            {
                reply = lastText.Content;
                unreachableInARow = 0;
                if (widgets.Count != stableCount) { stableCount = widgets.Count; stableSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - stableSince >= replyStable)
                {
                    FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: reply read from the stored conversation, len={reply.Length}");
                    return reply;
                }
                continue;   // a reply is in hand and settling; do not spend a reachability check on it
            }

            // No reply yet. Ask, occasionally, whether one can still arrive at all.
            if (++polls % ReachabilityCheckEveryPolls != 0) continue;

            bool reachable;
            try { reachable = await ownerReachableAsync(); }
            catch (OperationCanceledException) { return reply; }
            catch (Exception ex)
            {
                // The check itself failed. That is not evidence the computer is gone - it is the absence of
                // evidence either way - so it must never end the wait. Contained here because this runs
                // inside a request: an escaping exception would answer the person's spoken question with a
                // server error (found in review).
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: could not check whether the owning computer is reachable ({ex.Message}); continuing to wait");
                continue;
            }
            if (reachable) { unreachableInARow = 0; continue; }

            // ONE unreachable answer is not enough. A reconnect blip would end the wait on a session that is
            // still working, and worse, the finished turn may have been stored in the moment between the read
            // above and this answer - so re-read before believing it, and require a second consecutive
            // negative before giving up (found in review).
            var lateWidgets = readStoredConversation() ?? Array.Empty<TurnWidgetDto>();
            var lateText = lateWidgets.Skip(baseline.Value).LastOrDefault(w => w.Kind == "Text");
            if (lateText is not null && !string.IsNullOrWhiteSpace(lateText.Content))
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: the reply landed as the owning computer went away - answering with it rather than discarding it");
                return lateText.Content;
            }
            if (++unreachableInARow < 2) continue;

            FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: the owning computer is no longer reachable, so no reply is coming - ending the wait rather than sitting out the timeout");
            break;
        }
        FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: returning {(reply is null ? "NO reply" : "last reply len=" + reply.Length)} after wait");
        return reply;
    }
}

/// <summary>Body of the wingman voice-turn and ask-direct routes: the person's message.</summary>
public sealed class WingmanVoiceTurnRequest
{
    public string Text { get; set; } = "";
}

/// <summary>Body of the fleet-wide voice-mode route (issue #1765): <c>Enabled = true</c> turns voice
/// mode on for every session at once, <c>false</c> turns it off. An absent body defaults to enabling.</summary>
public sealed class VoiceModeAllRequest
{
    public bool Enabled { get; set; } = true;
}

/// <summary>Body of the wingman text-to-speech route: the text to speak, and an optional voice + model
/// (used by the settings "play sample" so a voice/model can be previewed before it is saved). When Voice
/// or Model is omitted the saved <see cref="Core.Configuration.TtsVoiceConfig"/> / <see cref="Core.Configuration.TtsModelConfig"/> values are used.</summary>
public sealed class WingmanTtsRequest
{
    public string Text { get; set; } = "";
    public string? Voice { get; set; }
    public string? Model { get; set; }
}

/// <summary>Body of the resumable-utterance complete route: how many chunks to reassemble and the
/// recording's MIME type / file extension (so the hosted transcriber gets a correctly-named file).</summary>
public sealed class UtteranceCompleteRequest
{
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
    public string? Ext { get; set; }

    // Capture-health (issue #863), optional - present only for the mobile dictation dialog, which
    // measures audio loss as recording wall-clock versus decoded audio duration (a compressed
    // MediaRecorder clip has no fixed bytes/sec). When present the complete handler persists a
    // dictation session record so mobile loss lands in the same log as the desktop and overlay.
    public double? ClientRecordedMs { get; set; }
    public double? ClientDecodedSeconds { get; set; }
    public long? ClientSourceBytes { get; set; }

    /// <summary>Which browser shell recorded the clip ("cockpit" / "mobile"). Every browser used to be
    /// logged as "mobile" here, so a Cockpit dictation on a desktop was filed as a phone dictation and
    /// the Cockpit's audio loss could not be separated out at all. Absent from an older client, which
    /// falls back to the literal this path always wrote.</summary>
    public string? ClientSurface { get; set; }
}
