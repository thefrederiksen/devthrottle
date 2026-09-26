using System.Collections.Concurrent;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Storage;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Durable, server-owned dictation upload (issue #1006).
///
/// The mobile app persists the raw recorded audio locally (IndexedDB) the instant Send is pressed,
/// then streams it here in SHA-checked chunks. Once the clip is fully uploaded the GATEWAY assembles
/// it, transcribes it, and injects the resulting text into the owning session ITSELF. So once the
/// audio reaches the server a dead tab, a page refresh, or a dropped connection can no longer lose a
/// recorded utterance: the client only has to get the bytes up (resumable, retry-per-chunk from the
/// durable local copy), and the server finishes the turn.
///
///   POST /dictation/upload               { sessionId } + Idempotency-Key                      -> { upload_id }
///   PUT  /dictation/{uploadId}/chunk/{i}  octet-stream + X-Chunk-Sha256                        -> { ok }
///   POST /dictation/{uploadId}/complete   { sessionId,totalChunks,mime,ext,before,after,sentAtUtc,resumed }
///                                          -> 200 { submitted, movedOn, transcript[, reason, offerSendAnyway] } | 202 { delivering, directorState }
///                                             | 200 { dropped, reason } | 409 { missing } | 400 | 402 | 5xx
///   POST /dictation/{uploadId}/ack        -> 200 { ok, retired }
///   GET  /dictation/{uploadId}/decisions  -> 200 { upload_id, state, decisions: [...] } | 404
///   GET  /dictation/{uploadId}/outcome    -> 200 (the complete's final body) | 202 { delivering, directorState } | 404
///
/// ONCE THE GATEWAY HOLDS THE WORDS, THE GATEWAY DRIVES THE DELIVERY (Voice Delivery mission, phase 5). A complete that
/// is authenticated, valid, carries its Send time and has every chunk staged is TAKEN OVER: its fields are written onto
/// the PENDING record, and from then on the Gateway itself finishes the delivery (<see cref="HeldDeliveryDriver"/>) -
/// the client only reads <c>/outcome</c>. After that moment a delivery is never answered 502, and a repeated complete
/// answers the current state and drives nothing.
///
/// A retried complete is single-flighted per uploadId (so the turn is submitted at most once WHILE this
/// instance holds it), and de-duplicated durably by the per-upload-id delivery record on disk (issue
/// #1183): once an upload id is DELIVERED or ABANDONED, its terminal tombstone makes every later
/// register/complete return the cached outcome and NEVER inject a second turn - past any age and across a
/// Gateway restart. A PENDING upload's chunks are retained (never age-swept) until it becomes terminal;
/// the tombstone is retired only by the client ack. Every route is token-gated via
/// <see cref="AuthMiddleware.HasValidToken"/> so it holds even when the production tray Gateway runs with
/// the global auth middleware off.
///
/// TENANT-KEYED (issue #1884). Authenticating a DEVICE is not the same as authorizing an UPLOAD. Every leg -
/// upload, chunk, complete, ack, abandon - now resolves the request's tenant from the authenticated device
/// key and works ONLY inside that tenant's partition of <see cref="VoiceUploadStore"/>, and the two static
/// in-memory caches below are keyed by tenant as well as upload id. Before this, an upload id was the whole
/// key: after account A completed upload id X, account B posting with <c>Idempotency-Key: X</c> was handed
/// A's terminal record - and A's TRANSCRIPT - and before A reached a terminal state B could overwrite A's
/// chunks, ack or abandon A's record, or race its completion. A GUID is not a tenant boundary; upload ids
/// travel in client logs, retries and store-and-forward queues.
///
/// WORKS ON HOSTED, TENANT-SCOPED (issue #1884, un-deny). The family is no longer refused on the hosted
/// Gateway: the five legs are mapped through <see cref="DictationTenantGate"/>, which resolves the request's
/// tenant from the authenticated device key and hands each leg a store bound to that tenant's partition -
/// fail-closed with 403 when no tenant resolves, never the shared/Local root. The completion's session
/// LOCATE is scoped to that same request tenant (see <see cref="RunCompleteCoreAsync"/>), which is what makes
/// dictation actually deliver on hosted rather than merely stop refusing: a hosted session is found in its
/// own account's partition and never another's. Self-host resolves Local throughout and is byte-identical to
/// before. The pre-partition shared root cannot be served to a hosted account tenant - it lives at the base
/// root, which only the Local partition names, and Local is refused on hosted - and the store additionally
/// quarantines any unattributable legacy staging on load (see <see cref="VoiceUploadStore"/>).
/// </summary>
internal static class GatewayDictationEndpoint
{
    /// <summary>
    /// How old a recording may be, from the moment the owner pressed Send, and still be typed into the session
    /// automatically (Voice Delivery mission, phase 2): <see cref="CcDirector.Gateway.Contracts.MaxDeliveryAge"/> -
    /// the ONE constant, the same number the Director's own age check reads, so a command handed to the Director
    /// cannot outlive the limit (QA finding F6). Before phase 5 the number lived here alone, guarded only what the
    /// Gateway still held, and on 25 September 2026 a frozen Director typed a spoken command seven minutes old the
    /// moment it woke.
    ///
    /// It replaced a byte rule that dropped any retried recording once the session's terminal had printed 512 bytes,
    /// which a busy agent's spinner passes in seconds.
    /// </summary>

    /// <summary>
    /// The <c>directorState</c> of the held answer when the Director gave no answer at all - not connected, too old
    /// to be asked, or silent. The other two held states are the Director's own words, <c>delivering</c> and
    /// <c>unknown</c> (<see cref="DeliveryStates"/>).
    /// </summary>
    internal const string NoAnswerDirectorState = DeliverySendAndAsk.NoAnswerState;

    /// <summary>The 400 answer's words for a complete without a Send time (phase 2 wire contract, section 1).</summary>
    internal const string SentAtUtcRequired = "sentAtUtc (ISO 8601 UTC) is required";

    // In-memory single-flight for complete, keyed by TENANT AND uploadId: concurrent or retried completes
    // await the SAME in-flight work so the turn is submitted at most once WHILE this instance holds the
    // entry. The entry is dropped as soon as the work settles - the DURABLE de-dupe (a delivered/abandoned
    // upload id never re-injecting, past any age and across a restart) is owned by the on-disk delivery
    // record (issue #1183), not by this cache, so there is no age-swept idempotency window to reopen the
    // hole.
    //
    // The TENANT in the key is issue #1884. This dictionary is static (process-wide) and used to be keyed by
    // upload id alone, so whichever account's complete arrived first supplied the cached run to every other
    // account posting that id - handing one account's transcript to another through a purely in-memory path
    // that no on-disk partition can guard. The store partition and this key are two separate defences and
    // both are needed: the same upload id is now perfectly legal in two tenants at once.
    private sealed record CompleteEntry(Lazy<Task<DictationOutcome>> Task);
    private static readonly TenantKeyedCache<CompleteEntry> _completes = new();

    // (tenant, uploadId) -> sessionId, captured at register so the chunk handler (which only has the
    // uploadId) can refresh the session's orange "Transcribing..." heartbeat as chunks stream in (issue
    // #1126). Pruned when the upload reaches a terminal completion; a leaked entry for a never-completed
    // upload is a single guid-pair and is bounded by real abandoned-upload volume. Tenant-keyed for the same
    // reason as _completes: it is static, and a session id is not one account's to hand to another.
    private static readonly TenantKeyedCache<string> _uploadSids = new();

    /// <summary>
    /// A process-wide cache whose key CANNOT be composed without a tenant.
    ///
    /// Same reasoning as <see cref="DictationTenantGate"/>, applied to the in-memory half. These two caches
    /// are static, so a key that omits the tenant hands one account's in-flight completion - and its
    /// transcript - to another through a path no on-disk partition can guard. When they were raw
    /// dictionaries keyed by a string the caller built, every use site was another chance to build that
    /// string without the tenant. Here the tenant is a required PARAMETER of every operation and the key is
    /// composed inside, so an un-tenanted key is not something a call site can express - including the call
    /// site somebody adds next month.
    /// </summary>
    private sealed class TenantKeyedCache<T>
    {
        private readonly ConcurrentDictionary<string, T> _map = new();

        internal T GetOrAdd(TenantId tenant, string uploadId, Func<string, T> create)
            => _map.GetOrAdd(CacheKey(tenant, uploadId), create);

        internal bool TryGet(TenantId tenant, string uploadId, out T value)
            => _map.TryGetValue(CacheKey(tenant, uploadId), out value!);

        internal void Set(TenantId tenant, string uploadId, T value)
            => _map[CacheKey(tenant, uploadId)] = value;

        internal void Remove(TenantId tenant, string uploadId)
            => _map.TryRemove(CacheKey(tenant, uploadId), out _);
    }

    /// <summary>
    /// The key both static caches use: the owning tenant AND the canonical staging spelling of the upload id.
    /// The id is canonicalized through the store's own normalizer so two spellings of one GUID cannot become
    /// two cache entries for one staging directory, and the tenant is first so a key can never be read as
    /// belonging to a different account by accident.
    /// </summary>
    internal static string CacheKey(TenantId tenant, string uploadId)
        => tenant.Value + "|" + (VoiceUploadStore.NormalizeUploadId(uploadId) ?? "invalid");

    /// <summary>
    /// Test seam: invoked with the cache key at the moment a single-flight completion entry is CREATED, so a
    /// test can bind the real call site rather than a helper. Null in production, never assigned outside
    /// tests. It exists because "the cache is tenant-keyed" is a claim about the GetOrAdd call, and a unit
    /// test of the key function alone would stay green if that call went back to keying on the upload id.
    /// </summary>
    internal static Action<string>? OnCompleteEntryCreatedForTests;

    /// <summary>
    /// Test seam: given the upload id and the assembled audio, returns the audio the complete goes on with. Null
    /// in production, never assigned outside tests. It exists because the empty-recording arm cannot be reached
    /// with real chunks - the store refuses a zero-byte chunk as incomplete, so an assembled clip always has
    /// bytes - and that arm's outcome still has to be proved through the real endpoint.
    /// </summary>
    internal static Func<string, byte[]?, byte[]?>? AssembledAudioForTests;

    /// <summary>
    /// Resolve the request's tenant from the AUTHENTICATED device key the auth layer stashed - the same seam
    /// the prompt log and the cockpit read path use. Null means DENY.
    ///
    /// GATED ON <see cref="GatewayHostedMode.IsHosted"/> ITSELF, never on whether a boundary was passed in.
    /// Deciding on the argument fails OPEN, and this is the fail-open that matters most on this endpoint: the
    /// boundary is a SECURITY argument, so a hosted call site, test, or future rewire that does not supply one
    /// would be answered <see cref="TenantId.Local"/>, and every leg below would then operate on the shared
    /// self-host root - reopening transcript reads, chunk overwrite, ack, abandon, and completion-cache
    /// joining, silently, with nothing failing loud to say so. An optional security argument is
    /// indistinguishable from a resolved one at the call site, which is exactly why that mistake survives.
    ///
    /// So on hosted this NEVER substitutes a tenant. A missing boundary, a boundary that is not hosted-wired
    /// (its ambient context is the single-tenant one, so it can only ever answer Local), and a key with no
    /// bound tenant all resolve to null, and null is a REFUSAL - never Local, never SYSTEM. Off hosted mode
    /// the answer is Local exactly as it has always been, so self-host behaviour is byte-identical.
    ///
    /// The second defence is that <see cref="Map"/> takes the boundary as a REQUIRED argument, so omitting it
    /// is a compile error rather than a runtime downgrade. Belt and braces is deliberate: this makes the
    /// runtime safe, the required argument makes the mistake unrepresentable.
    /// </summary>
    internal static TenantId? ResolveTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
    {
        if (!GatewayHostedMode.IsHosted)
            return boundary is null ? TenantId.Local : boundary.ResolveRequestTenant(ctx);
        if (boundary is null || !boundary.IsHosted)
            return null;
        return boundary.ResolveRequestTenant(ctx);
    }

    internal static IResult NoTenantResult()
        => Results.Json(new { error = "no tenant is bound to this request" },
            statusCode: StatusCodes.Status403Forbidden);

    /// <param name="gate">
    /// The ONLY way any leg below can obtain an upload store, and the reason a leg can no longer be written
    /// unscoped. See <see cref="DictationTenantGate"/>: this method deliberately does NOT take a
    /// <see cref="VoiceUploadStore"/>, so there is no unscoped store in scope anywhere in this file to
    /// accidentally use.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry,
        SessionOwnerCache? owners, string token, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, DictationTenantGate gate, Pairing.DeviceRegistry devices,
        Streaming.PushedSessionStore? pushedSessions = null,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = null,
        TimeSpan? streamStale = null,
        TimeProvider? clock = null,
        HeldDeliveryDriver? heldDeliveries = null)
    {
        // The clock the age limit reads: the real one in production; a test injects its own so the five-minute
        // boundary is proved without a five-minute wait.
        var ageClock = clock ?? TimeProvider.System;
        // Gateway Cleanup mission, Phase 2 (PR E-B): resolve the owning Director push-store-first and inject
        // the dictation through the tunnel-first SessionVerbClient (the delivery marker rides the PromptRequest
        // DeliveryUploadId field, not an HTTP header), so this path no longer HTTP-dials the Director.
        var stale = streamStale ?? TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);
        // The one place an owned delivery is attempted, shared with the Gateway's driver so the two run every attempt
        // through the same core and the same single-flight (Voice Delivery phase 5).
        var delivery = new DictationDelivery(registry, owners, transcription, transcribingSessions, pushedSessions, sendCommand,
            stale, ageClock);
        heldDeliveries?.Attach(delivery);
        app.MapPost("/dictation/upload", (DictationUploadRequest? body, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            // Authorize the UPLOAD, not just the device (issue #1884): everything below runs inside this
            // request's own tenant partition, so an upload id from another account is simply not present.
            if (!gate.TryOpen(ctx, out var store, out var tenant, out var deny)) return deny;
            var sid = body?.SessionId ?? "";
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "sessionId (guid) is required" }, statusCode: StatusCodes.Status400BadRequest);

            // The client's locally-generated id (its IndexedDB record id) is the Idempotency-Key AND the
            // upload id, so a resumed upload after a tab death maps back to the same staging dir.
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();

            // Durable de-dupe at register (issue #1183): if this upload id already reached a terminal record
            // (delivered or abandoned), do NOT re-open it as a fresh PENDING upload - return the cached
            // outcome so a re-registering client (whose earlier response was lost) drops its on-device copy
            // and acknowledges instead of re-uploading and re-injecting. Survives a restart (on disk).
            //
            // And if the record is there but cannot be READ - corrupt, locked, a shape this build does not
            // know, stamped for another tenant - do not re-open it either (issue #2745). A marker we cannot
            // read is still a marker: it says this upload id already became something, and re-opening it is
            // how the operator's own speech was injected a second time. The client is told why and holds the
            // recording; nothing is written over the marker, so the evidence is still there for an operator.
            //
            // And it is ONE operation under the upload's record gate (review round two): the read, the decision,
            // the staging refresh and the PENDING write happen together in OpenPending. As three separate calls
            // a complete could land the DELIVERED tombstone between the read and the PENDING write, and the
            // write buried it - the same re-injection, reached through a race instead of a fold.
            var opened = store.OpenPending(string.IsNullOrWhiteSpace(key) ? null : key, sid);
            var uploadId = opened.UploadId;
            if (opened.Before.Refuses)
            {
                FileLog.Write($"[GatewayDictation] upload re-register REFUSED: {opened.Before.Describe(uploadId)}; not re-opened");
                return RecordRefusal(opened.Before, uploadId);
            }
            if (!opened.Opened)
            {
                var existing = opened.Before.Record!;
                FileLog.Write($"[GatewayDictation] upload re-register of terminal uploadId={uploadId} state={existing.State}");
                return TerminalRegisterResult(uploadId, existing);
            }
            // A PENDING or FAILED record (or none) has been (re-)opened as a fresh PENDING upload: the staging
            // dir exists and the explicit durable PENDING marker carries the sessionId, which BOTH persists the
            // owning session on disk for the enforced session lock (issue #1188, so the lock survives a Gateway
            // restart) AND, for a FAILED id, IS the retry re-entry back to PENDING - overwriting the FAILED
            // marker while keeping the staged chunks (issue #1185).
            if (opened.Before.Record is { State: DictationDeliveryState.Failed })
                FileLog.Write($"[GatewayDictation] upload re-register clears FAILED uploadId={uploadId}, retrying");
            _uploadSids.Set(tenant, uploadId, sid);
            try { transcribingSessions.Begin(tenant, sid); } catch { /* the orange mark is a nicety */ }
            FileLog.Write($"[GatewayDictation] upload registered sid={sid} uploadId={uploadId}");
            return Results.Json(new { upload_id = uploadId });
        });

        app.MapPut("/dictation/{uploadId}/chunk/{index:int}", async (string uploadId, int index, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            // Issue #1884: an upload id that belongs to another account does not exist in this partition, so
            // the existing unknown-id guard becomes the authorization - the caller cannot overwrite, extend,
            // or even confirm the existence of another account's staged audio.
            if (!gate.TryOpen(ctx, out var store, out var tenant, out var deny)) return deny;
            if (!store.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            try
            {
                await store.StoreChunkAsync(uploadId, index, ms.ToArray(), string.IsNullOrEmpty(sha) ? null : sha, ctx.RequestAborted);
                // Heartbeat: a stored chunk is progress, so keep the orange mark alive past its idle
                // backstop for a slow upload that streams over more than the idle window (issue #1126).
                if (_uploadSids.TryGet(tenant, uploadId, out var chunkSid))
                    transcribingSessions.Refresh(tenant, chunkSid);
                return Results.Json(new { ok = true, index });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayDictation] chunk uploadId={uploadId} index={index} FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/dictation/{uploadId}/complete", async (string uploadId, DictationCompleteRequest? req, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            // Issue #1884: resolve and authorize the tenant BEFORE anything touches the store or the static
            // single-flight cache, so a caller who does not own this upload id can neither read its record
            // nor join its in-flight completion run.
            if (!gate.TryOpen(ctx, out var store, out var tenant, out var deny)) return deny;
            if (req is null || req.TotalChunks <= 0 || !Guid.TryParse(req.SessionId ?? "", out _))
                return Results.Json(new { error = "sessionId (guid) and totalChunks (>0) are required" },
                    statusCode: StatusCodes.Status400BadRequest);
            // Every attempt after the client's first says so in the decision log, with what it carried, so a
            // retry is readable afterwards whatever it is answered with (Voice Delivery mission).
            if (req.Resumed)
                store.RecordDecision(uploadId, DeliveryDecisions.Retried, new DeliveryDecisionFacts
                {
                    SessionId = req.SessionId,
                    Resumed = true,
                    SentAtUtc = req.SentAtUtc,
                    TotalChunks = req.TotalChunks,
                });

            // DevThrottle Stats: this dictation is a VOICE turn; resolve WHICH surface recorded it from the
            // verified device key that authenticated this complete (the phone that recorded it, or the
            // cockpit browser for a cockpit Speak) so the tally does not mislabel cockpit voice as phone.
            // Captured here (in request context) and threaded into the cached single-flight run; all completes
            // for one upload id come from the same device, so the value is stable across retries.
            var deliverySurface = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
            // And the credential kind the gate verified, captured in request scope for the same reason and
            // recorded on the ledger row (source logging, 2026-09-05).
            var deliveryIdentityKind = AuthMiddleware.IdentityKind(ctx);

            // Durable de-dupe (issue #1183): a DELIVERED or ABANDONED upload id has a terminal tombstone on
            // disk. Return its cached outcome and NEVER inject a second turn - even past the old one-hour
            // window and even after a Gateway restart (the record and this check both live on disk). This
            // handles the SEQUENTIAL/after-restart retry; the in-memory single-flight below handles two
            // CONCURRENT completes racing before the first tombstone is written (they share one run).
            //
            // A tombstone that is there but cannot be read is refused outright (issue #2745): this is the
            // injection point, and "I could not read the marker" is not "there is no marker". The orange mark
            // is cleared because nothing is going to happen for this upload until an operator looks at the file.
            var read = store.Read(uploadId);
            if (read.Refuses)
            {
                EndTranscribing(transcribingSessions, tenant, req.SessionId!);
                FileLog.Write($"[GatewayDictation] complete REFUSED: {read.Describe(uploadId)}; nothing injected");
                return RecordRefusal(read, uploadId);
            }
            var settled = read.Record;
            if (settled is { State: DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned })
            {
                EndTranscribing(transcribingSessions, tenant, req.SessionId!);
                _uploadSids.Remove(tenant, uploadId);
                FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: cached terminal outcome " +
                    $"state={settled.State} (no re-injection)");
                return TerminalOutcome(settled).ToResult();
            }

            // The Send time is what the age limit is measured from, and there is no honest stand-in for it: not the
            // arrival here (a recording held on a phone out of signal would read as fresh), not the upload's own
            // times. Every client that talks to this Gateway ships in the same image and stamps it. Refused before
            // anything is transcribed, decided or typed.
            //
            // AFTER the durable record's answer, not before it (phase 2 review, finding 3): a recording that is already
            // resolved needs no Send time to hand back its cached outcome, and a page left open across a deploy - still
            // running the client from before sentAtUtc existed - must learn that its words were delivered or shown back,
            // not be refused with this 400 forever.
            if (req.SentAtUtc is not { Kind: DateTimeKind.Utc })
                return Results.Json(new { error = SentAtUtcRequired }, statusCode: StatusCodes.Status400BadRequest);

            // A REPEATED COMPLETE FOR A DELIVERY THE GATEWAY ALREADY OWNS answers the current state and drives nothing - it
            // never sends, asks or transcribes (Voice Delivery phase 5, contract section 2). The Gateway is finishing it; a
            // complete whose first answer was lost on the way back learns where it stands and changes nothing.
            if (settled is { State: DictationDeliveryState.Pending, Owned: not null })
            {
                FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: already owned by the Gateway; answering its state, driving nothing");
                return (await delivery.AnswerRepeatedCompleteAsync(tenant, store, uploadId)).ToResult();
            }

            // A completion attempt is progress - keep the orange mark alive across the server-side
            // transcribe so a slow transcribe cannot let it age out mid-flight (issue #1126).
            transcribingSessions.Refresh(tenant, req.SessionId!);

            // A FAILED (parked) record is user-retryable, NOT a terminal short-circuit (issue #1185): this
            // complete IS the explicit retry, so clear the FAILED marker back to PENDING (keeping the staged
            // chunks) and re-drive the real work below.
            if (settled is { State: DictationDeliveryState.Failed })
            {
                store.ClearFailed(uploadId);
                FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: cleared FAILED, retrying");
            }

            // EVERY CHUNK STAGED, checked BEFORE the session is looked up and without reading the audio (phase 5): the
            // Gateway takes over only a recording it holds in full, and it must be able to take one over whose Director is
            // offline at this very moment - so this cannot wait behind the session lookup the delivery core starts with.
            // Missing chunks are the client's to send, exactly as before: 409, and nothing is taken over.
            var missing = store.MissingChunks(uploadId, req.TotalChunks);
            if (missing is null)
                return Results.Json(new { error = "unknown upload id" }, statusCode: StatusCodes.Status404NotFound);
            if (missing.Count > 0)
            {
                store.RecordDecision(uploadId, DeliveryDecisions.Incomplete, new DeliveryDecisionFacts
                {
                    TotalChunks = req.TotalChunks,
                    MissingChunks = missing.Count,
                });
                return DictationOutcome.Incomplete(missing).ToResult();
            }

            // THE GATEWAY TAKES THE DELIVERY OVER (phase 5, contract section 1): everything needed to finish it with no
            // client at all is written onto the PENDING record, with the one "gateway-owns-delivery" line. From here the
            // Gateway drives it to its end - when the Director's tunnel comes back, on a steady tick, after a restart.
            var owned = new DictationOwnedDelivery(ageClock.GetUtcNow().UtcDateTime, req.SessionId!, req.TotalChunks,
                req.Mime, req.Ext, req.Before, req.After, req.Prefix, req.SentAtUtc.Value, deliverySurface, deliveryIdentityKind,
                req.ClientRecordedMs, req.ClientDecodedSeconds, req.ClientSourceBytes, req.ClientSurface);
            var taken = store.TakeOwnership(uploadId, owned);
            switch (taken.Result)
            {
                case DictationOwnership.AlreadyOwned:
                    // Another complete of the same upload took it over a moment ago: this one is a repeat.
                    return (await delivery.AnswerRepeatedCompleteAsync(tenant, store, uploadId)).ToResult();
                case DictationOwnership.NotPending when taken.Read.Refuses:
                    return RecordRefusal(taken.Read, uploadId);
                case DictationOwnership.NotPending when taken.Read.Record is { State: DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned } resolved:
                    return TerminalOutcome(resolved).ToResult();
                case DictationOwnership.NotPending:
                    // Not a pending upload (never given a PENDING marker, or parked again meanwhile): the Gateway did not take
                    // it over, which is the one thing a 502 on this route still means.
                    FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: record is {taken.Read.Record?.State.ToString() ?? taken.Read.Kind.ToString()}, not PENDING; not taken over");
                    return Results.Json(new { error = "the upload is not pending, so the Gateway did not take its delivery over; register it again" },
                        statusCode: StatusCodes.Status502BadGateway);
            }
            if (heldDeliveries is not null) heldDeliveries.Track(tenant, uploadId, HeldDeliveryKind.Dictation);

            var outcome = await delivery.AttemptAsync(tenant, store, uploadId, owned, driveTrigger: null);
            return outcome.ToResult();
        });

        // Client acknowledgment (issue #1183): once the client has received a terminal (delivered or
        // abandoned) outcome, dropped its on-device copy, and will not re-drive this upload id, it calls
        // this to retire the durable tombstone. Idempotent: acking an already-retired (or never-created) id
        // is a no-op returning retired=false. If the ack is lost the tombstone simply persists and a later
        // re-complete returns the same outcome and the client re-acks - so the tombstone is retired ONLY on
        // a real client ack, never by age.
        app.MapPost("/dictation/{uploadId}/ack", (string uploadId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            // Issue #1884: an ack RETIRES a record - it deletes state. Scoped to the caller's own partition,
            // so acking an id another account owns finds nothing and reports retired=false, leaving that
            // account's tombstone (and therefore its de-dupe guarantee) untouched.
            if (!gate.TryOpen(ctx, out var store, out _, out var deny)) return deny;
            var retired = store.Acknowledge(uploadId);
            FileLog.Write($"[GatewayDictation] ack uploadId={uploadId} retired={retired}");
            return Results.Json(new { ok = true, retired });
        });

        // User-initiated ABANDON (issue #1181, Task 5): the user gives up on this dictation. Marks the
        // durable record ABANDONED - a terminal tombstone that DISCARDS the staged audio and clears the
        // session lock (the PENDING marker is gone, so IsSessionLocked is false and the session un-oranges).
        // Idempotent and safe against a race with delivery: if the turn already DELIVERED we do NOT abandon
        // (it landed) and say so; otherwise the id becomes ABANDONED. The client that still holds the audio
        // reconciles on its next contact - a re-register / re-complete of an abandoned id returns dropped, so
        // it drops its on-device copy with no resurrection and no duplicate. Abandon may target ANY SURFACE'S
        // dictation - phone, cockpit, desktop - because it addresses the durable upload id rather than the
        // device that recorded it. Issue #1884 draws the line that "any surface" always meant: any surface OF
        // THE SAME ACCOUNT. It is scoped to the caller's own partition, so it can no longer discard another
        // account's staged audio or resolve its pending record.
        app.MapPost("/dictation/{uploadId}/abandon", (string uploadId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!gate.TryOpen(ctx, out var store, out var tenant, out var deny)) return deny;

            // An abandon WRITES an ABANDONED tombstone over whatever marker is there. Over a marker that cannot
            // be read that would destroy the only evidence of what this upload id became - and if it was in
            // fact DELIVERED, a later re-complete would then tell the user "dropped" about speech that was
            // acted on. So an unreadable marker refuses the abandon too (issue #2745); the ack leg remains the
            // client's way to retire it once its own copy is gone.
            //
            // And the read, the already-delivered decision and the ABANDONED write are ONE operation under the
            // upload's record gate (review round three): as two calls, a complete could inject the speech and
            // land DELIVERED in between, and the abandon then wrote over it.
            var abandon = store.Abandon(uploadId, "user_abandoned");
            if (abandon.Before.Refuses)
            {
                FileLog.Write($"[GatewayDictation] abandon REFUSED: {abandon.Before.Describe(uploadId)}; marker left as it is");
                return RecordRefusal(abandon.Before, uploadId);
            }
            var existing = abandon.Before.Record;
            if (!abandon.Abandoned)
            {
                FileLog.Write($"[GatewayDictation] abandon uploadId={uploadId}: already DELIVERED, not abandoning");
                return Results.Json(new { ok = true, upload_id = uploadId, abandoned = false, already_delivered = true });
            }

            // Clear the in-memory transcribing marks so the roster un-oranges at once (the durable PENDING
            // marker - the "Uploading from phone" source - is already gone via the abandon). Keyed to the
            // caller's own tenant (issue #1884, Gap B), so an abandon can never clear another account's mark.
            var sid = existing?.SessionId;
            if (!string.IsNullOrEmpty(sid))
            {
                EndTranscribing(transcribingSessions, tenant, sid);
                transcribingSessions.ClearActivelyTranscribing(tenant, sid);
            }
            FileLog.Write($"[GatewayDictation] abandon uploadId={uploadId} sid={sid}: marked ABANDONED, staging discarded");
            return Results.Json(new { ok = true, upload_id = uploadId, abandoned = true });
        });

        // Read back what the Gateway decided about one upload (Voice Delivery mission, 25 September 2026): the
        // record's state and every decision line in the order written, from the upload's own durable directory,
        // so "what happened to my words?" is answerable without the container's log. Scoped to the caller's own
        // partition like every other leg (issue #1884): another account's upload id is simply not found. It
        // returns lengths and states, never the words - the decision log holds none, and the transcript is not
        // included from the record either.
        app.MapGet("/dictation/{uploadId}/decisions", (string uploadId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!gate.TryOpen(ctx, out var store, out _, out var deny)) return deny;
            var log = store.ReadDecisions(uploadId);
            if (!log.Found)
                return Results.Json(new { error = "unknown upload id" }, statusCode: StatusCodes.Status404NotFound);
            var record = log.Record.Record;
            FileLog.Write($"[GatewayDictation] decisions uploadId={uploadId} lines={log.Lines.Count} record={log.Record.Kind}");
            return Results.Json(new DictationDecisionsResponse
            {
                UploadId = VoiceUploadStore.NormalizeUploadId(uploadId) ?? uploadId,
                Record = log.Record.Kind.ToString(),
                State = record?.State.ToString(),
                AcknowledgedFrom = record?.AcknowledgedFrom?.ToString(),
                Submitted = record?.Submitted,
                MovedOn = record?.MovedOn,
                Reason = record?.Reason,
                SessionId = record?.SessionId,
                Decisions = log.Lines,
            }, DeliveryDecisionLine.Json);
        });

        // Read what the Gateway has made of an owned delivery (Voice Delivery phase 5, contract section 5). The client no
        // longer drives a delivery once the Gateway has taken it over: it reads this - on load, when the page becomes
        // visible, when the connection returns - and renders it. READ-ONLY: it never sends, asks, transcribes or writes.
        // Scoped to the caller's own partition like every other leg, so another account's upload id is simply not found.
        app.MapGet("/dictation/{uploadId}/outcome", (string uploadId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!gate.TryOpen(ctx, out var store, out _, out var deny)) return deny;
            return OutcomeOf(store, uploadId);
        });
    }

    /// <summary>
    /// The answer of <c>GET /dictation/{uploadId}/outcome</c>, from the durable record and the "Send anyway" delivery beside
    /// it. One set of body shapes for both, the ones the complete and the prompt route already give, so the client reads
    /// every answer the same way. Nothing here writes.
    /// </summary>
    internal static IResult OutcomeOf(VoiceUploadStore store, string uploadId)
    {
        var log = store.ReadDecisions(uploadId);
        if (!log.Found)
            return Results.Json(new { error = "unknown upload id" }, statusCode: StatusCodes.Status404NotFound);
        if (log.Record.Refuses)
            return RecordRefusal(log.Record, uploadId);

        // A "Send anyway" the Gateway took over answers first: it is the newer delivery of the same words, and the record
        // beneath it was already resolved (or acknowledged) before the owner pressed it.
        var sendAnyway = store.ReadSendAnyway(uploadId);
        if (sendAnyway is { } claim)
        {
            return claim.Outcome switch
            {
                null => Held(store.LastHeldState(uploadId)),
                SendAnywayOutcomes.Delivered => Results.Json(new { submitted = true, movedOn = false, transcript = claim.Text }),
                SendAnywayOutcomes.Unconfirmed or SendAnywayOutcomes.TooOld or SendAnywayOutcomes.SessionExited => Results.Json(new
                {
                    submitted = false, movedOn = true, reason = claim.Outcome, offerSendAnyway = OffersSendAnyway(claim.Outcome),
                    transcript = claim.Text,
                }),
                _ => throw new InvalidOperationException($"upload {uploadId} has a Send anyway settled as '{claim.Outcome}', which this Gateway does not know"),
            };
        }

        var record = log.Record.Record;
        if (record is { State: DictationDeliveryState.Pending, Owned: not null })
            return Held(store.LastHeldState(uploadId));
        if (record is { State: DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned })
            return TerminalOutcome(record).ToResult();
        var what = record is null ? "has no record" : $"is {record.State} and not held by the Gateway";
        return Results.Json(new { error = $"upload {uploadId} {what}" }, statusCode: StatusCodes.Status404NotFound);

        // Held: 202 with the Gateway's last word on it. Before any attempt has answered, that word is "retrying" - true:
        // the Gateway will try it itself.
        static IResult Held(string? directorState)
            => Results.Json(new { delivering = true, directorState = directorState ?? DeliverySendAndAsk.RetryingState },
                statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Start the one attempt of an owned delivery, or JOIN the one already running for this upload. Keyed by tenant and
    /// upload id, so the complete route and the driver share it: at most one attempt of one upload is ever running in
    /// this Gateway. The entry leaves the cache the moment its attempt settles - the durable record owns everything after
    /// that - and the session's orange mark is cleared on every outcome but an incomplete upload (issue #1048).
    /// </summary>
    internal static Task<DictationOutcome> StartOrJoin(TenantId tenant, string uploadId, string sid,
        TranscribingSessions transcribingSessions, Func<Task<DictationOutcome>> attempt)
    {
        var entry = _completes.GetOrAdd(tenant, uploadId, completeKey =>
        {
            OnCompleteEntryCreatedForTests?.Invoke(completeKey);
            return new CompleteEntry(new Lazy<Task<DictationOutcome>>(
                () => RunAndSettleAsync(tenant, uploadId, sid, transcribingSessions, attempt)));
        });
        return entry.Task.Value;
    }

    /// <summary>The attempt running for this upload right now, when there is one.</summary>
    internal static bool TryJoin(TenantId tenant, string uploadId, out Task<DictationOutcome> running)
    {
        if (_completes.TryGet(tenant, uploadId, out var entry))
        {
            running = entry.Task.Value;
            return true;
        }
        running = null!;
        return false;
    }

    private static async Task<DictationOutcome> RunAndSettleAsync(TenantId tenant, string uploadId, string sid,
        TranscribingSessions transcribingSessions, Func<Task<DictationOutcome>> attempt)
    {
        DictationOutcome outcome;
        try
        {
            outcome = await attempt();
        }
        catch (Exception ex)
        {
            // The delivery core answers its own failures; reaching here means the attempt could not even start (its drive
            // line could not be written, for one). The delivery is still the Gateway's, so it is held to be tried again -
            // after ownership it is never a 502 - and the log says why.
            FileLog.Write($"[GatewayDictation] attempt of uploadId={uploadId} could not run: {ex.Message}; held, the Gateway will try again");
            outcome = DictationOutcome.StillDelivering(DeliverySendAndAsk.RetryingState);
        }
        finally
        {
            _completes.Remove(tenant, uploadId);
        }
        // The Gateway OWNS the orange "Transcribing..." mark, so it clears it on every outcome but an incomplete upload
        // (issue #1048, extended by #1126): more chunks are coming only then.
        if (!outcome.IsIncomplete)
        {
            EndTranscribing(transcribingSessions, tenant, sid);
            _uploadSids.Remove(tenant, uploadId);
        }
        return outcome;
    }

    // Map a NON-Ok transcription result to the dictation outcome, or null when the result is Ok and the
    // caller should continue to inject (issue #1185).
    //
    // TWO SEPARATE QUESTIONS, and conflating them is what wedged the orange (defect 19):
    //
    //   1. What does the CLIENT get told? Only PermanentError gets the 422 "stop forever" contract.
    //      Out-of-credits stays 402 and every other non-Ok outcome stays a retryable 502, so the durable
    //      queue keeps re-driving a failure that might yet succeed. That classification is CORRECT and is
    //      deliberately unchanged here.
    //
    //   2. What state is the RECORD left in? EVERY non-Ok outcome now parks the record FAILED. This is the
    //      defect-19 fix. Parking is NOT "giving up": FAILED keeps the staged chunk bytes, and the next
    //      register/complete clears it back to PENDING and re-drives (see the FAILED re-entry at register
    //      and complete). What it changes is that a record which is going nowhere RIGHT NOW stops claiming
    //      the session is "Uploading from phone", because IsPending is false while FAILED.
    //
    // OBSERVED, not theorised (14 July 2026 log correlation): upload f13cb4b6d9d0 on 12 July stood PENDING
    // for 1 hour 30 minutes - painting its session orange across four Gateway restarts - while complete
    // returned 502 roughly fifteen times from THIS retryable arm, which returned without any terminal write.
    // It then transcribed and delivered 362 characters at 07:40. Both halves matter: the durable record was
    // right to keep the words (they landed), and the colour was lying for ninety minutes. Parking FAILED
    // here keeps the words AND ends the lie - that upload would still have delivered at 07:40.
    //
    // The retryable arm used to return silently, which is why the log could say WHEN it wedged but never
    // WHY. It logs now.
    internal static DictationOutcome? MapNonOkTranscription(
        GatewayTranscriptionResult result, string uploadId, VoiceUploadStore store)
    {
        if (result.Outcome == TranscriptionOutcome.Ok) return null;
        if (result.Outcome == TranscriptionOutcome.OutOfCredits)
        {
            // Park the record so the session stops reading "Uploading from phone" while there is no credit
            // to transcribe it with; the client still gets 402, and adding credit + retrying re-enters
            // PENDING and delivers. NOTE: never once observed to fire - zero OutOfCredits in any log on this
            // machine, ever, across 846 terminal outcomes. The mechanism is real; the cause is not this.
            store.MarkFailed(uploadId, "out_of_credits");
            FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: out of credits " +
                $"code={result.Code}; parked FAILED (chunks retained, retryable)");
            return DictationOutcome.OutOfCredits(HostedAiErrorMapper.MapCode(result.Code));
        }
        // A genuinely-permanent failure (unsupported/undecodable format, or too large to reduce - issue
        // #1139) can NEVER transcribe, so returning the generic retryable 502 makes the durable queue
        // re-drive it forever. Instead park the record FAILED with the reason code (KEEPING the chunks so an
        // explicit user retry can re-complete) and return the client's stop contract.
        if (result.Outcome == TranscriptionOutcome.PermanentError)
        {
            store.MarkFailed(uploadId, result.Code ?? "");
            FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: permanent failure " +
                $"code={result.Code}; parked FAILED");
            return DictationOutcome.Permanent(TranslatePermanentReason(result.Code));
        }
        // The retryable arm - THE ONE THAT ACTUALLY WEDGED (see f13cb4b6d9d0 above). Still a 502 the client
        // re-drives; now it parks the record so the colour tells the truth between attempts.
        store.MarkFailed(uploadId, result.Code ?? "transcription_error");
        FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: retryable transcription failure " +
            $"code={result.Code} error={result.Error}; parked FAILED (chunks retained, retryable)");
        return DictationOutcome.Error(StatusCodes.Status502BadGateway, result.Error ?? "transcription failed");
    }

    // Translate the transcription lane's machine-readable permanent-failure code into the client-facing
    // reason at THIS boundary (issue #1185): audio_too_large -> audio-too-large; unsupported_format and
    // non_decodable both -> unsupported-format. Any unrecognized permanent code is still a permanent stop,
    // so it defaults to the generic unsupported-format rather than being reclassified as retryable.
    internal static string TranslatePermanentReason(string? code) => code switch
    {
        "audio_too_large" => "audio-too-large",
        "unsupported_format" => "unsupported-format",
        "non_decodable" => "unsupported-format",
        _ => "unsupported-format",
    };

    // Map a terminal delivery record to the outcome a re-complete returns: the cached submitted result for
    // DELIVERED (so the turn is never injected twice), or a clear dropped result for ABANDONED.
    internal static DictationOutcome TerminalOutcome(DictationDeliveryRecord record)
        => record.State == DictationDeliveryState.Abandoned
            ? DictationOutcome.Dropped(record.Reason ?? "")
            : DictationOutcome.Submitted(record.Submitted, record.MovedOn, record.Transcript, record.Reason);

    // The response for an upload id whose delivery record is there but cannot be read (issue #2745): the
    // register, complete and abandon legs all refuse rather than re-open, inject, or write over it. The
    // status says whether a retry can help: a marker that could not be READ (locked, permission, a disk
    // fault) may read fine in a moment, so 423 Locked - the client keeps the recording and tries again
    // later. NOT 503: the phone client reads every 502/503/504 as "the Gateway is unreachable" and flips its
    // connection banner, and this Gateway answered. A marker that was read and is not a delivery record, or
    // is another tenant's, will not change by itself, so 409 - it needs an operator at the file the body
    // names. Neither is a terminal outcome, so the client does not drop its copy; and neither is a 2xx, so
    // nothing downstream treats it as opened.
    private static IResult RecordRefusal(DictationRecordRead read, string uploadId)
    {
        var status = read.Kind == DictationRecordReadKind.Unreadable
            ? StatusCodes.Status423Locked
            : StatusCodes.Status409Conflict;
        return Results.Json(new
        {
            error = read.Describe(uploadId) + "; refusing to re-open, deliver, or overwrite it",
            upload_id = uploadId,
            record = read.Kind.ToString(),
            file = read.Path,
        }, statusCode: status);
    }

    // The register-time response for an upload id that is already terminal: echoes the id plus the cached
    // outcome so a re-registering client drops its copy and acknowledges instead of re-uploading.
    private static IResult TerminalRegisterResult(string uploadId, DictationDeliveryRecord record)
        => record.State == DictationDeliveryState.Abandoned
            ? Results.Json(new { upload_id = uploadId, terminal = true, submitted = false, movedOn = false, dropped = true, reason = record.Reason ?? "", transcript = "" })
            : record.MovedOn
                // Shown back, not sent: the reason says why, read from the record (null on a tombstone written by the
                // byte rule before phase 2, which the client shows with its generic wording), and whether "Send anyway"
                // is offered is ruled here from that same reason (change 1).
                ? Results.Json(new { upload_id = uploadId, terminal = true, submitted = record.Submitted, movedOn = true, dropped = false, transcript = record.Transcript, reason = record.Reason, offerSendAnyway = OffersSendAnyway(record.Reason) })
                : Results.Json(new { upload_id = uploadId, terminal = true, submitted = record.Submitted, movedOn = false, dropped = false, transcript = record.Transcript });

    internal static async Task<DictationOutcome> RunCompleteCoreAsync(
        string uploadId, TenantId tenant, DictationCompleteRequest req, VoiceUploadStore store, DirectorRegistry registry,
        SessionOwnerCache? owners, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, string? deliverySurface, string deliveryIdentityKind,
        Streaming.PushedSessionStore? pushedSessions, DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        TimeSpan streamStale, TimeProvider clock)
    {
        var sid = req.SessionId!;
        // Checked at the route before this run was created; a caller that got here without it is a defect in the
        // caller, not a recording to guess an age for.
        var sentAtUtc = req.SentAtUtc
            ?? throw new InvalidOperationException($"complete of upload '{uploadId}' reached the delivery core without sentAtUtc");
        // THE DELIVERY ID IS THE UPLOAD ID, in the store's one spelling: the identity the Director's delivery record
        // refuses a second copy by, and the one the Gateway asks it about.
        var deliveryId = VoiceUploadStore.NormalizeUploadId(uploadId)
            ?? throw new InvalidOperationException($"upload id '{uploadId}' is not a GUID, yet its record was read");
        try
        {
            // REACHABILITY IS THE FIRST QUESTION, BEFORE ANY COST AND BEFORE ANY MARK.
            //
            // This locate used to sit AFTER the transcribe, and the exited arm below used to return a bare
            // 410 that resolved nothing. Both were wrong, and together they were an unbounded loop: the
            // durable record stayed PENDING, the phone's background driver re-completed every few seconds
            // (two-second exponential backoff capped at fifteen for the first hour, then every five minutes,
            // forever - see the client's backgroundSend driver), and each lap paid for a full transcript that
            // was then thrown away while the dead session was repainted orange. Observed 13 September 2026:
            // one session cycled between "Transcribing" and nothing for hours and could only be stopped by
            // deleting the session.
            //
            // The gate needs the SESSION and nothing else - no key, no chunks, no audio - so it is asked
            // first. A dictation aimed at a session that cannot receive it now costs one in-memory lookup.
            var (director, session) = await GatewayEndpoints.LocateSessionAsync(
                registry, sid, pushedSessions, streamStale, tenant, owners);
            // The Director that holds the session, remembered on the durable record the moment a locate finds it: it
            // is what lets a LATER attempt - whose own locate finds nothing - prove the session has ENDED rather than
            // hold it forever (contract section 9, F4). It is the ONE source for that fact: the record names its
            // Director or no ending can be proved at all, and nothing else is consulted.
            if (director is not null)
                store.RememberOwningDirector(uploadId, director.DirectorId);
            if (director is null || session is null)
            {
                // THE SESSION CANNOT BE REACHED RIGHT NOW - its Director is not connected (Voice Delivery phase 5). The
                // Gateway owns this delivery, so this is not an error for the client to retry: it is held as "waiting for
                // the Director", and the Gateway sends it itself when that Director's tunnel comes back. The time rules
                // still apply when no Director ever returns, so nothing waits forever:
                store.RecordDecision(uploadId, DeliveryDecisions.SessionNotFound,
                    new DeliveryDecisionFacts { SessionId = sid, StatusCode = StatusCodes.Status404NotFound });
                // ENDED ONLY WHEN PROVABLE (contract section 9, F4): its Director is connected and fresh and no longer
                // lists the session. Resolved AT ONCE, session-exited, with whatever words an earlier attempt paid for
                // (contract section 8: a transcript that was paid for is never thrown away) and NO "Send anyway" - there
                // is no session left to send to.
                if (SessionEndedOnAFreshDirector(store.Read(uploadId).Record?.Owned?.DirectorId,
                        pushedSessions, streamStale, tenant, sid))
                    return ResolveAsUndeliverable(store, uploadId, sid, "no longer listed by its connected Director",
                        store.SentWords(uploadId));
                // - words that may already be in (an earlier attempt sent them) are "could not confirm it arrived" past
                //   the limit from Send, with the words kept, and held until then;
                if (store.MayHaveBeenSentToDirector(uploadId))
                    return HoldOrUnconfirmed(store, uploadId, sid, store.SentWords(uploadId), sentAtUtc, clock,
                        DeliverySendAndAsk.DirectorNotConnected, DeliverySendAndAsk.WaitingForDirectorState);
                // - words never sent are held until the limit, and past it are transcribed below so they can be shown
                //   back as too old: the delivery gate after the transcript finds the session still unreachable and
                //   resolves them there.
                if (!IsTooOld(clock, sentAtUtc, out _))
                    return HoldAsStillDelivering(store, uploadId, sid, DeliverySendAndAsk.WaitingForDirectorState);
            }
            else if (IsExited(session))
                // The words an earlier attempt may already have paid for are handed back here too (contract section 8):
                // a retry whose session exited between attempts must not throw them away.
                return ResolveAsUndeliverable(store, uploadId, sid, session.Status ?? "", store.SentWords(uploadId));

            // A RETRY ASKS FIRST, BEFORE IT PAYS FOR A TRANSCRIPT (Voice Delivery mission, phase 2). When an earlier
            // attempt of this upload handed the words to the Director, they may already be in the session - the 09:05
            // incident on 25 September 2026 was exactly that: a slow success read as a failure, retried, and doubled by
            // "Send anyway". The Director is the one place that knows, so it is asked, and only a recording it says is
            // not in (never seen, or a send that failed) is transcribed and sent again.
            if (director is not null && store.MayHaveBeenSentToDirector(uploadId))
            {
                var earlier = await DeliverySendAndAsk.AskAsync(store, uploadId, sid, deliveryId,
                    new SessionVerbClient(director, sendCommand), DeliveryDecisions.AskReasonRetryAsksFirst);
                if (earlier.Kind != SessionVerbClient.DeliveryStateAskKind.Answered)
                    return HoldOrUnconfirmed(store, uploadId, sid, store.SentWords(uploadId), sentAtUtc, clock,
                        DeliverySendAndAsk.NoAnswerName(earlier.Kind)!);
                switch (earlier.Answer!.State)
                {
                    case DeliveryState.Delivered:
                        // The words are in: resolved exactly as a delivery. This attempt paid for no transcript; the words
                        // handed back are the ones the earlier send kept on the PENDING record (phase 2, change 1).
                        var sentWords = store.SentWords(uploadId);
                        store.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: sentWords);
                        FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: the Director says an earlier " +
                            $"attempt was delivered; resolved as delivered with chars={sentWords.Length}, nothing transcribed or typed again");
                        return DictationOutcome.Submitted(true, false, sentWords);
                    case DeliveryState.Delivering:
                        return HoldAsStillDelivering(store, uploadId, sid, DeliveryStates.Delivering);
                    case DeliveryState.NotDelivered:
                    case DeliveryState.Unknown:
                        // Known not to be in: carry on as a first attempt - reuse the kept words, the age limit, send.
                        // A not-delivered reason of too-old (the DIRECTOR's own age check, contract section 9, F6) needs
                        // no arm of its own here: the age limit below the send shows the words back as too old on any
                        // attempt that is past it, and the send's own answer arm catches a refusal that arrives past
                        // the limit - so the decision record always says the Director refused it for age.
                        break;
                    default:
                        throw new InvalidOperationException($"the Director answered delivery state {earlier.Answer.State}, which this Gateway does not know");
                }
            }

            // Only now is there real work to do, so only now is the session marked ACTIVELY transcribing
            // (issue #1181, Task 4). The aggregator reads this to show "Transcribing" (vs the durable PENDING
            // marker's "Uploading from phone"), and it used to be the first line of this method - so a
            // session that the very next gate was about to refuse outright was painted as though the server
            // were busy turning audio into text for it. A mark that precedes the gate that can refuse the
            // delivery is a claim about work that is not going to happen. Cleared in the finally below, so it
            // never outlives the run.
            transcribingSessions.MarkActivelyTranscribing(tenant, sid);

            // THE TRANSCRIPT IS REUSED, NEVER PAID FOR TWICE (contract section 8): the words are on the record the
            // MOMENT they exist - written below the first time this recording was transcribed - so every later attempt,
            // including one after the Director said not-delivered or unknown, reads them back and transcribes nothing.
            // A paid transcript thrown away while its words have not been shown to the owner was QA case 8's damage.
            var transcript = store.SentWords(uploadId);
            if (transcript.Length == 0)
            {
                // The configured mode's key must be present before we pay the reassembly + transcribe cost.
                var routing = transcription.Resolve();
                if (routing.Key is null)
                {
                    store.RecordDecision(uploadId, DeliveryDecisions.CompleteError, new DeliveryDecisionFacts
                    {
                        StatusCode = StatusCodes.Status503ServiceUnavailable,
                        Error = $"no key configured for transcription mode {routing.Mode}",
                    });
                    // A recording that could never be transcribed is not held forever (contract section 8): past the limit
                    // from Send it has no words and nothing was ever sent, so it is shown back too old - the recording is
                    // still on the device - and within it the Gateway owns the delivery and tries again itself.
                    if (IsTooOld(clock, sentAtUtc, out var noKeyAge))
                        return ResolveTooOld(store, uploadId, sid, "", noKeyAge);
                    return HoldToRetry(store, uploadId, sid);
                }

                var assembled = await store.AssembleAsync(uploadId, req.TotalChunks);
                if (assembled.Status == "unknown_upload")
                    return DictationOutcome.Error(StatusCodes.Status404NotFound, "unknown upload id");
                if (assembled.Status == "incomplete")
                {
                    store.RecordDecision(uploadId, DeliveryDecisions.Incomplete, new DeliveryDecisionFacts
                    {
                        TotalChunks = req.TotalChunks,
                        MissingChunks = assembled.Missing.Count,
                    });
                    return DictationOutcome.Incomplete(assembled.Missing);
                }
                var audio = assembled.Audio;
                if (AssembledAudioForTests is { } substitute) audio = substitute(uploadId, audio);
                if (audio is null || audio.Length == 0)
                {
                    // Retired in place rather than deleted, so this outcome stays readable in the decision log
                    // (Voice Delivery mission). To every reader it is the deleted directory it replaces.
                    store.ResolveEmptyRecording(uploadId);
                    return DictationOutcome.Error(StatusCodes.Status502BadGateway, "assembled recording was empty");
                }

                var result = await transcription.TranscribeAsync(
                    audio, "audio." + (req.Ext ?? "wav"), req.Mime ?? "audio/wav", applyCorrection: true, CancellationToken.None,
                    tenant: tenant, source: "dictation");
                // A transcription that failed but might succeed on another try is the Gateway's to try again (Voice Delivery
                // phase 5): the record stays PENDING and owned, the client is told 202 "retrying", and the driver re-runs it.
                // It is no longer parked FAILED - that parking existed so the colour told the truth while the client re-drove
                // it every few seconds, and the client no longer drives an owned delivery at all. Out of credits and a
                // permanent failure are answered exactly as before (contract section 1: out of scope, not held).
                //
                // A recording whose transcription KEEPS failing is not held to the 24-hour expiry (contract section 8):
                // past the limit from Send it is shown back too old with no words and "Send anyway" - the client already
                // has the no-words wording, and the recording is still on the device. Within the limit it stays held.
                if (result.Outcome is not (TranscriptionOutcome.Ok or TranscriptionOutcome.OutOfCredits or TranscriptionOutcome.PermanentError))
                {
                    store.RecordDecision(uploadId, DeliveryDecisions.CompleteError, new DeliveryDecisionFacts
                    {
                        StatusCode = StatusCodes.Status502BadGateway,
                        Reason = result.Code ?? "transcription_error",
                        Error = result.Error ?? "transcription failed",
                    });
                    FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: retryable transcription failure " +
                        $"code={result.Code} error={result.Error}; the Gateway will try again itself");
                    if (IsTooOld(clock, sentAtUtc, out var failedAge))
                        return ResolveTooOld(store, uploadId, sid, "", failedAge);
                    return HoldToRetry(store, uploadId, sid);
                }
                if (MapNonOkTranscription(result, uploadId, store) is { } nonOk)
                    return nonOk;

                transcript = (result.Text ?? "").Trim();
                store.RecordDecision(uploadId, DeliveryDecisions.Transcribed, new DeliveryDecisionFacts
                {
                    Characters = transcript.Length,
                    AudioBytes = audio.Length,
                });
                // THE WORDS ARE ON THE RECORD THE MOMENT THEY EXIST (contract section 8) - before the second locate and
                // before the send - so any later ruling (held, too old, could not confirm, session ended) hands them back,
                // and no later attempt ever pays for them again. Written over the PENDING record, as the tombstones keep
                // them; the acknowledgement deletes them exactly as it deletes a tombstone's.
                store.KeepSentWords(uploadId, transcript);
                // Capture-health (issue #863): persist the fire-and-forget Send path's audio-loss deficit into
                // the SAME dictation session log the Voice-mode and desktop paths write, via the one shared
                // helper. The assembled audio byte count is what the server actually transcribed. When the client
                // did not send its measurements this is a no-op. Fire-and-forget - never affects the outcome.
                MobileCaptureHealthLog.Persist(
                    uploadId, MobileCaptureHealthLog.SurfaceOr(req.ClientSurface, "mobile-send"),
                    req.ClientRecordedMs, req.ClientDecodedSeconds, req.ClientSourceBytes,
                    audio.Length, transcript);
            }
            // Compose the final message: any typed text the caret split the dictation around (before /
            // after), any earlier paused dictation segments already turned to text (prefix), and this
            // clip's transcript, space-joined skipping empties. The common voice case is transcript alone.
            // Each part's place in the joined message is recorded as it is composed (source logging, 2026-09-05):
            // the earlier dictated segments and this clip's transcript are the SPOKEN characters, the typed
            // halves are not, and the ledger row carries exactly those ranges over the text delivered.
            var spokenSpans = new List<SpokenSpanDto>();
            var pieces = new List<string>();
            var at = 0;
            foreach (var (piece, spoken) in new[] { (req.Before, false), (req.Prefix, true), (transcript, true), (req.After, false) })
            {
                if (string.IsNullOrWhiteSpace(piece)) continue;
                var trimmed = piece!.Trim();
                if (pieces.Count > 0) at += 1;
                if (spoken) spokenSpans.Add(new SpokenSpanDto { Start = at, Length = trimmed.Length });
                pieces.Add(trimmed);
                at += trimmed.Length;
            }
            var message = string.Join(" ", pieces);
            if (message.Length == 0)
            {
                // Silent/empty clip with no typed text: nothing to submit, but the turn is genuinely done -
                // record it as a durable DELIVERED tombstone so a re-complete returns the same no-op outcome
                // instead of re-running (issue #1183). Discards the retained chunks, keeps the marker.
                store.MarkDelivered(uploadId, submitted: false, movedOn: false, transcript);
                return DictationOutcome.Submitted(false, false, transcript);
            }

            // THE DELIVERY GATE, asked a SECOND time and deliberately so. The gate at the top of this method
            // is the COST gate: it refuses before we pay for a transcript. This one is asked at the moment of
            // delivery, because the transcribe above takes seconds and a session can exit inside them.
            //
            // Gateway Cleanup mission, Phase 2: resolve the owner push-store-first (no HTTP fan-out) and gate
            // on an exited session, exactly as the old LocateAsync did, then reach it through the tunnel.
            //
            // Located in the REQUEST'S OWN TENANT (issue #1884, un-deny). This is what makes dictation actually
            // WORK on the hosted Gateway: the tenant was resolved from the authenticated device key at the gate
            // and carried in here, so the session is found in the caller's partition and NEVER another
            // account's. On self-host the tenant is Local and this is byte-identical to before. A caller whose
            // tenant did not resolve never reached this leg - the gate refused it up front - so there is no
            // path here that falls back to a shared/Local locate on hosted.
            (director, session) = await GatewayEndpoints.LocateSessionAsync(
                registry, sid, pushedSessions, streamStale, tenant, owners);
            if (director is not null)
                store.RememberOwningDirector(uploadId, director.DirectorId);
            if (director is null || session is null)
            {
                store.RecordDecision(uploadId, DeliveryDecisions.SessionNotFound,
                    new DeliveryDecisionFacts { SessionId = sid, StatusCode = StatusCodes.Status404NotFound });
                // Ended only when provable (contract section 9, F4) - and this gate holds the words in hand, so an
                // ended session resolves with them: session-exited, no "Send anyway".
                if (SessionEndedOnAFreshDirector(store.Read(uploadId).Record?.Owned?.DirectorId,
                        pushedSessions, streamStale, tenant, sid))
                    return ResolveAsUndeliverable(store, uploadId, sid, "no longer listed by its connected Director", transcript);
                // Known not to be in, and the session cannot be reached: past the limit the words are shown back as too
                // old (they were transcribed for exactly this); within it the Gateway waits for the Director.
                if (IsTooOld(clock, sentAtUtc, out var unreachableAge))
                    return ResolveTooOld(store, uploadId, sid, transcript, unreachableAge);
                return HoldAsStillDelivering(store, uploadId, sid, DeliverySendAndAsk.WaitingForDirectorState);
            }
            if (IsExited(session))
                return ResolveAsUndeliverable(store, uploadId, sid, session.Status ?? "", transcript);
            var route = new SessionVerbClient(director, sendCommand);

            // THE AGE LIMIT (Voice Delivery mission, phase 2; it replaced the byte rule). Reached only by words KNOWN
            // not to be in the session: a first attempt, or a retry whose Director said "not delivered" or "never
            // seen" above. A recording that may already be in was held before the transcript and never gets here,
            // however long it has been - showing it back could hand the owner a "Send anyway" that doubles it.
            // After the transcript on purpose, so the words can be shown back. Strictly more than the limit: 5:00
            // is sent, 5:01 is shown back.
            if (IsTooOld(clock, sentAtUtc, out var age))
                return ResolveTooOld(store, uploadId, sid, transcript, age);

            // The Gateway injects the dictation by calling the owning Director's control API DIRECTLY, which
            // BYPASSES the Gateway's own /sessions/{sid}/prompt front door (issue #1188). That front door
            // blocks OTHER surfaces from typing into the PENDING session. The Director now ALSO enforces the
            // lock on its own control API (issue #1181, Task 3b), so this delivery is no longer implicitly
            // exempt there: it names its upload id via the X-Dictation-Delivery header (deliveryUploadId), and
            // the Director exempts exactly this send - the dictation's own arrival, which is what the lock is
            // held for. The header rides the fleet-authenticated call, so it cannot be forged from outside.
            // DevThrottle Stats: Surface tags which surface recorded this voice turn (phone / cockpit);
            // deliveryUploadId marks it a voice Delivery at the Director. Together the Director counts it as
            // one voice turn from the resolved surface. A dictation is always a real operator turn, so when
            // the device key did not resolve we stamp "unknown" (never null) - it is counted into the honest
            // "unknown" surface bucket, never silently dropped (decision 9).
            //
            // SPOKEN ONLY WHEN THE WORDS ARE THE TRANSCRIPT AND NOTHING ELSE (ruling R10; inspection finding I2-01 of
            // the "Clean up Your Throttle" mission, 2026-09-05). The Director reads a nonblank DeliveryUploadId as
            // "a voice turn", and this path used to stamp it on the WHOLE composed message - so typed text the
            // caret split the dictation around (before / after) and any earlier segment already turned to text
            // (prefix) were all counted as speech, while the same words sent from the paused dialog were typed.
            // The rule the page discloses is applied here, at the one place the message is composed: the id
            // rides only when before, prefix and after are all empty. A mixed message is delivered exactly the
            // same, as one typed turn from the same surface. The upload's own durable record is untouched.
            // THE RULE IS THE ONE THE DESKTOP APPLIES TOO (ruling R20): SpokenTurnRule, in Core, and its
            // Examples table is what both surfaces' tests feed through their real paths.
            var spokenAlone = SpokenTurnRule.IsSpokenAlone(req.Before, req.Prefix, req.After);
            if (!spokenAlone)
                FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: composed with typed text around the transcript; delivered as ONE TYPED turn (ruling R10)");
            store.RecordDecision(uploadId, DeliveryDecisions.SentToDirector, new DeliveryDecisionFacts
            {
                SessionId = sid,
                Characters = message.Length,
                SpokenAlone = spokenAlone,
            });
            // THE DELIVERY ID RIDES EVERY DELIVERY (Voice Delivery mission, phase 1), spoken alone or composed with
            // typed text: it is the identity the Director's delivery record refuses a second copy by. The voice-turn
            // marker above decides only the send source and stays as it was.
            //
            // SENT WITH THE FOUR OUTCOMES KEPT APART (phase 2). "No answer came back" is not "it failed": the 25
            // September incident was a slow success read as a failure. So an unanswered send is followed by a question
            // to the Director, never by a 502 that tells the client to type the words again.
            var sent = await DeliverySendAndAsk.SendAsync(route, store, uploadId, sid, deliveryId, new PromptRequest
            {
                Text = message,
                AppendEnter = true,
                Surface = deliverySurface ?? "unknown",
                DeliveryUploadId = spokenAlone ? uploadId : null,
                DeliveryId = deliveryId,
                // THE SEND TIME THE DIRECTOR'S AGE LIMIT IS MEASURED FROM (Voice Delivery phase 5, contract section 9,
                // F6): the recording's own sentAtUtc - the moment the owner pressed Send - never the moment this
                // attempt happened to run, so a re-send minutes later is not minutes fresher than the recording is.
                SentAtUtc = sentAtUtc,
                Provenance = new SubmissionProvenanceDto
                {
                    Route = SubmissionRoutes.GatewayDictation,
                    IdentityKind = deliveryIdentityKind,
                    TranscriptId = uploadId,
                    SpokenSpans = spokenSpans,
                },
            },
            // WHAT THE DIRECTOR SAID, written once the answer is read so its facts are final: whether this attempt is
            // treated as ok, the error, the Director's own delivery state and reason, and whether it was a refused
            // duplicate. "Why was this delivered only once" is read from this line.
            (answer, reading) => store.RecordDecision(uploadId, DeliveryDecisions.DirectorAnswer, new DeliveryDecisionFacts
            {
                SessionId = sid,
                Ok = reading.Delivered,
                Error = reading.Error,
                State = answer.Body?.DeliveryState is { } directorState ? DeliveryStates.Format(directorState) : null,
                Reason = reading.Unanswered ? DeliveryDecisions.AskReasonPromptUnanswered : answer.Body?.DeliveryStateReason,
                RefusedDuplicate = reading.RefusedDuplicate ? true : null,
            }));

            switch (sent.Kind)
            {
                case DeliverySendKind.Delivered:
                    return ResolveDelivered(store, uploadId, sid, transcript, message.Length, sent.RefusedDuplicate);
                case DeliverySendKind.StillDelivering:
                    return HoldAsStillDelivering(store, uploadId, sid, DeliveryStates.Delivering);
                case DeliverySendKind.NoAnswer:
                    // Held - unless more than the limit has passed since Send, and then it is "could not confirm it
                    // arrived", even on a first attempt (change 1).
                    return HoldOrUnconfirmed(store, uploadId, sid, transcript, sentAtUtc, clock, sent.NoAnswerKind!);
                case DeliverySendKind.NeverSeen:
                    // The Director says it never saw the id - but the send's answer never came, so it may yet arrive.
                    // Held: the client's next attempt asks again, and "unknown" then counts as not in (contract section 4).
                    return HoldAsStillDelivering(store, uploadId, sid, DeliveryStates.Unknown);
                case DeliverySendKind.NotDelivered:
                    // The DIRECTOR ITSELF refused this send for age (contract section 9, F6): its check types nothing
                    // past the limit and answers not-delivered with reason too-old. Known not to in, and past the
                    // limit from Send by the Gateway's own clock: shown back too old with the words, and the decision
                    // record's director-answer line above already says the Director refused it for age.
                    if (sent.NeverLeft is false
                        && IsTooOld(clock, sentAtUtc, out var refusedAge)
                        && IsTheDirectorsAgeRefusal(sent.DirectorReason))
                        return ResolveTooOld(store, uploadId, sid, transcript, refusedAge);
                    // Definitely not in: the Gateway tries again itself (phase 5), and that attempt asks first and then
                    // types, under the age limit. A prompt that never left this Gateway is waiting for its Director.
                    FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: not delivered ({sent.Error}); " +
                        (sent.NeverLeft ? "waiting for the Director" : "the Gateway will try again"));
                    return HoldAsStillDelivering(store, uploadId, sid,
                        sent.NeverLeft ? DeliverySendAndAsk.WaitingForDirectorState : DeliverySendAndAsk.RetryingState);
                default:
                    throw new InvalidOperationException($"unknown delivery send outcome {sent.Kind}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId} FAILED: {ex.Message}; the Gateway will try again");
            store.RecordDecision(uploadId, DeliveryDecisions.CompleteError,
                new DeliveryDecisionFacts { StatusCode = StatusCodes.Status502BadGateway, Error = ex.Message });
            // After ownership a delivery is never answered 502 (contract section 2): the Gateway tries again itself.
            return HoldToRetry(store, uploadId, sid);
        }
        finally
        {
            // Issue #1181, Task 4: the transcription run is over (delivered, failed, or threw), so drop the
            // "Transcribing" mark. The durable PENDING/DELIVERED marker now owns the session's state.
            //
            // DELIBERATELY UNCONDITIONAL, even though the reachability gate above can now return before the
            // mark is ever set. The actively-transcribing map has NO idle backstop - it is bounded by the run
            // and nothing else - and DictationPhase.For paints "Transcribing" from it whatever the durable
            // record says, so an entry leaked by an earlier crashed run for this session would paint until
            // the Gateway restarted. This clear is that net, and guarding it on "did THIS run mark it" would
            // remove the net to answer a question the clear's idempotence already answers.
            transcribingSessions.ClearActivelyTranscribing(tenant, sid);
        }
    }

    /// <summary>
    /// The reason stamped on the durable record when a dictation is resolved because its session has exited.
    /// It is ITS OWN reason, not the moved-on one, because the two causes are different facts and the record
    /// is the only place either is answerable afterwards: "the session moved on" says other turns happened
    /// while the clip was in flight, and "the session exited" says there is no process left to type into
    /// ever again. Folding one into the other would make every count of stale drops silently include dead
    /// sessions, and the query the owner runs over these records ("what happened to my words?") would answer
    /// the wrong thing.
    /// </summary>
    internal const string ExitedSessionReason = DeliveryDecisions.SessionExited;

    /// <summary>
    /// The reason stamped on the durable record, and answered to the client, when a recording is shown back instead of
    /// sent because it is older than <see cref="CcDirector.Gateway.Contracts.MaxDeliveryAge"/> from Send. Its own reason
    /// for the same cause <see cref="ExitedSessionReason"/> has one: the two resolve with the same wire flags, and only
    /// the reason says which it was. One spelling, aliased from <see cref="DeliveryDecisions.TooOld"/>, which is the
    /// contracts' <see cref="CcDirector.Gateway.Contracts.MaxDeliveryAge.TooOldReason"/> - the word the Director writes
    /// its own age refusal with (Voice Delivery phase 5, F6).
    /// </summary>
    internal const string TooOldReason = DeliveryDecisions.TooOld;

    /// <summary>
    /// Whether a Director's not-delivered answer is its AGE REFUSAL (Voice Delivery phase 5, F6). The Director refuses a
    /// prompt older than the limit with the one reason word <see cref="TooOldReason"/> followed by the measured age and
    /// where it was measured (<c>PromptAgeLimit.TooOldReason</c> in Core: "too-old: the prompt was 5m 01s old from Send
    /// ..."), so the word is matched at the START of the reason, never as an exact string - and never a contains-match,
    /// which would read any other reason that happens to name the word as an age refusal. The word itself is the one
    /// named constant both halves spell it by.
    /// </summary>
    internal static bool IsTheDirectorsAgeRefusal(string? reason)
        => reason is not null
           && (string.Equals(reason, TooOldReason, StringComparison.Ordinal)
               || (reason.Length > TooOldReason.Length
                   && reason[TooOldReason.Length] == ':'
                   && reason.StartsWith(TooOldReason, StringComparison.Ordinal)));

    /// <summary>
    /// The reason stamped on the durable record, and answered to the client, when a recording is resolved as "could not
    /// confirm it arrived" (Voice Delivery phase 2, change 1): it was sent, the Director gave no answer of any kind to the
    /// question of what became of it, and more than <see cref="CcDirector.Gateway.Contracts.MaxDeliveryAge"/> have passed since Send. Its own
    /// reason beside <see cref="TooOldReason"/>: too old means the words are known NOT to be in, unconfirmed means they
    /// may be - so only too old offers "Send anyway" (<see cref="OffersSendAnyway"/>).
    /// </summary>
    internal const string UnconfirmedReason = DeliveryDecisions.Unconfirmed;

    /// <summary>
    /// Whether a shown-back recording (<c>movedOn</c>) offers "Send anyway" - the Gateway's ruling, read by the client
    /// verbatim (rule 7; phase 2, change 1). Decided from the record's reason alone, so the fresh answer, the cached
    /// re-complete and the register-time answer cannot disagree. True for <see cref="TooOldReason"/> (the words are
    /// known not to be in the session, and a live session may still take them) and for a tombstone written before
    /// reasons were kept. False for <see cref="UnconfirmedReason"/>, where the words may already be in and a second
    /// copy could double them - and, since phase 5 round 2 (contract section 9, F4), FALSE FOR
    /// <see cref="ExitedSessionReason"/> too: the session has ended, so there is nothing left to send to, and the
    /// client shows the words, the ended-session label and Dismiss. The Delivery Lead: "'Send anyway' is offered only
    /// when the Director has answered not-delivered or unknown (so we never offer a second copy that might double)";
    /// a dead session is the strongest "not deliverable" there is.
    /// </summary>
    internal static bool OffersSendAnyway(string? reason)
        => reason is null || string.Equals(reason, TooOldReason, StringComparison.Ordinal);

    /// <summary>
    /// A recording whose question got no answer of any kind: HELD while it is within the limit from Send, and resolved as
    /// "could not confirm it arrived" once it is past it - a DELIVERED tombstone, not sent, shown back, with the words
    /// (<paramref name="words"/>) kept and a decision line carrying the age and which kind of no answer it was. Never
    /// held forever (the Delivery Lead's ruling, change 1).
    /// </summary>
    private static DictationOutcome HoldOrUnconfirmed(VoiceUploadStore store, string uploadId, string sid, string words,
        DateTime sentAtUtc, TimeProvider clock, string noAnswerKind, string heldState = NoAnswerDirectorState)
    {
        if (!DeliverySendAndAsk.IsPastConfirmLimit(clock.GetUtcNow().UtcDateTime, sentAtUtc, out var age))
            return HoldAsStillDelivering(store, uploadId, sid, heldState);
        store.MarkDelivered(uploadId, submitted: false, movedOn: true, words, reason: UnconfirmedReason, age: age,
            directorNoAnswer: noAnswerKind);
        FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: {age.TotalSeconds:0}s since Send and the " +
            $"Director gave no answer ({noAnswerKind}); resolved as {UnconfirmedReason} with chars={words.Length}, nothing typed");
        return DictationOutcome.Submitted(false, true, words, UnconfirmedReason);
    }

    /// <summary>Whether a recording sent at <paramref name="sentAtUtc"/> is past the age limit now - strictly more than
    /// <see cref="CcDirector.Gateway.Contracts.MaxDeliveryAge"/>, so 5:00 is still sent and 5:01 is shown back.</summary>
    private static bool IsTooOld(TimeProvider clock, DateTime sentAtUtc, out TimeSpan age)
    {
        age = clock.GetUtcNow().UtcDateTime - sentAtUtc;
        return age > MaxDeliveryAge.Span;
    }

    /// <summary>
    /// Words known not to be in the session, past the age limit: resolved, not dropped. The DELIVERED tombstone keeps
    /// the words, so a re-complete returns them and never types them (issue #1183), and the client shows them with
    /// "Send anyway".
    /// </summary>
    private static DictationOutcome ResolveTooOld(VoiceUploadStore store, string uploadId, string sid, string transcript, TimeSpan age)
    {
        store.MarkDelivered(uploadId, submitted: false, movedOn: true, transcript, reason: TooOldReason, age: age);
        FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: {age.TotalSeconds:0}s since Send, " +
            $"more than {MaxDeliveryAge.Minutes} minutes; shown back as {TooOldReason} with chars={transcript.Length}, nothing typed");
        return DictationOutcome.Submitted(false, true, transcript, TooOldReason);
    }

    /// <summary>A Gateway-side problem, or words the Director said are not in: held as "retrying", because the Gateway
    /// owns the delivery and tries again itself (Voice Delivery phase 5).</summary>
    private static DictationOutcome HoldToRetry(VoiceUploadStore store, string uploadId, string sid)
        => HoldAsStillDelivering(store, uploadId, sid, DeliverySendAndAsk.RetryingState);

    /// <summary>
    /// Hold a recording that may already be in the session: the record stays PENDING (nothing is resolved, nothing
    /// deleted), the decision is written, and the client is answered 202 "still delivering" with the Director's state as
    /// it is told it. The client keeps its copy and completes again; that retry asks the Director first.
    /// </summary>
    private static DictationOutcome HoldAsStillDelivering(VoiceUploadStore store, string uploadId, string sid, string directorState)
    {
        DeliverySendAndAsk.RecordHeld(store, uploadId, sid, directorState);
        return DictationOutcome.StillDelivering(directorState);
    }

    /// <summary>
    /// The words are in: write the durable DELIVERED tombstone as the immediate next step, before anything else, to
    /// minimise the window in which a re-complete could re-inject (issue #1183). Known, deliberately unfixed residual:
    /// a Gateway crash in the few milliseconds before the marker lands lets a later re-complete run again - and that
    /// retry now asks the Director first, which answers delivered. MarkDelivered discards the chunks, keeps the marker.
    /// </summary>
    private static DictationOutcome ResolveDelivered(VoiceUploadStore store, string uploadId, string sid, string transcript,
        int characters, bool refusedDuplicate)
    {
        store.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript);
        FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: submitted chars={characters}" +
            (refusedDuplicate ? " (by an earlier attempt; this copy was refused by the Director)" : ""));
        return DictationOutcome.Submitted(true, false, transcript);
    }

    /// <summary>
    /// RESOLVE a dictation whose session can never receive it, instead of merely refusing it.
    ///
    /// This is the fix for the unbounded retry loop. Every other terminal outcome in
    /// <see cref="RunCompleteCoreAsync"/> writes a durable tombstone through
    /// <see cref="VoiceUploadStore.MarkDelivered"/>, so a re-complete short-circuits on the record and the
    /// client's driver stops. The exited arm was the ONE early return that wrote nothing: the record stayed
    /// PENDING, so the session stayed locked, the phone re-completed forever, and nothing in the system could
    /// ever end it. The owner's only remedy was to delete the session.
    ///
    /// WHY <c>movedOn</c> AND NOT <c>abandoned</c>. The flag is the WIRE contract, and the client already has
    /// exactly one arm that means "the server will never deliver this upload id; the words were NOT sent;
    /// keep the audio and tell the user" - that is the movedOn arm, which publishes a sticky, non-clearing
    /// notice and offers the words back. The alternatives are both worse and both silent in their own way:
    /// an ABANDONED tombstone lands on the client's abandoned arm, which is deliberately SILENT because a
    /// user who cancelled already knows - so a recording nobody cancelled would vanish without a word; and a
    /// plain delivered-nothing tombstone lands on the unheard arm, which DELETES the audio and tells the user
    /// nothing was heard, which is false twice over. So the flag stays movedOn and the RECORD carries the
    /// true cause in <see cref="ExitedSessionReason"/>.
    ///
    /// THE TRANSCRIPT IS HANDED BACK WHENEVER THERE IS ONE. From the cost gate there is none by design -
    /// that is the whole point of asking reachability before paying - and the recording is untouched on the
    /// device, so the client offers a fresh retry instead. From the DELIVERY gate the clip has already been
    /// transcribed, and those words are the user's: carrying them into the tombstone and the outcome is what
    /// lets the client show them and offer "Send anyway" into a live session, rather than throwing away
    /// speech we already have because the target died while we were listening to it.
    /// </summary>
    private static DictationOutcome ResolveAsUndeliverable(
        VoiceUploadStore store, string uploadId, string sid, string status, string transcript)
    {
        store.MarkDelivered(uploadId, submitted: false, movedOn: true, transcript, reason: ExitedSessionReason);
        FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: the session has exited " +
            $"(status={status}); resolved as {ExitedSessionReason} with chars={transcript.Length}, nothing injected");
        return DictationOutcome.Submitted(submitted: false, movedOn: true, transcript, ExitedSessionReason);
    }

    private static void EndTranscribing(TranscribingSessions t, TenantId tenant, string sid)
    {
        try { t.End(tenant, sid); } catch { /* the Gateway's stale-mark backstop clears it if this throws */ }
    }

    internal static bool IsExited(SessionDto session)
        => string.Equals(session.Status, "Exited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.Status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the Gateway can PROVE a session it cannot locate has ENDED - one of the only two proofs there are
    /// (contract section 9, F4): its Director - THE ONE THE DELIVERY'S OWN DURABLE RECORD NAMES - is connected and
    /// fresh, and its fresh snapshot no longer lists the session. Anything else - a stale Director, a disconnected
    /// one, no Director named on the record at all - is NOT ended and stays a held delivery (contract section 8):
    /// "a Director that is stale or unreachable is a HELD delivery, never 'session gone'". The freshness horizon is
    /// the locator's own (<see cref="GatewayEndpoints.LocateGrace"/> over the stream staleness), so "fresh" here
    /// means exactly what "locatable" means there.
    ///
    /// A RECORD THAT NAMES NO DIRECTOR PROVES NOTHING (no fallback programming). The owned record and its
    /// <see cref="DictationOwnedDelivery.DirectorId"/> ship in the same release, never deployed apart, so no
    /// real record exists without the field: one that names no Director is one whose session was never located
    /// by any attempt, and there is no second source for the fact. The in-memory owner cache is NOT consulted -
    /// any fresh roster read may prune it, so it is not a durable answer, and answering "ended" from it would be
    /// a second way to answer the same question. Such a record is held (section 8) and one log line says so.
    /// </summary>
    /// <param name="rememberedDirector">The Director a delivery's own durable record names as its session's
    /// Director, when it does; null otherwise.</param>
    internal static bool SessionEndedOnAFreshDirector(string? rememberedDirector,
        Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale, TenantId tenant, string sid)
    {
        if (string.IsNullOrWhiteSpace(rememberedDirector))
        {
            FileLog.Write($"[GatewayDictation] ended proof: sid={sid}: the delivery's record names no Director, " +
                "so an ending cannot be proved and the delivery stays held");
            return false;
        }
        if (pushedSessions is null) return false;
        var fresh = pushedSessions.TryGetFresh(tenant, rememberedDirector, streamStale + GatewayEndpoints.LocateGrace);
        return fresh is not null && fresh.All(s => !string.Equals(s.SessionId, sid, StringComparison.Ordinal));
    }
}

