using System.Text.Json;
using CcDirector.Core.Recording;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tailscale;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Maps the <c>/ingest/recording</c> REST surface the phone recorder uploads
/// to. The phone records offline; when it has connectivity it registers a
/// recording, PUTs each finalized audio segment (idempotent by index + hash),
/// then POSTs <c>complete</c>, which transcribes every segment through the
/// existing dictation pipeline and assembles + cleans the transcript into the
/// local transcripts area. Transcripts stay local (transient) until the user
/// promotes one into the vault.
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
        var service = BuildService();

        app.MapPost("/ingest/recording", async (HttpContext ctx) =>
        {
            try
            {
                var req = await JsonSerializer.DeserializeAsync<RecordingRegisterRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (req is null || string.IsNullOrWhiteSpace(req.RecordingId))
                    return Results.BadRequest(new { error = "RecordingId is required" });
                var status = service.Register(req);
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

                await service.StoreChunkAsync(id, index, bytes, sha, ctx.RequestAborted);
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
                var status = await service.CompleteAsync(id, manifest, ctx.RequestAborted);
                return Results.Json(status);
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
                return Results.Json(service.GetStatus(id));
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(new { error = "unknown recording" });
            }
        });

        // ===== Transcripts browser (dashboard page + read APIs) =============

        app.MapGet("/transcripts", () =>
        {
            var html = EmbeddedResources.Load("transcripts.html");
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/ingest/recordings", () => Results.Json(service.ListAll()));

        app.MapGet("/ingest/recording/{id}/transcript", (string id) =>
        {
            var text = service.GetTranscript(id);
            return text is null
                ? Results.NotFound(new { error = "no transcript" })
                : Results.Text(text, "text/plain; charset=utf-8");
        });

        app.MapGet("/ingest/recording/{id}/audio/{index:int}", (string id, int index) =>
        {
            var audio = service.GetAudioFile(id, index);
            return audio is null
                ? Results.NotFound(new { error = "no such segment" })
                : Results.File(audio.Value.path, audio.Value.contentType, enableRangeProcessing: true);
        });

        app.MapDelete("/ingest/recording/{id}", (string id) =>
        {
            try
            {
                service.DeleteRecording(id);
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
                var status = await service.PromoteToVaultAsync(id);
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
                var item = service.UpdateMeta(id, update);
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
        var dictionaryPath = Path.Combine(localAppData, "cc-director", "dictation", "dictionary.yaml");

        FileLog.Write($"[RecordingEndpoints] BuildService: root={root}, collection={collectionDir}");

        var transcriber = new OpenAiRecordingTranscriber(dictionaryPath: dictionaryPath);
        var filer = new CcVaultFiler(collectionDir);
        return new RecordingIngestService(root, transcriber, filer, collectionDir);
    }
}
