using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Network;
using CcDirector.Core.Recording;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Maps the <c>/ingest</c> REST surface the phone recorder uploads to. The phone records offline; when it
/// has connectivity it registers a recording, PUTs each finalized audio segment (idempotent by index +
/// hash), then POSTs <c>complete</c>, which queues the recording and returns 202 immediately. A background
/// worker transcribes every segment through the existing dictation pipeline and assembles + cleans the
/// transcript into the local transcripts area, retrying flaky segments and re-queueing a failed job for a
/// later attempt. The phone polls <c>status</c> to watch progress. Transcripts stay local (transient) until
/// the user promotes one into the vault. A shared dictation glossary lives under the same prefix.
///
/// Routes (all JSON except the raw-bytes chunk PUT), all under <c>/ingest</c>:
///   POST   /ingest/recording                      register, body RecordingRegisterRequest
///   PUT    /ingest/recording/{id}/chunk/{index}   raw audio bytes, header X-Chunk-Sha256
///   POST   /ingest/recording/{id}/complete        body RecordingManifest
///   GET    /ingest/recording/{id}/status          RecordingStatusDto
///   GET    /ingest/recordings                     list EVERY recording on this gateway
///   GET    /ingest/recording/{id}/transcript      the cleaned transcript as plain text
///   GET    /ingest/recording/{id}/audio/{index}   one raw audio segment
///   POST   /ingest/recording/{id}/promote         copy transcript + audio into the vault
///   PATCH  /ingest/recording/{id}/meta            set human-readable title/subtitle/summary
///   DELETE /ingest/recording/{id}                 delete the transient local transcript
///   GET    /ingest/dictionary                     the shared dictation glossary
///   PUT    /ingest/dictionary                     replace the shared dictation glossary
///   POST   /ingest/dictionary/terms               add terms to the shared dictation glossary
///   GET    /ingest/agent-info                     copy-paste API guide for an external agent
///
/// Auth is the Gateway's existing token middleware (applied host-wide when enabled), so these routes
/// inherit it without extra checks here.
///
/// DENIED IN WHOLE ON HOSTED. Every route under <c>/ingest</c> is refused on the hosted Gateway, because
/// nothing in this surface carries a tenant. The durable directory for a recording is built from the
/// CALLER-SUPPLIED recording id alone, so on shared hosted infrastructure any authenticated device can name
/// another account's recording and be served it: list every record on the box, read its raw audio and its
/// raw and cleaned transcript, overwrite its title, subtitle, summary and chunks, promote it into the vault,
/// or delete it outright. The id sanitiser only replaces invalid characters, so two distinct caller ids can
/// alias onto the same directory as well. The dictionary routes have the same shape: one shared glossary
/// file, no tenant, read and written by anyone. It is a deny of the WHOLE GROUP, not a guard on the worst
/// route, because the read, the write and the destruction are all equally wrong here, and because a
/// route-by-route fix rots: the next ingest route added would be open again by default.
///
/// It is a DENY rather than a per-tenant partition because partitioning this store is real work - the
/// on-disk layout, the promote target and the shared glossary all have to grow an owner - and a
/// half-partition is worse than an honest refusal. (Contrast the cached wingman voice READ surface, which
/// WAS partitioned per tenant in #1973 and is therefore served, not denied - the two surfaces looked alike
/// but had different work done to them.)
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE CHECK. This group is denied
/// through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny family
/// on this Gateway adopts (primitive at <c>src/CcDirector.Gateway/Tenancy/HostedRouteDeny.cs</c>; the
/// key-vault group in <see cref="VaultEndpoints"/> is the reference adoption). On hosted the handlers are
/// NEVER MAPPED. In their place the exclusive prefix maps ONE verb-less catch-all refusal over everything
/// under <c>/ingest</c> plus a root refusal at the prefix itself. There is no binding step to get ahead of,
/// no body parameter, no media-type constraint and no method constraint, so EVERY request shape - a valid
/// body, a malformed body, a wrong media type, a verb the group never mapped, and a route added LATER -
/// meets the refusal. The exclusivity claim is CHECKED at startup by
/// <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/>: the Gateway refuses to start if any live route
/// serves beneath <c>/ingest</c>. Nothing else on this Gateway serves under <c>/ingest</c>, so the whole
/// prefix is this group's to claim.
///
/// NO OFF-ROUTE WRITER TO HOST-GATE. Unlike the key-vault group (which had a startup seed, a provisioner and
/// a revocation firing off-route), NOTHING writes this surface's state except the routes themselves.
/// <see cref="RecordingIngestService"/> is constructed in exactly one place - <see cref="BuildService"/>,
/// reached only through the per-request Lazy below - and on hosted the catch-all answers before any handler
/// runs, so the Lazy is never forced, the service is never built and its transcription worker never starts.
/// The shared glossary is written only by the two denied dictionary routes. (DictionaryResolver's cache
/// write is a Director-side consumer of a Gateway glossary, not a Gateway writer of this store.) So there is
/// no defence-in-depth writer to gate; the deny is the whole mechanism.
///
/// UN-DENY DEBT: a deny closes the ROUTE door only. Before it is lifted: (1) give recordings and the shared
/// glossary a per-tenant layout (keyed by tenant, not by caller-supplied id alone, which also closes the
/// id-aliasing hazard), (2) quarantine, purge or migrate the PRE-DENY root rather than adopting un-owned
/// material into the new layout, and ONLY THEN (3) lift this refusal. This is recorded on the payload's
/// unDenyInstruction so it travels with the deny.
///
/// Self-host is COMPLETELY unchanged - the owner records, transcribes, edits and deletes exactly as before -
/// and that is the control. Off hosted the primitive maps the real handlers on the group exactly as an
/// unguarded builder would and creates no refusal at all.
/// </summary>
internal static class RecordingEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The exclusive prefix the recording-ingest group owns outright on hosted.</summary>
    internal const string Prefix = "/ingest";

    /// <summary>
    /// Maps the recording-ingest routes so they SERVE per-tenant (issues #2058/#2060). Each route resolves the
    /// caller's tenant and dispatches to that tenant's own recording store / glossary, answering 403 when no
    /// tenant resolves - never the Local partition. Self-host resolves to the single Local tenant, unchanged.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder outer,
        bool hosted,
        // The auth-boundary tenant binder. REQUIRED AND NON-NULLABLE (finding I1-01): when this defaulted to
        // null, a forgotten argument compiled cleanly and the resolver behind it decided on the argument. A
        // self-host caller constructs the boundary over the SingleTenantContext, which always resolves Local.
        Tenancy.HostedTenantBoundary tenantBoundary,
        KeyVault? keyVault = null,
        TranscriptionHistoryLog? history = null,
        TranscriptionAudioArchive? audioArchive = null,
        DictionarySuggestionService? suggestions = null,
        DictionarySuggestionDismissalStore? dismissals = null,
        SuggestionEmailComposer? emailComposer = null)
    {
        FileLog.Write($"[RecordingEndpoints] mapping {Prefix} recording + dictionary routes PER-TENANT (issues #2058/#2060); hosted={hosted} - each route resolves the caller's tenant and answers 403 when none resolves");

        // Un-denied (issues #2058/#2060): the routes SERVE per-tenant instead of the whole /ingest prefix
        // being refused on hosted. They map under the same prefix on the ungrouped builder; each handler
        // resolves the caller's tenant and dispatches to that tenant's own recording store / glossary.
        var app = outer.MapGroup(Prefix);
        // The archive carries the mode it writes under, so a mapper that was handed none builds one for its own mode.
        MapRoutes(app, tenantBoundary, keyVault, history, audioArchive ?? new TranscriptionAudioArchive(hosted), suggestions, dismissals, emailComposer);
    }

    /// <summary>
    /// Every /ingest route, mapped relative to the <see cref="Prefix"/> so the full paths are
    /// <c>/ingest/recording</c>, <c>/ingest/dictionary</c> and so on exactly as before. Each route resolves
    /// the CALLER's tenant and serves only that tenant's partition; a request with no resolvable tenant is
    /// refused (403), never served the Local partition.
    /// </summary>
    private static void MapRoutes(
        IEndpointRouteBuilder app,
        Tenancy.HostedTenantBoundary tenantBoundary,
        KeyVault? keyVault,
        TranscriptionHistoryLog? history,
        TranscriptionAudioArchive audioArchive,
        DictionarySuggestionService? suggestions = null,
        DictionarySuggestionDismissalStore? dismissals = null,
        SuggestionEmailComposer? emailComposer = null)
    {
        // Lazily built on FIRST USE, not at host startup: constructing the service resolves
        // the OpenAI API key (the transcriber needs it), and the Gateway must boot on machines
        // without that key. A missing key then fails the individual recording request loudly
        // (500 with an explicit hosted-AI setup message) instead of preventing
        // the entire Gateway host from starting. On hosted the group discards every handler, so this
        // Lazy is never forced and the service is never built.
        // In production the host owns the key vault + local history + audio archive and passes them, so the
        // recording transcriber shares the host's single instances rather than newing its own copies.
        // Per-tenant recording services (issues #2058/#2060). Each tenant gets its OWN RecordingIngestService
        // over its OWN root directory, so one account's recordings/transcripts and its glossary are physically
        // partitioned from another's - the recording directory is now keyed by (tenant, id), not the
        // caller-supplied id alone. Built lazily per tenant on first use; on self-host the single Local tenant
        // maps to the existing flat root, so nothing moves there. The cleanup pass reads the SAME tenant's
        // glossary, so a per-tenant glossary edit changes only that tenant's transcripts. (The glossary is
        // never sent to the speech-to-text provider - it is applied to the finished transcript; issue 2481.)
        var services = new System.Collections.Concurrent.ConcurrentDictionary<string, RecordingIngestService>(StringComparer.Ordinal);
        RecordingIngestService ServiceFor(TenantId tenant)
            => services.GetOrAdd(tenant.Value, _ => BuildService(tenant, keyVault, history, audioArchive));

        app.MapPost("/recording", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var req = await JsonSerializer.DeserializeAsync<RecordingRegisterRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (req is null || string.IsNullOrWhiteSpace(req.RecordingId))
                    return Results.BadRequest(new { error = "RecordingId is required" });
                var status = ServiceFor(t.Value).Register(req);
                return Results.Json(status);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] register bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapPut("/recording/{id}/chunk/{index:int}", async (string id, int index, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                    return Results.BadRequest(new { error = "empty chunk body" });

                await ServiceFor(t.Value).StoreChunkAsync(id, index, bytes, sha, ctx.RequestAborted);
                return Results.Json(new { ok = true, index, bytes = bytes.Length });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] chunk store failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/recording/{id}/complete", async (string id, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var manifest = await JsonSerializer.DeserializeAsync<RecordingManifest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (manifest is null)
                    return Results.BadRequest(new { error = "manifest body required" });
                // The audio completeness gate (issue #586) runs inside CompleteAsync.
                // Three outcomes:
                //   - "incomplete": a segment is missing or its SHA256/byte count
                //     does not match the manifest. NOTHING is transcribed; the
                //     response names MissingOrBadIndices so the phone re-sends
                //     exactly those, then calls complete again. Returned as 409
                //     Conflict (the upload conflicts with the declared manifest)
                //     so the phone treats it as "resend", never "done".
                //   - all-pass: enqueue and return 202. Transcription runs in the
                //     background worker, so the phone never holds the request open
                //     for the length of a transcription - a long recording can no
                //     longer be killed by a request/proxy timeout. The phone polls
                //     GET .../status to watch progress.
                //   - empty capture (zero segments): CompleteAsync throws; surfaced
                //     below as an explicit error, never a silent empty transcript.
                var status = await ServiceFor(t.Value).CompleteAsync(id, manifest, ctx.RequestAborted);
                if (status.State == "incomplete")
                    return Results.Json(status, statusCode: StatusCodes.Status409Conflict);
                return Results.Json(status, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] complete bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            catch (InvalidOperationException ex)
            {
                // An empty capture (zero segments) fails loud here rather than
                // ever producing an empty transcript (issue #586). Surface a clean,
                // named error to the phone.
                FileLog.Write($"[RecordingEndpoints] complete refused: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception ex)
            {
                // The recording's status.json is already marked "error" by the
                // service; surface a clean message to the phone for retry.
                FileLog.Write($"[RecordingEndpoints] complete failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/recording/{id}/status", (string id, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                return Results.Json(ServiceFor(t.Value).GetStatus(id));
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(new { error = "unknown recording" });
            }
        });

        // The Transcripts and Dictionary PAGES are served by the Cockpit now (one-URL plan);
        // /voice, /transcripts, /dictionary fall through the proxy to it. Only the data API
        // below stays here.

        // ===== Dictionary data API ==========================================
        // The glossary is a single shared YAML file used by both phone-recording
        // transcription and desktop dictation. The page sends the whole document
        // on save (no partial merge) so the file stays the single source of truth.

        app.MapGet("/dictionary", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            var dict = DictionaryLoader.LoadFromDisk(GlossaryPathFor(t.Value));
            return Results.Json(ToDto(dict));
        });

        app.MapPut("/dictionary", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var dto = await JsonSerializer.DeserializeAsync<DictionaryDto>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (dto is null)
                    return Results.BadRequest(new { error = "dictionary body required" });

                var path = GlossaryPathFor(t.Value);
                DictionaryLoader.WriteToDisk(path, FromDto(dto, DictionaryLoader.LoadFromDisk(path)));
                var reread = DictionaryLoader.LoadFromDisk(path);
                return Results.Json(ToDto(reread));
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] dictionary bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Additive convenience endpoint so an agent in a session can add a term
        // (and optional mistranscription spellings) without round-tripping the
        // whole document. Existing entries are preserved; duplicates are ignored.
        app.MapPost("/dictionary/terms", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var add = await JsonSerializer.DeserializeAsync<DictionaryAddRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                var hasTerms = add?.Terms is { Count: > 0 };
                var hasPatterns = add?.Mistranscriptions is { Count: > 0 };
                if (add is null || (!hasTerms && !hasPatterns))
                    return Results.BadRequest(new { error = "provide 'terms' and/or 'mistranscriptions'" });

                var path = GlossaryPathFor(t.Value);
                var onDisk = DictionaryLoader.LoadFromDisk(path);
                var current = ToDto(onDisk);

                foreach (var term in add.Terms ?? new())
                {
                    var trimmed = term?.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !current.Vocabulary.Contains(trimmed))
                        current.Vocabulary.Add(trimmed);
                }

                foreach (var kv in add.Mistranscriptions ?? new())
                {
                    var term = kv.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(term) || kv.Value is null)
                        continue;
                    if (!current.CommonMistranscriptions.TryGetValue(term, out var variants))
                    {
                        variants = new List<string>();
                        current.CommonMistranscriptions[term] = variants;
                    }
                    foreach (var v in kv.Value)
                    {
                        var vv = v?.Trim();
                        if (!string.IsNullOrWhiteSpace(vv) && !variants.Contains(vv))
                            variants.Add(vv);
                    }
                }

                DictionaryLoader.WriteToDisk(path, FromDto(current, onDisk));
                return Results.Json(ToDto(DictionaryLoader.LoadFromDisk(path)));
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] dictionary add bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // ===== Dictionary suggestions API (devthrottle #2075) ================
        // The server mines this tenant's stored transcripts for terms the model keeps getting wrong that are
        // not yet in the glossary, and offers them for one-press addition. Everything the Dictionary page does
        // is available here too, so a customer who scripts their setup gets the same flow. Mapped only when the
        // suggestion service and dismissal store were wired (production always wires them; the recording-only
        // test harnesses map neither and simply do not expose these routes). Same tenant idiom as every /ingest
        // route: resolve the caller's tenant, 403 when none resolves, never the Local partition on hosted.
        if (suggestions is not null && dismissals is not null)
            MapSuggestionRoutes(app, tenantBoundary, suggestions, dismissals);

        // The daily-email block, mapped on its own condition because it depends on the composer rather than on
        // the dismissal store - a harness that wires one need not wire the other.
        if (emailComposer is not null)
            MapSuggestionEmailRoute(app, tenantBoundary, emailComposer);

        app.MapGet("/recordings", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(ServiceFor(t.Value).ListAll());
        });

        app.MapGet("/recording/{id}/transcript", (string id, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            var text = ServiceFor(t.Value).GetTranscript(id);
            return text is null
                ? Results.NotFound(new { error = "no transcript" })
                : Results.Text(text, "text/plain; charset=utf-8");
        });

        app.MapGet("/recording/{id}/audio/{index:int}", (string id, int index, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            var audio = ServiceFor(t.Value).GetAudioFile(id, index);
            return audio is null
                ? Results.NotFound(new { error = "no such segment" })
                : Results.File(audio.Value.path, audio.Value.contentType, enableRangeProcessing: true);
        });

        app.MapDelete("/recording/{id}", (string id, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                ServiceFor(t.Value).DeleteRecording(id);
                return Results.Json(new { ok = true, id });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] delete not found: {ex.Message}");
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapPost("/recording/{id}/promote", async (string id, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var status = await ServiceFor(t.Value).PromoteToVaultAsync(id);
                return Results.Json(status);
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] promote rejected: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[RecordingEndpoints] promote failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Update human-readable metadata (title, subtitle, summary). Accepts both
        // PATCH (partial update) and POST so simple clients can use either.
        app.MapMethods("/recording/{id}/meta", new[] { "PATCH", "POST" }, async (string id, HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var update = await JsonSerializer.DeserializeAsync<RecordingMetaUpdate>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (update is null)
                    return Results.BadRequest(new { error = "meta body required" });
                var item = ServiceFor(t.Value).UpdateMeta(id, update);
                return Results.Json(item);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] meta bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] meta not found: {ex.Message}");
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // A copy-paste guide for an external agent/LLM. The base URL is the
        // tailnet front door (resolved via Tailscale, independent of how this
        // page was reached) so an agent on any tailnet machine gets a URL that
        // actually works. Access is API-only; the guide does not expose the disk.
        app.MapGet("/agent-info", () =>
        {
            var baseUrl = TailscaleIdentity.TryGetFrontDoorBaseUrl();
            var guide = BuildAgentInfo(baseUrl);
            return Results.Text(guide, "text/plain; charset=utf-8");
        });
    }

    /// <summary>
    /// The dictionary-suggestions routes (devthrottle #2075), all under <c>/ingest/dictionary</c>, all sharing
    /// the same tenant idiom as the glossary routes above:
    ///   GET  /dictionary/suggestions          the ranked pending suggestions with evidence, plus the count
    ///   GET  /dictionary/suggestions/count     just the count (the nav-badge poll)
    ///   POST /dictionary/suggestions/scan      run a scan NOW (mine + model-screen; the page's button)
    ///   POST /dictionary/suggestions/apply     add the chosen terms to the glossary (term + wrong spellings)
    ///   POST /dictionary/suggestions/dismiss   stop suggesting a term (remembered, evidence snapshotted)
    ///   GET  /dictionary/dismissed             the dismissed terms with their evidence, for the Restore screen
    ///   POST /dictionary/dismissed/restore     make a dismissed term eligible again
    /// Reads serve the STORED result of the latest scan (daily per tenant, or scan-now) - they never mine and
    /// never call the screening model (devthrottle #2115). Apply and dismiss edit the stored result in place
    /// so the page and the badge reflect the action at once; a restored term reappears on the next scan.
    /// </summary>
    private static void MapSuggestionRoutes(
        IEndpointRouteBuilder app,
        Tenancy.HostedTenantBoundary? tenantBoundary,
        DictionarySuggestionService suggestions,
        DictionarySuggestionDismissalStore dismissals)
    {
        app.MapGet("/dictionary/suggestions", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(ToSuggestionsResponse(suggestions.GetStored(t.Value)));
        });

        app.MapGet("/dictionary/suggestions/count", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(new { count = suggestions.GetSuggestionCount(t.Value) });
        });

        // The Dictionary page's "Scan now" button: run the full scan (mine + screen the never-judged
        // candidates) for this tenant and return the fresh stored result. May take seconds when there are
        // new candidates (one model call); instant when there is nothing new to judge.
        app.MapPost("/dictionary/suggestions/scan", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            var result = await suggestions.RunScanAsync(t.Value, ctx.RequestAborted);
            return Results.Json(ToSuggestionsResponse(result));
        });

        app.MapPost("/dictionary/suggestions/apply", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            SuggestionApplyRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<SuggestionApplyRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] apply bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req?.Terms is not { Count: > 0 })
                return Results.BadRequest(new { error = "provide 'terms' (the canonical terms to add)" });

            // Resolve each requested term against the CURRENT pending suggestions, so a caller can only apply a
            // term the server actually offered - and so the term's wrong spellings (its evidence) are written
            // as Common mistranscriptions in the same press. A term that is not currently suggested is ignored.
            var vocab = new List<string>();
            var mistranscriptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var applied = new List<string>();
            foreach (var term in req.Terms)
            {
                var match = suggestions.FindSuggestion(t.Value, term ?? "");
                if (match is null) continue;
                vocab.Add(match.Term);
                mistranscriptions[match.Term] = match.Variants.Select(v => v.Heard).ToList();
                applied.Add(match.Term);
            }

            var updated = TenantGlossary.AddTerms(t.Value, vocab, mistranscriptions);
            foreach (var term in applied)
                suggestions.RemoveFromStored(t.Value, term); // reflect the apply at once, no rescan
            var remaining = suggestions.GetSuggestions(t.Value);
            return Results.Json(new SuggestionApplyResponse(
                ToDto(updated), applied, remaining.Select(ToSuggestionDto).ToList(), remaining.Count));
        });

        app.MapPost("/dictionary/suggestions/dismiss", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            SuggestionTermRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<SuggestionTermRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] dismiss bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (string.IsNullOrWhiteSpace(req?.Term))
                return Results.BadRequest(new { error = "provide 'term' (the term to dismiss)" });

            // Snapshot the evidence from the current suggestion so the Dismissed-terms screen can still explain
            // it after its transcripts age out. If the term is no longer suggested, dismiss it with empty
            // evidence so the exclusion still holds (idempotent, honest).
            var match = suggestions.FindSuggestion(t.Value, req.Term)
                ?? new MistranscriptionSuggestion(req.Term.Trim(), Array.Empty<MistranscriptionVariant>(), 0, 0);
            dismissals.Dismiss(t.Value, match, DateTime.UtcNow);
            suggestions.RemoveFromStored(t.Value, match.Term); // reflect the dismissal at once, no rescan
            return Results.Json(new { ok = true, dismissed = match.Term, count = suggestions.GetSuggestionCount(t.Value) });
        });

        app.MapGet("/dictionary/dismissed", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            var list = dismissals.List(t.Value).Select(ToDismissedDto).ToList();
            return Results.Json(new DismissedResponse(list));
        });

        app.MapPost("/dictionary/dismissed/restore", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            SuggestionTermRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<SuggestionTermRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] restore bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (string.IsNullOrWhiteSpace(req?.Term))
                return Results.BadRequest(new { error = "provide 'term' (the term to restore)" });

            var restored = dismissals.Restore(t.Value, req.Term);
            // The term is eligible again on the NEXT scan (daily, or the page's "Scan now"); the stored
            // result is not edited here because a restored term has no fresh mining evidence to show yet.
            return Results.Json(new { ok = true, restored });
        });
    }

    /// <summary>
    /// The daily-email block route (issue #2074, mockup screen 5): <c>POST /ingest/dictionary/suggestions/
    /// email-block</c>. Whatever composes this tenant's daily report asks here whether the report should carry
    /// a dictionary-suggestions block, and gets the finished block back - it never decides for itself
    /// (critical rule 7). Same tenant idiom as every other route in this group.
    ///
    /// A POST rather than a GET because it can COMMIT: <c>markMentioned</c> spends one of the batch's two
    /// mentions, and a GET that changes state would be spent by a link preview or a retry. Omit it, or send
    /// false, to preview the block without spending anything.
    /// </summary>
    private static void MapSuggestionEmailRoute(
        IEndpointRouteBuilder app,
        Tenancy.HostedTenantBoundary? tenantBoundary,
        SuggestionEmailComposer composer)
    {
        app.MapPost("/dictionary/suggestions/email-block", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();

            // An absent or empty body is the common case (preview), so it is valid and means markMentioned
            // false - the caller that commits says so explicitly.
            SuggestionEmailBlockRequest? req = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    req = await JsonSerializer.DeserializeAsync<SuggestionEmailBlockRequest>(
                        ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                }
                catch (JsonException ex)
                {
                    FileLog.Write($"[RecordingEndpoints] email-block bad JSON: {ex.Message}");
                    return Results.BadRequest(new { error = "invalid JSON" });
                }
            }

            var decision = composer.Compose(t.Value, req?.MarkMentioned ?? false);
            return Results.Json(new SuggestionEmailBlockResponse(
                decision.Include,
                // The reason travels as a lower-case word rather than the enum's name, so it reads the same
                // in a log line, a test assertion, and a report the owner sees.
                decision.Reason.ToString().ToLowerInvariant(),
                decision.Block?.Heading,
                decision.Block?.Html,
                decision.Block?.Text,
                SuggestionEmailBlock.Footer,
                decision.TermCount,
                decision.Batch,
                decision.Mentions,
                Settings.DictationEmailCadenceState.MaxMentionsPerBatch));
        });
    }

    private static SuggestionDto ToSuggestionDto(MistranscriptionSuggestion s) => new(
        s.Term,
        s.Variants.Select(v => new VariantDto(v.Heard, v.Count)).ToList(),
        s.WrongCount,
        s.TotalCount);

    /// <summary>Fold a stored scan (or null = never scanned) into the wire response. Null folds to an empty
    /// list with ScreeningOk true and no timestamp - the page's "no scan yet" state, not an error.</summary>
    private static SuggestionsResponse ToSuggestionsResponse(DictionarySuggestionScanStore.ScanResult? scan)
        => scan is null
            ? new SuggestionsResponse(new List<SuggestionDto>(), 0, null, true, "")
            : new SuggestionsResponse(
                scan.Suggestions.Select(ToSuggestionDto).ToList(), scan.Suggestions.Count,
                scan.ScannedAtUtc, scan.ScreeningOk, scan.ScreeningError);

    private static DismissedTermDto ToDismissedDto(DismissedTerm d) => new(
        d.Term,
        d.Variants.Select(v => new VariantDto(v.Heard, v.Count)).ToList(),
        d.WrongCount,
        d.TotalCount,
        d.DismissedAtUtc);

    private static string BuildAgentInfo(string? baseUrl)
    {
        // When Tailscale is not available there is no remotely reachable URL.
        // Say so truthfully rather than emitting a localhost URL that only works
        // on this one machine.
        var url = baseUrl ?? "(unavailable - Tailscale was not detected on this machine, so the API has no remote URL)";
        return $$"""
        # DevThrottle Transcripts - Agent API

        Base URL: {{url}}

        This API is reachable from any machine on the tailnet over HTTPS. Do
        everything through the REST API below. Do NOT read or write transcript
        files on disk - the files live on one machine and are not portable; the
        API is the only supported access path.

        ## REST API

        GET    {base}/ingest/recordings
               List all transcripts (JSON). Fields: recordingId, title, subtitle,
               summary, startedAt, state, segments, durationMs, hasTranscript,
               inVault.

        GET    {base}/ingest/recording/{id}/transcript
               The cleaned transcript as plain text.

        GET    {base}/ingest/recording/{id}/audio/{index}
               One audio segment (index starts at 0).

        PATCH  {base}/ingest/recording/{id}/meta
               Set human-readable metadata. JSON body, any subset of:
                 { "title": "...", "subtitle": "...", "summary": "..." }
               A null/omitted field is left unchanged. Returns the updated record.

        POST   {base}/ingest/recording/{id}/promote
               Copy this transcript + audio into the vault (permanent).

        DELETE {base}/ingest/recording/{id}
               Delete the transient local transcript. A promoted vault copy is kept.

        ## Dictionary (transcript-correction glossary)

        The shared glossary used to correct FINISHED transcripts toward the
        user's terms. It is never sent to the speech-to-text provider: the
        audio is transcribed as spoken, with no vocabulary and no steering
        hint, and only then are the listed words substituted. Editing it
        affects both phone-recording transcription and desktop dictation.
        Changes apply on the next recording (no restart).

        GET    {base}/ingest/dictionary
               The glossary as JSON:
                 { "vocabulary": ["acmeflow", ...],
                   "commonMistranscriptions": { "ConPTY": ["Conty", ...] },
                   "profiles": { "default": { "cleanupEnabled": true, "stylePrompt": null } } }

        POST   {base}/ingest/dictionary/terms
               Add term(s) and/or mistranscription spellings. Additive: existing
               entries are kept and duplicates ignored. JSON body, either field
               optional:
                 { "terms": ["NewTerm"],
                   "mistranscriptions": { "ConPTY": ["Conty"] } }
               Returns the updated dictionary. Use this for "add this word".

        PUT    {base}/ingest/dictionary
               Replace the ENTIRE glossary. Body is the shape GET returns. Use
               only when rewriting the whole thing; prefer POST .../terms to add.

        (Replace {base} with the Base URL above.)

        ## Typical workflow for an agent

        1. GET /ingest/recordings and find transcripts with an empty or auto
           generated title and no summary.
        2. GET .../transcript to read the text.
        3. PATCH .../meta with a clear title, a short subtitle, and a summary.
        """;
    }

    /// <summary>The 403 every /ingest route answers when the caller's tenant cannot be resolved (issues
    /// #2058/#2060). On the hosted Gateway an authenticated request whose device key has no bound tenant is
    /// refused, NEVER served the Local partition (that would be a wrong-tenant read of another account's
    /// recordings or glossary). Self-host always resolves to Local, so this never fires there.</summary>
    private static IResult TenantRequired()
        => Results.Json(new { error = "a tenant could not be resolved for this request" },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>A filesystem-safe folder name for a tenant partition.</summary>
    private static string TenantFolder(TenantId tenant)
    {
        var chars = tenant.Value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>This tenant's transcripts root. The single Local tenant keeps the existing flat root (so a
    /// self-host install's recordings do not move); every other tenant gets its own subdirectory.</summary>
    private static string RootForTenant(TenantId tenant)
        => tenant == TenantId.Local ? CcStorage.Transcripts()
            : Path.Combine(CcStorage.Transcripts(), TenantFolder(tenant));

    /// <summary>This tenant's vault-transcripts promotion target (per tenant on hosted, flat on self-host).</summary>
    private static string CollectionForTenant(TenantId tenant)
        => tenant == TenantId.Local ? CcStorage.VaultTranscripts()
            : Path.Combine(CcStorage.VaultTranscripts(), TenantFolder(tenant));

    /// <summary>This tenant's dictation glossary file. Local keeps the existing shared file; every other
    /// tenant gets its own glossary. Read by BOTH the dictionary editor routes and this tenant's recording
    /// cleanup pass, so a per-tenant edit changes only that tenant's transcripts (issue #2060). It is never
    /// sent to the speech-to-text provider - it is applied to the finished transcript (issue 2481).
    /// Delegates to <see cref="TenantGlossary.PathFor"/> so the editor routes, the cleanup pass, and the
    /// suggestion "apply" path can never disagree on the glossary location (devthrottle #2075).</summary>
    private static string GlossaryPathFor(TenantId tenant) => TenantGlossary.PathFor(tenant);

    private static RecordingIngestService BuildService(
        TenantId tenant,
        KeyVault? keyVault,
        TranscriptionHistoryLog? history,
        TranscriptionAudioArchive audioArchive)
    {
        // Local transient store for transcripts (audio + markdown), per tenant. Transcripts
        // are NOT auto-filed into the vault; the user promotes the keepers.
        var root = RootForTenant(tenant);
        // Promotion target: the vault transcripts collection (permanent copy), per tenant.
        var collectionDir = CollectionForTenant(tenant);
        // This tenant's transcription-health history. The single Local tenant uses the host's shared log
        // (self-host, unchanged); every other tenant writes to its own partition so a recording's history
        // contribution is never fleet-global. (The Transcription Health READ surface is issue #2059.)
        var tenantHistory = tenant == TenantId.Local
            ? history
            : TranscriptionHistoryLog.ForTenant(tenant);

        FileLog.Write($"[RecordingEndpoints] BuildService tenant={tenant.ToLogString()}: root={root}, collection={collectionDir}");

        var filer = new CcVaultFiler(collectionDir);

        // The transcriber is built LAZILY, only when the background worker actually transcribes -
        // never during register/chunk/complete. This is what guarantees audio + notes always
        // land on the server regardless of transcription: a missing key no longer fails ingest, it
        // just fails (and reschedules) the downstream transcription job.
        //
        // It routes through the ONE Gateway transcription owner (issue #839): the single
        // GatewayTranscriptionService resolves the configured mode and the key (from the Gateway
        // vault) and picks the provider - in-process Whisper for on-device mode, or the resolved
        // provider-compatible batch endpoint for hosted mode. So switching the mode in the Cockpit
        // changes how the recording is transcribed with no Gateway restart, and on-device mode now
        // works for recordings too - the same single audio-to-text path every other batch caller uses.
        //
        // The transcriber carries THIS tenant into the cleanup call, so the corrector reads this
        // tenant's own glossary (issue #2060) through the one TenantGlossary owner - the same
        // mechanism every live-dictation caller uses (issue #2482), no hand-injected path here.
        //
        // The glossary is still only ever applied to the FINISHED transcript and is never sent to the
        // speech-to-text provider (issue 2481). That sentence used to sit on the hand-injected
        // glossaryPath this site no longer has; it is kept here, at the construction site, because
        // deleting the injection must not delete the ruling that came with it. The same claim is on
        // GlossaryPathFor, which is now the only place a glossary path is composed.
        return new RecordingIngestService(
            root,
            transcriberFactory: () => new GatewayServiceRecordingTranscriber(
                new GatewayTranscriptionService(
                    keyVault ?? new KeyVault(),
                    audioArchive,
                    history: tenantHistory),
                tenant),
            filer,
            collectionDir);
    }

    private static DictionaryDto ToDto(DictationDictionary dict) => new(
        Vocabulary: dict.Vocabulary.ToList(),
        CommonMistranscriptions: dict.CommonMistranscriptions
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        Profiles: dict.Profiles.ToDictionary(
            kv => kv.Key,
            kv => new DictionaryProfileDto(kv.Value.CleanupEnabled, kv.Value.FuzzyCorrectionEnabled)));

    /// <param name="onDisk">The glossary as it currently stands, so a save cannot DESTROY a setting the
    /// request does not mention. <see cref="DictionaryProfileDto.FuzzyCorrectionEnabled"/> is nullable
    /// for exactly this reason: the phone editor and any older client send a profile as
    /// <c>{ cleanupEnabled }</c> only, and without this a routine save of the word list would silently
    /// switch a deliberate opt-in back off. Absent means "leave it as it was", not "false".</param>
    private static DictationDictionary FromDto(DictionaryDto dto, DictationDictionary onDisk)
    {
        var vocab = (dto.Vocabulary ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        var patterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var kv in dto.CommonMistranscriptions ?? new())
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                continue;
            var variants = kv.Value
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();
            if (variants.Count > 0)
                patterns[kv.Key.Trim()] = variants;
        }

        var profiles = new Dictionary<string, DictationProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in dto.Profiles ?? new())
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                continue;
            var name = kv.Key.Trim();
            profiles[name] = new DictationProfile(
                Name: name,
                CleanupEnabled: kv.Value.CleanupEnabled,
                FuzzyCorrectionEnabled: kv.Value.FuzzyCorrectionEnabled
                    ?? (onDisk.Profiles.TryGetValue(name, out var existing) && existing.FuzzyCorrectionEnabled));
        }

        return new DictationDictionary(vocab, patterns, profiles);
    }
}