/// <summary>Register-time body: the session the recording is for.</summary>
public sealed class DictationUploadRequest
{
    public string? SessionId { get; set; }
}

/// <summary>Complete-time body.</summary>
public sealed class DictationCompleteRequest
{
    public string? SessionId { get; set; }
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
    public string? Ext { get; set; }
    /// <summary>Typed text before the caret (prepended to the transcript). Empty for the voice case.</summary>
    public string? Before { get; set; }
    /// <summary>Typed text after the caret (appended to the transcript). Empty for the voice case.</summary>
    public string? After { get; set; }
    /// <summary>Earlier paused dictation segments already turned to text, joined ahead of this clip.</summary>
    public string? Prefix { get; set; }
    /// <summary>
    /// When the owner pressed Send, in UTC: an ISO 8601 string ending in Z (the client stamps it with
    /// <c>new Date(ms).toISOString()</c>). Required - a complete without it is refused with 400. The age limit
    /// (<see cref="CcDirector.Gateway.Contracts.MaxDeliveryAge"/>) is measured from it.
    /// </summary>
    public DateTime? SentAtUtc { get; set; }
    /// <summary>True for every attempt after the client's first. It decides nothing: it is written to the decision
    /// log as the "retried" line, so a retry can be read afterwards.</summary>
    public bool Resumed { get; set; }
    /// <summary>Capture-health (issue #863), optional - present when the mobile Send path measured the clip:
    /// recording wall-clock, decoded audio duration, and source blob size. When present the complete handler
    /// persists a dictation session record so the fire-and-forget Send path's audio loss lands in the same
    /// log as the Voice-mode and desktop paths. Diagnostics only; omitting it changes nothing.</summary>
    public double? ClientRecordedMs { get; set; }
    public double? ClientDecodedSeconds { get; set; }
    public long? ClientSourceBytes { get; set; }
    /// <summary>Which browser shell recorded the clip ("cockpit-send" / "mobile-send"). Every browser
    /// used to be logged as "mobile-send" here, which is why per-surface audio loss could not be read
    /// out of the log. Absent from an older client (or a clip queued before this shipped), which falls
    /// back to the literal this path always wrote.</summary>
    public string? ClientSurface { get; set; }
}

