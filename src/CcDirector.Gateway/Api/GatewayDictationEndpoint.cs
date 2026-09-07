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
///   POST /dictation/upload               { sessionId, baselineBufferBytes } + Idempotency-Key -> { upload_id }
///   PUT  /dictation/{uploadId}/chunk/{i}  octet-stream + X-Chunk-Sha256                        -> { ok }
///   POST /dictation/{uploadId}/complete   { sessionId,totalChunks,mime,ext,before,after,baselineBufferBytes,resumed }
///                                          -> 200 { submitted, movedOn, transcript } | 200 { dropped, reason } | 409 { missing } | 402 | 5xx
///   POST /dictation/{uploadId}/ack        -> 200 { ok, retired }
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
    // How much the session's terminal output may grow after a RESUMED clip was recorded before we treat
    // it as "moved on" and refuse to inject the now-stale dictation (issue #1006 guard). Immediate
    // (non-resumed) sends always inject; the 1-hour staging sweep is the hard backstop for staleness.
    private const long MovedOnBufferGrowthBytes = 512;

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
        TimeSpan? streamStale = null)
    {
        // Gateway Cleanup mission, Phase 2 (PR E-B): resolve the owning Director push-store-first and inject
        // the dictation through the tunnel-first SessionVerbClient (the delivery marker rides the PromptRequest
        // DeliveryUploadId field, not an HTTP header), so this path no longer HTTP-dials the Director.
        var stale = streamStale ?? TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);
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

            // A completion attempt is progress - keep the orange mark alive across the server-side
            // transcribe so a slow transcribe cannot let it age out mid-flight (issue #1126).
            transcribingSessions.Refresh(tenant, req.SessionId!);

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
            // A FAILED (parked) record is user-retryable, NOT a terminal short-circuit (issue #1185): this
            // complete IS the explicit retry, so clear the FAILED marker back to PENDING (keeping the staged
            // chunks) and re-drive the real work below.
            if (settled is { State: DictationDeliveryState.Failed })
            {
                store.ClearFailed(uploadId);
                FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: cleared FAILED, retrying");
            }

            // Per-uploadId single-flight: concurrent or retried completes await the SAME in-flight run, so a
            // still-PENDING id is assembled + transcribed + injected at most once even under a concurrent
            // race. The entry is dropped once the run settles (below); the durable tombstone owns de-dupe
            // from then on, so there is no age-swept cache window.
            var entry = _completes.GetOrAdd(tenant, uploadId, completeKey =>
            {
                OnCompleteEntryCreatedForTests?.Invoke(completeKey);
                return new CompleteEntry(new Lazy<Task<DictationOutcome>>(() => RunCompleteCoreAsync(
                    uploadId, tenant, req, store, registry, owners, transcription, transcribingSessions, deliverySurface, deliveryIdentityKind,
                    pushedSessions, sendCommand, stale)));
            });

            DictationOutcome outcome;
            try
            {
                outcome = await entry.Task.Value;
            }
            catch (Exception ex)
            {
                _completes.Remove(tenant, uploadId); // transient: let a retry re-run
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }

            // The Gateway OWNS the orange "Transcribing..." mark, so it clears it here - OUTSIDE the
            // single-flight cache - on EVERY terminal outcome (issue #1048, extended by #1126). Doing the
            // clear here, not inside the cached RunCompleteCoreAsync, means a RESENT completion for an
            // already-finished upload id (which returns the cached outcome WITHOUT re-running the core, so
            // the old in-core clear never fired again) still clears the mark. The ONLY outcome we keep the
            // mark for is an incomplete upload: more chunks are still coming and the client completes again
            // on the same upload id, so the session is genuinely still transcribing.
            if (!outcome.IsIncomplete)
            {
                EndTranscribing(transcribingSessions, tenant, req.SessionId!);
                _uploadSids.Remove(tenant, uploadId);
            }
            // Drop the in-memory single-flight entry once the run settles. A terminal run has already
            // written the durable tombstone (inside the core), so a later retry short-circuits on the
            // ReadRecord check above; a non-terminal run (transient error, incomplete upload, out-of-credits)
            // leaves no tombstone, so the next complete re-runs the real work (issue #1183).
            _completes.Remove(tenant, uploadId);
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
            var read = store.Read(uploadId);
            if (read.Refuses)
            {
                FileLog.Write($"[GatewayDictation] abandon REFUSED: {read.Describe(uploadId)}; marker left as it is");
                return RecordRefusal(read, uploadId);
            }
            var existing = read.Record;
            if (existing is { State: DictationDeliveryState.Delivered })
            {
                FileLog.Write($"[GatewayDictation] abandon uploadId={uploadId}: already DELIVERED, not abandoning");
                return Results.Json(new { ok = true, upload_id = uploadId, abandoned = false, already_delivered = true });
            }

            store.MarkAbandoned(uploadId, "user_abandoned");
            // Clear the in-memory transcribing marks so the roster un-oranges at once (the durable PENDING
            // marker - the "Uploading from phone" source - is already gone via MarkAbandoned). Keyed to the
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
    private static DictationOutcome TerminalOutcome(DictationDeliveryRecord record)
        => record.State == DictationDeliveryState.Abandoned
            ? DictationOutcome.Dropped(record.Reason ?? "")
            : DictationOutcome.Submitted(record.Submitted, record.MovedOn, record.Transcript);

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
            : Results.Json(new { upload_id = uploadId, terminal = true, submitted = record.Submitted, movedOn = record.MovedOn, dropped = false, transcript = record.Transcript });

    private static async Task<DictationOutcome> RunCompleteCoreAsync(
        string uploadId, TenantId tenant, DictationCompleteRequest req, VoiceUploadStore store, DirectorRegistry registry,
        SessionOwnerCache? owners, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, string? deliverySurface, string deliveryIdentityKind,
        Streaming.PushedSessionStore? pushedSessions, DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        TimeSpan streamStale)
    {
        var sid = req.SessionId!;
        // Issue #1181, Task 4: this run assembles + transcribes + delivers, so mark the session ACTIVELY
        // transcribing for its duration. The aggregator reads this to show "Transcribing" (vs the durable
        // PENDING marker's "Uploading from phone"). Cleared in the finally so it never outlives the run.
        transcribingSessions.MarkActivelyTranscribing(tenant, sid);
        try
        {
            // The configured mode's key must be present before we pay the reassembly + transcribe cost.
            var routing = transcription.Resolve();
            if (routing.Key is null)
                return DictationOutcome.Error(StatusCodes.Status503ServiceUnavailable,
                    $"no key configured for transcription mode {routing.Mode}");

            var assembled = await store.AssembleAsync(uploadId, req.TotalChunks);
            if (assembled.Status == "unknown_upload")
                return DictationOutcome.Error(StatusCodes.Status404NotFound, "unknown upload id");
            if (assembled.Status == "incomplete")
                return DictationOutcome.Incomplete(assembled.Missing);
            var audio = assembled.Audio;
            if (audio is null || audio.Length == 0)
            {
                store.Delete(uploadId);
                return DictationOutcome.Error(StatusCodes.Status502BadGateway, "assembled recording was empty");
            }

            var result = await transcription.TranscribeAsync(
                audio, "audio." + (req.Ext ?? "wav"), req.Mime ?? "audio/wav", applyCorrection: true, CancellationToken.None,
                tenant: tenant, source: "dictation");
            if (MapNonOkTranscription(result, uploadId, store) is { } nonOk)
                return nonOk;

            var transcript = (result.Text ?? "").Trim();
            // Capture-health (issue #863): persist the fire-and-forget Send path's audio-loss deficit into
            // the SAME dictation session log the Voice-mode and desktop paths write, via the one shared
            // helper. The assembled audio byte count is what the server actually transcribed. When the client
            // did not send its measurements this is a no-op. Fire-and-forget - never affects the outcome.
            MobileCaptureHealthLog.Persist(
                uploadId, MobileCaptureHealthLog.SurfaceOr(req.ClientSurface, "mobile-send"),
                req.ClientRecordedMs, req.ClientDecodedSeconds, req.ClientSourceBytes,
                audio.Length, transcript);
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

            // Gateway Cleanup mission, Phase 2: resolve the owner push-store-first (no HTTP fan-out) and gate
            // on an exited session, exactly as the old LocateAsync did, then reach it through the tunnel.
            //
            // Located in the REQUEST'S OWN TENANT (issue #1884, un-deny). This is what makes dictation actually
            // WORK on the hosted Gateway: the tenant was resolved from the authenticated device key at the gate
            // and carried in here, so the session is found in the caller's partition and NEVER another
            // account's. On self-host the tenant is Local and this is byte-identical to before. A caller whose
            // tenant did not resolve never reached this leg - the gate refused it up front - so there is no
            // path here that falls back to a shared/Local locate on hosted.
            var (director, session) = await GatewayEndpoints.LocateSessionAsync(
                registry, sid, pushedSessions, streamStale, tenant, owners);
            if (director is null || session is null)
                return DictationOutcome.Error(StatusCodes.Status404NotFound, "session not found");
            if (IsExited(session))
                return DictationOutcome.Error(StatusCodes.Status410Gone, "session has exited");
            var route = new SessionVerbClient(director, sendCommand);

            // Moved-on guard (issue #1006): for a RESUMED clip, if the session's terminal output grew
            // materially since the clip was recorded, other turns happened - drop the stale dictation
            // rather than inject it into a session that has moved on. Immediate sends skip this.
            //
            // The baseline is the LARGER of what the phone recorded and what a previous FAILED delivery
            // attempt of THIS upload id re-baselined to (Lost Dictations mission, issue #1593). The phone's
            // baseline is stamped once, when the clip was recorded, and never moves - so after one of our own
            // failed attempts typed the text and cleared it again, the phone's baseline describes a terminal
            // that no longer exists, and the retry gets dropped as "moved on" by OUR OWN noise. Taking the
            // larger of the two costs nothing when no attempt failed (the stored value is absent) and is what
            // stops the observed drop when one did.
            //
            // The STRICT read, deliberately (issue #2745): the complete leg has already refused a marker it
            // cannot read before this core ever runs, so a throw here can only mean the marker went bad between
            // that check and this one. It lands in the complete handler's catch as a 502 with the file named,
            // and nothing is injected - which is the right answer for a marker nobody can read.
            var effectiveBaseline = Math.Max(
                req.BaselineBufferBytes, store.ReadRecord(uploadId)?.RebaselineBufferBytes ?? 0);
            if (req.Resumed && session is not null && req.BaselineBufferBytes > 0 &&
                session.TotalBufferBytes > effectiveBaseline + MovedOnBufferGrowthBytes)
            {
                // The turn is resolved (deliberately dropped as stale): a durable DELIVERED tombstone with
                // movedOn set, so a re-complete returns the same moved-on outcome and never injects the stale
                // clip (issue #1183). Discards the retained chunks, keeps the marker.
                store.MarkDelivered(uploadId, submitted: false, movedOn: true, transcript);
                FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: session moved on " +
                    $"(buffer {req.BaselineBufferBytes}/effective {effectiveBaseline}->{session.TotalBufferBytes}); dropped");
                return DictationOutcome.Submitted(false, true, transcript);
            }

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
            var (ok, _, err) = await route.PostPromptAsync(sid, new PromptRequest
            {
                Text = message,
                AppendEnter = true,
                Surface = deliverySurface ?? "unknown",
                DeliveryUploadId = spokenAlone ? uploadId : null,
                Provenance = new SubmissionProvenanceDto
                {
                    Route = SubmissionRoutes.GatewayDictation,
                    IdentityKind = deliveryIdentityKind,
                    TranscriptId = uploadId,
                    SpokenSpans = spokenSpans,
                },
            });
            if (!ok)
            {
                // THE ATTEMPT WE JUST MADE INVALIDATED THE PHONE'S BASELINE (Lost Dictations mission, #1593).
                // A failed submit is not a no-op on the terminal: the observed failure (05:31 on 2026-07-15)
                // typed the text twice and cleared it twice, writing ~8,700 bytes of our own noise into the
                // buffer. The phone's baseline was stamped when the clip was RECORDED and never moves, so the
                // retry that follows this 502 would be judged against a number our own failure just made a
                // lie, and the moved-on guard would drop the user's words as stale. Re-baseline the upload id
                // to the freshest buffer position we can see, so the retry is judged honestly.
                //
                // Re-READ the session rather than reusing the `session` snapshot from before the attempt:
                // that snapshot predates the noise by definition and would re-baseline to the same number the
                // phone already sent, which is no re-baseline at all.
                //
                // NOT PROVEN TO CLOSE THE WINDOW COMPLETELY, and deliberately not dressed up as if it does:
                // this reads the pushed store, so it sees only what the owning Director has pushed BY NOW. If
                // the push stream lags the failure, some of the attempt's noise is not in this number yet and
                // a retry could still be judged against a baseline that is too low. It is strictly better than
                // today (today re-baselines by exactly zero) and it is monotonic, so a later failed attempt
                // can only improve it - but the residual lag window is real and is not closed here.
                var (_, freshSession) = await GatewayEndpoints.LocateSessionAsync(
                    registry, sid, pushedSessions, streamStale, tenant, owners);
                if (freshSession is not null)
                    store.RecordFailedDeliveryBaseline(uploadId, freshSession.TotalBufferBytes);
                FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: submit FAILED ({err}); " +
                    $"re-baselined to {freshSession?.TotalBufferBytes.ToString() ?? "unavailable"} " +
                    $"(was {req.BaselineBufferBytes}) so the retry is not dropped as stale");
                return DictationOutcome.Error(StatusCodes.Status502BadGateway, err ?? "submit to session failed");
            }

            // Write the durable DELIVERED tombstone as the IMMEDIATE next step after the session accepted the
            // prompt, before anything else, to minimize the window in which a re-complete could re-inject
            // (issue #1183). Known, deliberately unfixed residual limitation: a Gateway crash in the few
            // milliseconds between PostPromptAsync returning and this marker landing on disk would let a
            // later re-complete inject the turn a second time. We minimize and document it rather than paper
            // over it with a fallback. MarkDelivered discards the retained chunks and keeps the marker.
            store.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript);
            FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: submitted chars={message.Length}");
            return DictationOutcome.Submitted(true, false, transcript);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId} FAILED: {ex.Message}");
            return DictationOutcome.Error(StatusCodes.Status502BadGateway, ex.Message);
        }
        finally
        {
            // Issue #1181, Task 4: the transcription run is over (delivered, failed, or threw), so drop the
            // "Transcribing" mark. The durable PENDING/DELIVERED marker now owns the session's state.
            transcribingSessions.ClearActivelyTranscribing(tenant, sid);
        }
    }

    private static void EndTranscribing(TranscribingSessions t, TenantId tenant, string sid)
    {
        try { t.End(tenant, sid); } catch { /* the Gateway's stale-mark backstop clears it if this throws */ }
    }

    private static bool IsExited(SessionDto session)
        => string.Equals(session.Status, "Exited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.Status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Register-time body: the session and the client's record-time terminal-byte baseline.</summary>
public sealed class DictationUploadRequest
{
    public string? SessionId { get; set; }
    public long BaselineBufferBytes { get; set; }
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
    /// <summary>The session's TotalBufferBytes when the clip was recorded (for the moved-on guard).</summary>
    public long BaselineBufferBytes { get; set; }
    /// <summary>True when this complete is a resume after a reload/relaunch (applies the moved-on guard).</summary>
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
    private enum Kind { Submitted, Error, Incomplete, OutOfCredits, Dropped, Permanent }
    private readonly Kind _kind;
    private readonly bool _submitted;
    private readonly bool _movedOn;
    private readonly string _transcript;
    private readonly int _status;
    private readonly string? _error;
    private readonly HostedAiState _creditsState;
    private readonly IReadOnlyList<int> _missing;

    private DictationOutcome(Kind kind, bool submitted = false, bool movedOn = false, string transcript = "",
        int status = 0, string? error = null, HostedAiState creditsState = default, IReadOnlyList<int>? missing = null)
    {
        _kind = kind;
        _submitted = submitted;
        _movedOn = movedOn;
        _transcript = transcript;
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

    public static DictationOutcome Submitted(bool submitted, bool movedOn, string transcript)
        => new(Kind.Submitted, submitted: submitted, movedOn: movedOn, transcript: transcript);
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
        Kind.Submitted => Results.Json(new { submitted = _submitted, movedOn = _movedOn, transcript = _transcript }),
        Kind.Dropped => Results.Json(new { submitted = false, movedOn = false, dropped = true, reason = _error ?? "" }),
        Kind.Permanent => Results.Json(new { permanent = true, reason = _error ?? "" }, statusCode: StatusCodes.Status422UnprocessableEntity),
        Kind.Incomplete => Results.Json(new { status = "incomplete", missing = _missing }, statusCode: StatusCodes.Status409Conflict),
        Kind.OutOfCredits => HostedAiHttp.PaymentRequiredResult(_creditsState),
        _ => Results.Json(new { error = _error }, statusCode: _status),
    };
}