/// <summary>JSON shape for the dictionary editor (GET/PUT /ingest/dictionary).</summary>
internal sealed record DictionaryDto(
    List<string> Vocabulary,
    Dictionary<string, List<string>> CommonMistranscriptions,
    Dictionary<string, DictionaryProfileDto> Profiles);

/// <param name="FuzzyCorrectionEnabled">Nullable on purpose: absent means "leave whatever is on disk
/// alone". Clients that predate the field send a profile as <c>{ cleanupEnabled }</c>, and a routine
/// word-list save from one of those must not switch a deliberate opt-in back off.</param>
internal sealed record DictionaryProfileDto(bool CleanupEnabled, bool? FuzzyCorrectionEnabled = null);

/// <summary>Additive request for POST /ingest/dictionary/terms.</summary>
internal sealed record DictionaryAddRequest(
    List<string>? Terms,
    Dictionary<string, List<string>>? Mistranscriptions);

// ===== Dictionary suggestions DTOs (devthrottle #2075) ==================================================

/// <summary>One wrong spelling and how often it was seen - the "heard as X (n)" evidence.</summary>
internal sealed record VariantDto(string Heard, int Count);

/// <summary>A pending suggestion: the canonical term to add, its wrong spellings, and the counts behind
/// "wrong 53 of 97 times".</summary>
internal sealed record SuggestionDto(string Term, List<VariantDto> Variants, int WrongCount, int TotalCount);