/// <summary>Terminal or retryable outcome of a dictation complete, mapped to an HTTP result.</summary>
internal sealed class DictationOutcome
{
    private enum Kind { Submitted, Error, Incomplete, OutOfCredits, Dropped, Permanent, StillDelivering }
    private readonly Kind _kind;
    private readonly bool _submitted;
    private readonly bool _movedOn;
    private readonly string _transcript;
    private readonly string? _reason;
    private readonly string? _directorState;
    private readonly int _status;
    private readonly string? _error;
    private readonly HostedAiState _creditsState;
    private readonly IReadOnlyList<int> _missing;

    private DictationOutcome(Kind kind, bool submitted = false, bool movedOn = false, string transcript = "",
        int status = 0, string? error = null, HostedAiState creditsState = default, IReadOnlyList<int>? missing = null,
        string? reason = null, string? directorState = null)
    {
        _kind = kind;
        _submitted = submitted;
        _movedOn = movedOn;
        _transcript = transcript;
        _reason = reason;
        _directorState = directorState;
        _status = status;
        _error = error;
        _creditsState = creditsState;
        _missing = missing ?? Array.Empty<int>();
    }

    /// <summary>Terminal: the server resolved the clip (submitted, moved-on, empty, or an abandoned
    /// upload id that was dropped). Do not retry.</summary>
    public bool Terminal => _kind == Kind.Submitted || _kind == Kind.Dropped;

