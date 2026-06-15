using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Async voice-turn submit/poll surface (issue #376; docs/architecture/gateway/
/// VOICE_TURN_ARCHITECTURE.md). The phone submits ONE request and is then free to drop signal:
///
///   POST /sessions/{sid}/voice-turn/submit  -> 202 { turn_id, expires_at }
///        creates a <see cref="TurnJob"/>, resolves the owning Director, and fires a
///        background task that drives the Director's existing SSE endpoint
///        (POST /sessions/{sid}/voice-turn), mirroring each stage event into the job.
///
///   GET  /sessions/{sid}/voice-turn/{turnId} -> 200 { stage, transcript, summary,
///        audioBase64, message } from the job cache (no Director call); 404 when the
///        turn id is unknown or past its 10-minute TTL.
///
/// The Director's SSE endpoint stays the worker (transcription, send-to-session, summarize,
/// TTS); this layer only owns the async interface and the result cache. Owner resolution
/// mirrors <see cref="SessionWsProxyEndpoints"/>: the <see cref="SessionOwnerCache"/> fast
/// path first, then a live fleet fan-out.
///
/// Both routes require the Gateway token (issue #369) via <see cref="AuthMiddleware.HasValidToken"/>
/// - the same Bearer-or-cookie check every other protected route uses - enforced HERE so the
/// gate holds even in production mode, where the global auth middleware is off
/// (the tray Gateway runs authEnabled=false). Missing/wrong token -> 401.
/// </summary>
internal static class GatewayVoiceTurnEndpoint
{
    /// <summary>Ceiling for one full Director-side turn (transcribe + Claude + summarize + TTS).
    /// Comfortably above the Director's own internal timeouts (60s ready-wait + 120s turn).</summary>
    private static readonly TimeSpan DirectorTurnTimeout = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder app, GatewayTurnJobStore store, DirectorRegistry registry,
        DirectorEndpointClient client, SessionOwnerCache? owners, string token,
        VoiceUploadStore? uploads = null, VoiceTurnArchive? archive = null)
    {
        // Disk-backed singletons captured by the route closures: the resumable upload staging and
        // the durable reply archive. Constructed here (once, at startup) when the host does not
        // inject them; tests inject roots of their own.
        uploads ??= new VoiceUploadStore();
        archive ??= new VoiceTurnArchive();

        app.MapPost("/sessions/{sid}/voice-turn/submit", async (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[GatewayVoiceTurn] POST /sessions/{sid}/voice-turn/submit from {ctx.Connection.RemoteIpAddress}");

            // Issue #369: token-gated even when the global AuthMiddleware is off (the
            // production tray Gateway runs authEnabled=false). Same mechanism as every
            // other protected Gateway route - Bearer header or the gateway cookie.
            if (!AuthMiddleware.HasValidToken(ctx, token))
            {
                FileLog.Write($"[GatewayVoiceTurn] submit sid={sid}: missing or invalid token from {ctx.Connection.RemoteIpAddress} -> 401");
                return Results.Json(new { error = "missing or invalid token" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

            // Capture the input NOW: the request body is gone once the 202 is written, and the
            // background task must be able to (re)build it for the Director.
            VoiceTurnInput input;
            try
            {
                input = await VoiceTurnInput.ReadAsync(ctx);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayVoiceTurn] submit sid={sid}: unreadable body: {ex.Message}");
                return Results.Json(new { error = "unreadable request body: " + ex.Message },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var (endpoint, resolveError) = await ResolveDirectorEndpointAsync(sid, registry, client, owners, "submit");
            if (resolveError is not null) return resolveError;

            var job = store.Create(sid);

            // Fire-and-forget by design: the 202 is the whole point - the caller never waits on
            // the Director. The task is its own entry point, so it owns its try-catch and ALWAYS
            // lands the job in a terminal stage (reply or error); a poll can never hang forever.
            _ = Task.Run(() => RunTurnAsync(job, endpoint!, sid, input, token, archive));

            FileLog.Write($"[GatewayVoiceTurn] submit sid={sid}: accepted turnId={job.TurnId}, director={endpoint}");
            return Results.Json(new { turn_id = job.TurnId, expires_at = job.ExpiresAt },
                statusCode: StatusCodes.Status202Accepted);
        });

        // ===== Resumable upload front door (guaranteed audio-turn) =======================
        // The phone records in MediaRecorder timeslices and uploads chunk-by-chunk, so a dropped
        // connection resumes at the next missing chunk instead of re-sending the whole clip. When
        // all chunks have landed, the assembled audio is fed into the SAME async turn worker the
        // submit route uses - so the resumable upload and the audio-reply turn are one pipeline.
        //
        //   POST   .../voice-turn/upload                       -> 200 { upload_id }
        //   PUT    .../voice-turn/upload/{uploadId}/chunk/{i}  -> 200 { ok }   (idempotent)
        //   POST   .../voice-turn/upload/{uploadId}/complete   -> 202 { turn_id } | 409 { missing }

        app.MapPost("/sessions/{sid}/voice-turn/upload", (string sid, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

            // The client supplies its locally-generated turn id as the Idempotency-Key; it doubles
            // as the upload id, so a retried upload/complete maps back to the same turn.
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var uploadId = uploads.Register(string.IsNullOrWhiteSpace(key) ? null : key);
            FileLog.Write($"[GatewayVoiceTurn] upload sid={sid}: registered uploadId={uploadId}");
            return Results.Json(new { upload_id = uploadId });
        });

        app.MapPut("/sessions/{sid}/voice-turn/upload/{uploadId}/chunk/{index:int}",
            async (string sid, string uploadId, int index, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();
            try
            {
                await uploads.StoreChunkAsync(uploadId, index, bytes, string.IsNullOrEmpty(sha) ? null : sha, ctx.RequestAborted);
                return Results.Json(new { ok = true, index, bytes = bytes.Length });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayVoiceTurn] upload chunk sid={sid} uploadId={uploadId} index={index} FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/sessions/{sid}/voice-turn/upload/{uploadId}/complete",
            async (string sid, string uploadId, VoiceTurnCompleteRequest? req, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            if (req is null || req.TotalChunks <= 0)
                return Results.Json(new { error = "totalChunks (>0) is required" }, statusCode: StatusCodes.Status400BadRequest);

            // Idempotency: a retried completion must NOT start a second turn. The live job index is
            // the fast path; the durable archive covers a retry after the in-memory job expired.
            if (store.FindTurnByUpload(uploadId) is { } liveJob)
            {
                FileLog.Write($"[GatewayVoiceTurn] complete sid={sid} uploadId={uploadId}: idempotent live turn={liveJob.TurnId}");
                return Results.Json(new { turn_id = liveJob.TurnId, expires_at = liveJob.ExpiresAt },
                    statusCode: StatusCodes.Status202Accepted);
            }
            if (archive.FindByUpload(uploadId) is { } archivedRec)
            {
                FileLog.Write($"[GatewayVoiceTurn] complete sid={sid} uploadId={uploadId}: idempotent archived turn={archivedRec.TurnId}");
                return Results.Json(new { turn_id = archivedRec.TurnId },
                    statusCode: StatusCodes.Status202Accepted);
            }

            AssembleResult assembled;
            try
            {
                assembled = await uploads.AssembleAsync(uploadId, req.TotalChunks, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayVoiceTurn] complete sid={sid} uploadId={uploadId} assemble FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }

            switch (assembled.Status)
            {
                case "unknown_upload":
                    return Results.Json(new { error = "unknown upload id" }, statusCode: StatusCodes.Status404NotFound);
                case "incomplete":
                    // Client-recoverable: re-send the listed chunks, then complete again.
                    return Results.Json(new { status = "incomplete", missing = assembled.Missing },
                        statusCode: StatusCodes.Status409Conflict);
            }

            var (endpoint, resolveError) = await ResolveDirectorEndpointAsync(sid, registry, client, owners, "complete");
            if (resolveError is not null) return resolveError;

            var job = store.Create(sid, uploadId);
            var input = VoiceTurnInput.FromAudio(assembled.Audio!, req.Mime ?? "audio/webm");
            uploads.Delete(uploadId);  // chunks assembled; staging no longer needed

            _ = Task.Run(() => RunTurnAsync(job, endpoint!, sid, input, token, archive));

            FileLog.Write($"[GatewayVoiceTurn] complete sid={sid} uploadId={uploadId}: accepted turnId={job.TurnId}, director={endpoint}");
            return Results.Json(new { turn_id = job.TurnId, expires_at = job.ExpiresAt },
                statusCode: StatusCodes.Status202Accepted);
        });

        // ===== Poll (live job, then durable archive fallback) ============================
        app.MapGet("/sessions/{sid}/voice-turn/{turnId}", (string sid, string turnId, HttpContext ctx) =>
        {
            // Issue #369: same token gate as the submit route above.
            if (!AuthMiddleware.HasValidToken(ctx, token))
            {
                FileLog.Write($"[GatewayVoiceTurn] poll sid={sid} turnId={turnId}: missing or invalid token from {ctx.Connection.RemoteIpAddress} -> 401");
                return Results.Json(new { error = "missing or invalid token" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var job = store.Get(turnId);
            if (job is not null && string.Equals(job.SessionId, sid, StringComparison.OrdinalIgnoreCase))
            {
                var s = job.Snapshot();
                return Results.Json(new
                {
                    turn_id = job.TurnId,
                    stage = s.Stage,
                    transcript = s.Transcript,
                    summary = s.Summary,
                    audioBase64 = s.AudioBase64,
                    message = s.ErrorMessage,
                    expires_at = job.ExpiresAt,
                });
            }

            // Fallback: the in-memory job is gone (expired / Gateway restarted) but the turn
            // completed and was archived. The reply "sits in the session" - serve it from disk.
            var rec = archive.Get(turnId);
            if (rec is not null && string.Equals(rec.SessionId, sid, StringComparison.OrdinalIgnoreCase))
            {
                var audio = archive.GetAudio(turnId);
                return Results.Json(new
                {
                    turn_id = rec.TurnId,
                    stage = rec.Stage,
                    transcript = rec.Transcript,
                    summary = rec.Summary,
                    audioBase64 = audio is { Length: > 0 } ? Convert.ToBase64String(audio) : "",
                    message = (string?)null,
                    archived = true,
                });
            }

            FileLog.Write($"[GatewayVoiceTurn] poll sid={sid} turnId={turnId}: unknown or expired -> 404");
            return Results.Json(new { error = "turn not found or expired" }, statusCode: StatusCodes.Status404NotFound);
        });

        // Durable replay of the reply audio (the artifact that "sits in the session").
        app.MapGet("/sessions/{sid}/voice-turn/{turnId}/audio", (string sid, string turnId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);

            var rec = archive.Get(turnId);
            if (rec is null || !string.Equals(rec.SessionId, sid, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "turn not found or expired" }, statusCode: StatusCodes.Status404NotFound);

            var audio = archive.GetAudio(turnId);
            if (audio is null || audio.Length == 0)
                return Results.Json(new { error = "no reply audio for this turn" }, statusCode: StatusCodes.Status404NotFound);

            return Results.File(audio, "audio/mpeg");
        });

        // The session's completed voice turns, newest first (read from the durable archive).
        app.MapGet("/sessions/{sid}/voice-turns", (string sid, HttpContext ctx, string? since) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);

            DateTime? sinceUtc = null;
            if (!string.IsNullOrWhiteSpace(since) &&
                DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                sinceUtc = parsed.ToUniversalTime();

            var turns = archive.ListForSession(sid, sinceUtc).Select(r => new
            {
                turn_id = r.TurnId,
                stage = r.Stage,
                summary = r.Summary,
                transcript = r.Transcript,
                has_audio = r.HasAudio,
                created_at = r.CreatedAtUtc,
            });
            return Results.Json(new { session_id = sid, turns });
        });
    }

    /// <summary>
    /// Resolve the owning Director's dialable Control API base URL for a session. Fast path: the
    /// cached owner (kept fresh by the fleet aggregator and the WS proxy) - a dictionary lookup,
    /// no fleet fan-out - and it still resolves when the Director has just gone dark (the background
    /// task then surfaces the failure as the job's error stage). Slow path: a live fleet fan-out
    /// that also gates on an exited session. Returns (endpoint, null) on success, or (null, error)
    /// with the IResult the route should return (404 / 410 / 503).
    /// </summary>
    private static async Task<(string? endpoint, IResult? error)> ResolveDirectorEndpointAsync(
        string sid, DirectorRegistry registry, DirectorEndpointClient client, SessionOwnerCache? owners, string verb)
    {
        if (owners?.OwnerOf(sid) is { } cachedOwnerId && registry.Get(cachedOwnerId) is { } cachedDir
            && DialEndpoint(cachedDir) is { } cachedEndpoint)
            return (cachedEndpoint, null);

        var (director, session) = await LocateOwningDirectorAsync(registry, client, sid);
        if (director is null || session is null)
        {
            FileLog.Write($"[GatewayVoiceTurn] {verb} sid={sid}: no owning director -> 404");
            return (null, Results.Json(new { error = "session not found" }, statusCode: StatusCodes.Status404NotFound));
        }
        if (IsExited(session))
        {
            FileLog.Write($"[GatewayVoiceTurn] {verb} sid={sid}: session exited -> 410");
            return (null, Results.Json(new { error = "session has exited" }, statusCode: StatusCodes.Status410Gone));
        }
        owners?.Remember(sid, director.DirectorId);
        var endpoint = DialEndpoint(director);
        if (endpoint is null)
        {
            FileLog.Write($"[GatewayVoiceTurn] {verb} sid={sid}: owner {director.DirectorId} has no dialable endpoint -> 503");
            return (null, Results.Json(new { error = "owning director has no reachable endpoint" },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }
        return (endpoint, null);
    }

    /// <summary>
    /// Background driver for one turn: POST the captured input to the owning Director's SSE
    /// voice-turn endpoint, mirror every stage event into <paramref name="job"/>, and ALWAYS
    /// leave the job terminal (reply or error). This is a top-level task entry point, so the
    /// try-catch boundary lives here.
    /// </summary>
    private static async Task RunTurnAsync(TurnJob job, string directorEndpoint, string sid,
        VoiceTurnInput input, string token, VoiceTurnArchive archive)
    {
        FileLog.Write($"[GatewayVoiceTurn] RunTurnAsync: turnId={job.TurnId}, sid={sid}, director={directorEndpoint}");
        try
        {
            using var http = new HttpClient { Timeout = DirectorTurnTimeout };
            if (!string.IsNullOrEmpty(token))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{directorEndpoint}/sessions/{sid}/voice-turn")
            {
                Content = input.BuildContent(),
            };
            using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                FileLog.Write($"[GatewayVoiceTurn] RunTurnAsync turnId={job.TurnId}: director answered {(int)resp.StatusCode}");
                job.SetError($"director returned {(int)resp.StatusCode}: {Truncate(body)}");
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var terminal = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
                var json = trimmed["data:".Length..].Trim();
                if (string.IsNullOrWhiteSpace(json)) continue;

                if (ApplyEvent(job, json)) { terminal = true; break; }
            }

            if (!terminal)
            {
                FileLog.Write($"[GatewayVoiceTurn] RunTurnAsync turnId={job.TurnId}: SSE stream ended without reply/error");
                job.SetError("director stream ended without a reply");
            }
            else
            {
                FileLog.Write($"[GatewayVoiceTurn] RunTurnAsync turnId={job.TurnId}: terminal stage={job.Snapshot().Stage}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayVoiceTurn] RunTurnAsync turnId={job.TurnId} FAILED: {ex.Message}");
            job.SetError("director unreachable: " + ex.Message);
        }
        finally
        {
            // Persist the completed reply so it outlives the in-memory job's TTL and a Gateway
            // restart - this is what makes the reply "sit in the session". Only successful replies
            // are archived: an errored turn legitimately re-runs on a retried completion. Best-effort
            // (the archive swallows its own failures), so a logging miss never breaks the turn.
            PersistReplyIfComplete(job, sid, archive);
        }
    }

    /// <summary>Write a terminal <c>reply</c> job to the durable archive (summary, transcript, and
    /// the decoded reply audio). No-op for any non-reply terminal stage.</summary>
    private static void PersistReplyIfComplete(TurnJob job, string sid, VoiceTurnArchive archive)
    {
        var snap = job.Snapshot();
        if (!string.Equals(snap.Stage, "reply", StringComparison.Ordinal)) return;

        byte[]? audio = null;
        if (!string.IsNullOrEmpty(snap.AudioBase64))
        {
            try { audio = Convert.FromBase64String(snap.AudioBase64); }
            catch (FormatException ex)
            {
                FileLog.Write($"[GatewayVoiceTurn] PersistReply turnId={job.TurnId}: bad audio base64: {ex.Message}");
            }
        }

        archive.Save(new VoiceTurnArchiveRecord
        {
            TurnId = job.TurnId,
            SessionId = sid,
            UploadId = job.UploadId,
            Stage = "reply",
            Transcript = snap.Transcript ?? "",
            Summary = snap.Summary ?? "",
            HasAudio = audio is { Length: > 0 },
            CreatedAtUtc = DateTime.UtcNow,
        }, audio);
    }

    /// <summary>
    /// Apply one Director SSE event payload to the job. Returns true when the event is terminal
    /// (reply or error). An unparseable payload is skipped - the stream's terminal event (or its
    /// EOF guard) still lands the job in a terminal stage.
    /// </summary>
    private static bool ApplyEvent(TurnJob job, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[GatewayVoiceTurn] ApplyEvent turnId={job.TurnId}: unparseable SSE payload skipped: {ex.Message}");
            return false;
        }

        using (doc)
        {
            var stage = ReadString(doc, "stage");
            if (string.IsNullOrEmpty(stage)) return false;

            switch (stage)
            {
                case "transcript":
                    job.SetTranscript(ReadString(doc, "text"));
                    return false;
                case "reply":
                    job.SetReply(ReadString(doc, "summary"), ReadString(doc, "audioBase64"));
                    return true;
                case "error":
                    job.SetError(ReadString(doc, "message") ?? "director reported an error");
                    return true;
                default:
                    // submitted/transcribing/waiting/thinking/summarizing - plain progress.
                    job.SetStage(stage);
                    return false;
            }
        }
    }

    private static string? ReadString(JsonDocument doc, string field)
        => doc.RootElement.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>
    /// Find the one Director that owns this session id, with its session row (so the submit can
    /// gate on an exited session). Fans out in parallel - mirrors GatewayEndpoints.LocateSessionAsync.
    /// </summary>
    private static async Task<(DirectorDto? director, SessionDto? session)> LocateOwningDirectorAsync(
        DirectorRegistry registry, DirectorEndpointClient client, string sid)
    {
        var lookups = registry.ListDirectors().Select(async d =>
        {
            var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
            var s = await client.GetSessionAsync(ep, sid);
            return (director: d, session: s);
        }).ToList();

        var results = await Task.WhenAll(lookups);
        foreach (var (director, session) in results)
            if (session is not null) return (director, session);
        return (null, null);
    }

    private static bool IsExited(SessionDto session)
        => string.Equals(session.Status, "Exited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.Status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    /// <summary>The base URL the Gateway dials to reach the Director's Control API: the
    /// ControlEndpoint (loopback for FSW-discovered, tailnet for HTTP-registered), else the
    /// TailnetEndpoint. Null when neither is usable.</summary>
    private static string? DialEndpoint(DirectorDto d)
    {
        var endpoint = !string.IsNullOrWhiteSpace(d.ControlEndpoint) ? d.ControlEndpoint : d.TailnetEndpoint;
        return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.TrimEnd('/');
    }

    private static string Truncate(string s)
        => s.Length <= 300 ? s : s[..300] + "...";

    /// <summary>
    /// The captured submit body, buffered so the background task can rebuild it after the 202
    /// has been written. Two shapes, matching the Director endpoint's own contract:
    /// multipart/form-data (optional text field + optional audio file) or a raw JSON body
    /// (forwarded verbatim - the Director validates it).
    /// </summary>
    private sealed class VoiceTurnInput
    {
        private readonly string? _text;
        private readonly byte[]? _audioBytes;
        private readonly string? _audioFileName;
        private readonly string? _audioContentType;
        private readonly string? _rawJson;

        private VoiceTurnInput(string? text, byte[]? audioBytes, string? audioFileName,
            string? audioContentType, string? rawJson)
        {
            _text = text;
            _audioBytes = audioBytes;
            _audioFileName = audioFileName;
            _audioContentType = audioContentType;
            _rawJson = rawJson;
        }

        /// <summary>Build an input from already-assembled audio bytes (the resumable-upload path):
        /// the Gateway buffered the chunks itself, so it hands the Director one multipart audio file.</summary>
        public static VoiceTurnInput FromAudio(byte[] audioBytes, string mime)
        {
            var m = mime ?? "audio/webm";
            var fileName = m.Contains("webm", StringComparison.OrdinalIgnoreCase) ? "audio.webm"
                : m.Contains("mp4", StringComparison.OrdinalIgnoreCase) || m.Contains("m4a", StringComparison.OrdinalIgnoreCase) ? "audio.m4a"
                : m.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? "audio.ogg"
                : m.Contains("mpeg", StringComparison.OrdinalIgnoreCase) || m.Contains("mp3", StringComparison.OrdinalIgnoreCase) ? "audio.mp3"
                : m.Contains("wav", StringComparison.OrdinalIgnoreCase) ? "audio.wav"
                : "audio.webm";
            return new VoiceTurnInput(text: null, audioBytes: audioBytes, audioFileName: fileName,
                audioContentType: m, rawJson: null);
        }

        public static async Task<VoiceTurnInput> ReadAsync(HttpContext ctx)
        {
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                var text = form["text"].ToString();
                var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();

                byte[]? audioBytes = null;
                string? fileName = null;
                string? contentType = null;
                if (file is not null && file.Length > 0)
                {
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms, ctx.RequestAborted);
                    audioBytes = ms.ToArray();
                    fileName = string.IsNullOrEmpty(file.FileName) ? "audio.webm" : file.FileName;
                    contentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
                }

                return new VoiceTurnInput(
                    string.IsNullOrWhiteSpace(text) ? null : text,
                    audioBytes, fileName, contentType, rawJson: null);
            }

            // JSON path: buffer the raw body and forward it verbatim - the Director's own
            // validation (text required) is the single source of truth for the JSON shape.
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ctx.RequestAborted);
            return new VoiceTurnInput(text: null, audioBytes: null, audioFileName: null,
                audioContentType: null, rawJson: raw);
        }

        /// <summary>Rebuild the HttpContent for the Director call. Safe to call once per turn.</summary>
        public HttpContent BuildContent()
        {
            if (_rawJson is not null)
                return new StringContent(_rawJson, Encoding.UTF8, "application/json");

            var form = new MultipartFormDataContent();
            if (_text is not null)
                form.Add(new StringContent(_text), "text");
            if (_audioBytes is not null)
            {
                var audio = new ByteArrayContent(_audioBytes);
                audio.Headers.ContentType = new MediaTypeHeaderValue(_audioContentType ?? "application/octet-stream");
                form.Add(audio, "audio", _audioFileName ?? "audio.webm");
            }
            if (_text is null && _audioBytes is null)
            {
                // Empty form: forward a placeholder field so the multipart body is valid; the
                // Director answers with its structured "text or audio required" error event,
                // which the background task mirrors into the job (the async error contract).
                form.Add(new StringContent(""), "text");
            }
            return form;
        }
    }
}

/// <summary>Body of <c>POST /sessions/{sid}/voice-turn/upload/{uploadId}/complete</c>: the chunk
/// count to reassemble and the audio MIME type the phone recorded with.</summary>
public sealed class VoiceTurnCompleteRequest
{
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
}