/// <summary>GET /ingest/dictionary/suggestions and POST .../scan - the stored scan's approved suggestions
/// plus their count (the count the nav badge renders; the client never re-derives it), when the scan ran
/// (null = never scanned), and the Gateway-ruled screening state the page renders VERBATIM (rule 7 - the
/// client never derives a verdict): ScreeningOk false means the screening model was unreachable, the shown
/// list is previously-screened only, and ScreeningError says why.</summary>
internal sealed record SuggestionsResponse(
    List<SuggestionDto> Suggestions, int Count, DateTime? ScannedAtUtc, bool ScreeningOk, string ScreeningError);

/// <summary>POST /ingest/dictionary/suggestions/apply request: the canonical terms the customer chose to add.</summary>
internal sealed record SuggestionApplyRequest(List<string>? Terms);

/// <summary>POST /ingest/dictionary/suggestions/apply response: the updated glossary, which terms were
/// actually applied, and the suggestions that remain (with their new count).</summary>
internal sealed record SuggestionApplyResponse(
    DictionaryDto Dictionary, List<string> Applied, List<SuggestionDto> Suggestions, int Count);

/// <summary>POST /ingest/dictionary/suggestions/dismiss and /ingest/dictionary/dismissed/restore request.</summary>
internal sealed record SuggestionTermRequest(string? Term);