    /// <summary>The upload is missing chunks and the client will complete again on the same upload id, so
    /// the session is still genuinely transcribing - the ONE outcome that must NOT clear the orange mark
    /// (issue #1048).</summary>
    public bool IsIncomplete => _kind == Kind.Incomplete;

    /// <summary>Held, not final: the 202 "still delivering" answer. The Gateway's driver attempts it again.</summary>
    public bool IsHeld => _kind == Kind.StillDelivering;

    /// <param name="reason">Why a recording was shown back instead of sent (<c>too-old</c>, <c>session-exited</c>,
    /// <c>unconfirmed</c>), on a
    /// moved-on outcome only; null on a tombstone the old byte rule wrote. Ignored when not moved on.</param>
    public static DictationOutcome Submitted(bool submitted, bool movedOn, string transcript, string? reason = null)
        => new(Kind.Submitted, submitted: submitted, movedOn: movedOn, transcript: transcript, reason: movedOn ? reason : null);
    /// <summary>
    /// HELD, NOT FAILED (Voice Delivery mission, phase 2): the words may already be in the session - the Director said
    /// it is still delivering them, said it never saw them after a send whose answer never came, or gave no answer -
    /// so the client keeps its copy, shows "Still delivering" and asks again. The record stays PENDING. Not terminal:
    /// the next complete re-runs the core, which asks the Director first. HTTP 202.
    /// </summary>
    public static DictationOutcome StillDelivering(string directorState)
        => new(Kind.StillDelivering, directorState: directorState);
    public static DictationOutcome Error(int status, string error) => new(Kind.Error, status: status, error: error);
    public static DictationOutcome Incomplete(IReadOnlyList<int> missing) => new(Kind.Incomplete, missing: missing);
    public static DictationOutcome OutOfCredits(HostedAiState state) => new(Kind.OutOfCredits, creditsState: state);
    /// <summary>An ABANDONED upload id: the dictation was given up, so a re-complete returns a clear dropped
    /// outcome and never injects (issue #1183). Terminal - the client drops its copy and does not re-drive.</summary>
    public static DictationOutcome Dropped(string reason) => new(Kind.Dropped, error: reason);
    /// <summary>A PERMANENT transcription failure (issue #1185): this attempt is over (clears the orange
    /// mark, HTTP 422 { permanent, reason }), but the record is parked FAILED and is user-retryable - so it
    /// is NOT Terminal (a retry re-runs, it is not cached in _completes) and NOT Incomplete.</summary>
    public static DictationOutcome Permanent(string reason) => new(Kind.Permanent, error: reason);

