using System.Text.Json;
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
/// then POSTs <c>complete</c>, which transcribes every segment through the
/// existing dictation pipeline, assembles + cleans the transcript, and files
/// it into the vault transcripts collection.
///
/// Routes (all JSON except the raw-bytes chunk PUT):
///   POST /ingest/recording                      register, body RecordingRegisterRequest
///   PUT  /ingest/recording/{id}/chunk/{index}   raw audio bytes, header X-Chunk-Sha256
///   POST /ingest/recording/{id}/complete        body RecordingManifest
///   GET  /ingest/recording/{id}/status          RecordingStatusDto
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
    }

    private static RecordingIngestService BuildService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, "cc-director", "recordings");
        var collectionDir = Path.Combine(localAppData, "cc-director", "vault", "transcripts");
        var dictionaryPath = Path.Combine(localAppData, "cc-director", "dictation", "dictionary.yaml");

        FileLog.Write($"[RecordingEndpoints] BuildService: root={root}, collection={collectionDir}");

        var transcriber = new OpenAiRecordingTranscriber(dictionaryPath: dictionaryPath);
        var filer = new CcVaultFiler(collectionDir);
        return new RecordingIngestService(root, transcriber, filer, collectionDir);
    }
}