/// <summary>A dismissed term with its snapshotted evidence and when it was dismissed.</summary>
internal sealed record DismissedTermDto(
    string Term, List<VariantDto> Variants, int WrongCount, int TotalCount, DateTime DismissedAtUtc);

/// <summary>GET /ingest/dictionary/dismissed - the dismissed terms, newest first.</summary>
internal sealed record DismissedResponse(List<DismissedTermDto> Dismissed);

/// <summary>POST /ingest/dictionary/suggestions/email-block request (issue #2074). The body is optional;
/// omitting it previews the block without spending one of the batch's mentions.</summary>
/// <param name="MarkMentioned">True only from the caller that is actually sending the report.</param>
internal sealed record SuggestionEmailBlockRequest(bool? MarkMentioned);

/// <summary>
/// POST /ingest/dictionary/suggestions/email-block response (issue #2074) - the Gateway's finished verdict on
/// whether this tenant's daily report carries a suggestions block, and the block itself when it does.
/// </summary>
/// <param name="Include">Whether the report should carry the block. When false, every block field is null.</param>
/// <param name="Reason">Why: "included", "settingoff", "nosuggestions", or "alreadymentioned".</param>
/// <param name="Heading">The block's heading line; null when not included.</param>
/// <param name="Html">The block as HTML; null when not included.</param>
/// <param name="Text">The block as plain text; null when not included.</param>
/// <param name="Footer">The sentence naming the setting that controls the block, for the report's foot.</param>
/// <param name="TermCount">How many terms are pending, whether or not the block is included.</param>
/// <param name="Batch">The batch fingerprint the cadence is keyed on; empty when there are no suggestions.</param>
/// <param name="Mentions">How many times this batch has been mentioned, after any commit this call made.</param>
/// <param name="MaxMentions">The cap a batch may be mentioned, so the caller can render "1 of 2".</param>
internal sealed record SuggestionEmailBlockResponse(
    bool Include, string Reason, string? Heading, string? Html, string? Text, string Footer,
    int TermCount, string Batch, int Mentions, int MaxMentions);