    public IResult ToResult() => _kind switch
    {
        Kind.Submitted => _movedOn
            ? Results.Json(new { submitted = _submitted, movedOn = true, transcript = _transcript, reason = _reason,
                offerSendAnyway = GatewayDictationEndpoint.OffersSendAnyway(_reason) })
            : Results.Json(new { submitted = _submitted, movedOn = false, transcript = _transcript }),
        Kind.StillDelivering => Results.Json(new { delivering = true, directorState = _directorState },
            statusCode: StatusCodes.Status202Accepted),
        Kind.Dropped => Results.Json(new { submitted = false, movedOn = false, dropped = true, reason = _error ?? "" }),
        Kind.Permanent => Results.Json(new { permanent = true, reason = _error ?? "" }, statusCode: StatusCodes.Status422UnprocessableEntity),
        Kind.Incomplete => Results.Json(new { status = "incomplete", missing = _missing }, statusCode: StatusCodes.Status409Conflict),
        Kind.OutOfCredits => HostedAiHttp.PaymentRequiredResult(_creditsState),
        _ => Results.Json(new { error = _error }, statusCode: _status),
    };
}

/// <summary>
/// The answer of <c>GET /dictation/{uploadId}/decisions</c>: how the upload's record reads, its state, and every
/// decision the Gateway wrote for it, in order. Deliberately carries no transcript: lengths and states only.
/// </summary>
public sealed class DictationDecisionsResponse
{
    public string UploadId { get; init; } = "";
    /// <summary>How the record read: Present, Absent, Malformed, Unreadable.</summary>
    public string Record { get; init; } = "";
    /// <summary>The record's state when it could be read: Pending, Delivered, Abandoned, Failed, Acknowledged.</summary>
    public string? State { get; init; }
    /// <summary>For an acknowledged record, the state it was in when the client acknowledged it.</summary>
    public string? AcknowledgedFrom { get; init; }
    public bool? Submitted { get; init; }
    public bool? MovedOn { get; init; }
    public string? Reason { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyList<DeliveryDecisionLine> Decisions { get; init; } = Array.Empty<DeliveryDecisionLine>();
}
