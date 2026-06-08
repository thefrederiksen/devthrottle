using System.Text.Json;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Network;
using CcDirector.Core.Recording;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Maps the <c>/ingest/recording</c> REST surface the phone recorder uploads
/// to. The phone records offline; when it has connectivity it registers a
/// recording, PUTs each finalized audio segment (idempotent by index + hash),
/// then POSTs <c>complete</c>, which queues the recording and returns 202
/// immediately. A background worker transcribes every segment through the
/// existing dictation pipeline and assembles + cleans the transcript into the
/// local transcripts area, retrying flaky segments and re-queueing a failed
/// job for a later attempt. The phone polls <c>status</c> to watch progress.
/// Transcripts stay local (transient) until the user promotes one into the vault.
///
/// Routes (all JSON except the raw-bytes chunk PUT):
///   POST /ingest/recording                      register, body RecordingRegisterRequest
///   PUT  /ingest/recording/{id}/chunk/{index}   raw audio bytes, header X-Chunk-Sha256
///   POST /ingest/recording/{id}/complete        body RecordingManifest
///   GET  /ingest/recording/{id}/status          RecordingStatusDto
///   POST /ingest/recording/{id}/promote         copy transcript + audio into the vault
///   PATCH /ingest/recording/{id}/meta           set human-readable title/subtitle/summary
///   DELETE /ingest/recording/{id}               delete the transient local transcript
///   GET  /ingest/agent-info                     copy-paste API guide for an external agent
///
/// Auth is the Gateway's existing token middleware (applied host-wide when
/// enabled), so these routes inherit it without extra checks here.
/// </summary>
internal static class RecordingEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        // Lazily built on FIRST USE, not at host startup: constructing the service resolves
        // the OpenAI API key (the transcriber needs it), and the Gateway must boot on machines
        // without that key. A missing key then fails the individual recording request loudly
        // (500 with the explicit "OPENAI_API_KEY ... not set" message) instead of preventing
        // the entire Gateway host from starting.
        var lazyService = new Lazy<RecordingIngestService>(BuildService);

        app.MapPost("/ingest/recording", async (HttpContext ctx) =>
        {
            try
            {
                var req = await JsonSerializer.DeserializeAsync<RecordingRegisterRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (req is null || string.IsNullOrWhiteSpace(req.RecordingId))
                    return Results.BadRequest(new { error = "RecordingId is required" });
                var status = lazyService.Value.Register(req);
                return Results.Json(status);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] register bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapPut("/ingest/recording/{id}/chunk/{index:int}", async (string id, int index, HttpContext ctx) =>
        {
            try
            {
                var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                    return Results.BadRequest(new { error = "empty chunk body" });

                await lazyService.Value.StoreChunkAsync(id, index, bytes, sha, ctx.RequestAborted);
                return Results.Json(new { ok = true, index, bytes = bytes.Length });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] chunk store failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/ingest/recording/{id}/complete", async (string id, HttpContext ctx) =>
        {
            try
            {
                var manifest = await JsonSerializer.DeserializeAsync<RecordingManifest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (manifest is null)
                    return Results.BadRequest(new { error = "manifest body required" });
                // Enqueue and return immediately (202). Transcription runs in the
                // background worker, so the phone never holds the request open for
                // the length of a transcription - a long recording can no longer
                // be killed by a request/proxy timeout. The phone polls
                // GET .../status to watch progress.
                var status = await lazyService.Value.CompleteAsync(id, manifest, ctx.RequestAborted);
                return Results.Json(status, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] complete bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            catch (Exception ex)
            {
                // The recording's status.json is already marked "error" by the
                // service; surface a clean message to the phone for retry.
                FileLog.Write($"[RecordingEndpoints] complete failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/ingest/recording/{id}/status", (string id) =>
        {
            try
            {
                return Results.Json(lazyService.Value.GetStatus(id));
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

        app.MapGet("/ingest/dictionary", () =>
        {
            var dict = DictionaryLoader.LoadFromDisk(DictionaryFilePath());
            return Results.Json(ToDto(dict));
        });

        app.MapPut("/ingest/dictionary", async (HttpContext ctx) =>
        {
            try
            {
                var dto = await JsonSerializer.DeserializeAsync<DictionaryDto>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (dto is null)
                    return Results.BadRequest(new { error = "dictionary body required" });

                DictionaryLoader.WriteToDisk(DictionaryFilePath(), FromDto(dto));
                var reread = DictionaryLoader.LoadFromDisk(DictionaryFilePath());
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
        app.MapPost("/ingest/dictionary/terms", async (HttpContext ctx) =>
        {
            try
            {
                var add = await JsonSerializer.DeserializeAsync<DictionaryAddRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                var hasTerms = add?.Terms is { Count: > 0 };
                var hasPatterns = add?.Mistranscriptions is { Count: > 0 };
                if (add is null || (!hasTerms && !hasPatterns))
                    return Results.BadRequest(new { error = "provide 'terms' and/or 'mistranscriptions'" });

                var path = DictionaryFilePath();
                var current = ToDto(DictionaryLoader.LoadFromDisk(path));

                foreach (var term in add.Terms ?? new())
                {
                    var t = term?.Trim();
                    if (!string.IsNullOrWhiteSpace(t) && !current.Vocabulary.Contains(t))
                        current.Vocabulary.Add(t);
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

                DictionaryLoader.WriteToDisk(path, FromDto(current));
                return Results.Json(ToDto(DictionaryLoader.LoadFromDisk(path)));
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] dictionary add bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapGet("/ingest/recordings", () => Results.Json(lazyService.Value.ListAll()));

        app.MapGet("/ingest/recording/{id}/transcript", (string id) =>
        {
            var text = lazyService.Value.GetTranscript(id);
            return text is null
                ? Results.NotFound(new { error = "no transcript" })
                : Results.Text(text, "text/plain; charset=utf-8");
        });

        app.MapGet("/ingest/recording/{id}/audio/{index:int}", (string id, int index) =>
        {
            var audio = lazyService.Value.GetAudioFile(id, index);
            return audio is null
                ? Results.NotFound(new { error = "no such segment" })
                : Results.File(audio.Value.path, audio.Value.contentType, enableRangeProcessing: true);
        });

        app.MapDelete("/ingest/recording/{id}", (string id) =>
        {
            try
            {
                lazyService.Value.DeleteRecording(id);
                return Results.Json(new { ok = true, id });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] delete not found: {ex.Message}");
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapPost("/ingest/recording/{id}/promote", async (string id) =>
        {
            try
            {
                var status = await lazyService.Value.PromoteToVaultAsync(id);
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
        app.MapMethods("/ingest/recording/{id}/meta", new[] { "PATCH", "POST" }, async (string id, HttpContext ctx) =>
        {
            try
            {
                var update = await JsonSerializer.DeserializeAsync<RecordingMetaUpdate>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (update is null)
                    return Results.BadRequest(new { error = "meta body required" });
                var item = lazyService.Value.UpdateMeta(id, update);
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
        app.MapGet("/ingest/agent-info", () =>
        {
            var baseUrl = TailscaleIdentity.TryGetFrontDoorBaseUrl();
            var guide = BuildAgentInfo(baseUrl);
            return Results.Text(guide, "text/plain; charset=utf-8");
        });
    }

    private static string BuildAgentInfo(string? baseUrl)
    {
        // When Tailscale is not available there is no remotely reachable URL.
        // Say so truthfully rather than emitting a localhost URL that only works
        // on this one machine.
        var url = baseUrl ?? "(unavailable - Tailscale was not detected on this machine, so the API has no remote URL)";
        return $$"""
        # CC Director Transcripts - Agent API

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

        ## Dictionary (speech-to-text glossary)

        The shared glossary that biases transcription toward the user's terms.
        Editing it affects both phone-recording transcription and desktop
        dictation. Changes apply on the next recording (no restart).

        GET    {base}/ingest/dictionary
               The glossary as JSON:
                 { "vocabulary": ["mindzie", ...],
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

    private static RecordingIngestService BuildService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Local transient store for transcripts (audio + markdown). Transcripts
        // are NOT auto-filed into the vault; the user promotes the keepers.
        var root = Path.Combine(localAppData, "cc-director", "transcripts");
        // Promotion target: the vault transcripts collection (permanent copy).
        var collectionDir = Path.Combine(localAppData, "cc-director", "vault", "transcripts");

        FileLog.Write($"[RecordingEndpoints] BuildService: root={root}, collection={collectionDir}");

        // The OpenAI key comes from the Gateway's own key vault (read in-process). A missing
        // key leaves apiKey null and the transcriber fails the request loudly when used.
        var openAiKey = new KeyVault().Get("OPENAI_API_KEY");
        var transcriber = new OpenAiRecordingTranscriber(apiKey: openAiKey, dictionaryPath: DictionaryFilePath());
        var filer = new CcVaultFiler(collectionDir);
        return new RecordingIngestService(root, transcriber, filer, collectionDir);
    }

    /// <summary>
    /// The single shared dictation glossary file. Used by both the recording
    /// transcriber and the dictionary editor endpoints so the path is defined
    /// in exactly one place.
    /// </summary>
    private static string DictionaryFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "dictation", "dictionary.yaml");
    }

    private static DictionaryDto ToDto(DictationDictionary dict) => new(
        Vocabulary: dict.Vocabulary.ToList(),
        CommonMistranscriptions: dict.CommonMistranscriptions
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        Profiles: dict.Profiles.ToDictionary(
            kv => kv.Key,
            kv => new DictionaryProfileDto(kv.Value.CleanupEnabled)));

    private static DictationDictionary FromDto(DictionaryDto dto)
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
                CleanupEnabled: kv.Value.CleanupEnabled);
        }

        return new DictationDictionary(vocab, patterns, profiles);
    }
}

/// <summary>JSON shape for the dictionary editor (GET/PUT /ingest/dictionary).</summary>
internal sealed record DictionaryDto(
    List<string> Vocabulary,
    Dictionary<string, List<string>> CommonMistranscriptions,
    Dictionary<string, DictionaryProfileDto> Profiles);

internal sealed record DictionaryProfileDto(bool CleanupEnabled);

/// <summary>Additive request for POST /ingest/dictionary/terms.</summary>
internal sealed record DictionaryAddRequest(
    List<string>? Terms,
    Dictionary<string, List<string>>? Mistranscriptions);
