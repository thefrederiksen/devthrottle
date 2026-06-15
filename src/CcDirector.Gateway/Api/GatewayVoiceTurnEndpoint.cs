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
///        audioReady, audioLength, message } from the job cache (no Director call); 404 when
///        the turn id is unknown or past its 10-minute TTL. SLIM by default (issue #407): the
///        reply audio bytes are NOT in the poll; audioBase64 is included only when the caller
///        opts in (?includeAudio=1 / X-Include-Audio) for one-release back-compat with old phones.
///
///   GET  /sessions/{sid}/voice-turn/{turnId}/audio -> 200 raw audio/mpeg (or 206 Partial
///        Content for an HTTP Range request), served from the same cached job (issue #407);
///        404 when the turn is unknown/expired or has no audio. The dedicated, resumable fetch
///        so a dropped audio download resumes via Range instead of re-downloading the whole blob.
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
        DirectorEndpointClient client, SessionOwnerCache? owners, string token)
    {
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

            // Resolve the owning Director. Fast path: the cached owner (kept fresh by the fleet
            // aggregator and the WS proxy) - a dictionary lookup, no fleet fan-out, and it still
            // resolves when the Director has just gone dark (the background task then surfaces
            // the failure as the job's error stage, which is exactly the async contract).
            string? endpoint = null;
            if (owners?.OwnerOf(sid) is { } cachedOwnerId && registry.Get(cachedOwnerId) is { } cachedDir)
                endpoint = DialEndpoint(cachedDir);

            if (endpoint is null)
            {
                var (director, session) = await LocateOwningDirectorAsync(registry, client, sid);
                if (director is null || session is null)
                {
                    FileLog.Write($"[GatewayVoiceTurn] submit sid={sid}: no owning director -> 404");
                    return Results.Json(new { error = "session not found" }, statusCode: StatusCodes.Status404NotFound);
                }
                if (IsExited(session))
                {
                    FileLog.Write($"[GatewayVoiceTurn] submit sid={sid}: session exited -> 410");
                    return Results.Json(new { error = "session has exited" }, statusCode: StatusCodes.Status410Gone);
                }
                owners?.Remember(sid, director.DirectorId);
                endpoint = DialEndpoint(director);
                if (endpoint is null)
                {
                    FileLog.Write($"[GatewayVoiceTurn] submit sid={sid}: owner {director.DirectorId} has no dialable endpoint -> 503");
                    return Results.Json(new { error = "owning director has no reachable endpoint" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            var job = store.Create(sid);

            // Fire-and-forget by design: the 202 is the whole point - the caller never waits on
            // the Director. The task is its own entry point, so it owns its try-catch and ALWAYS
            // lands the job in a terminal stage (reply or error); a poll can never hang forever.
            _ = Task.Run(() => RunTurnAsync(job, endpoint, sid, input, token));

            FileLog.Write($"[GatewayVoiceTurn] submit sid={sid}: accepted turnId={job.TurnId}, director={endpoint}");
            return Results.Json(new { turn_id = job.TurnId, expires_at = job.ExpiresAt },
                statusCode: StatusCodes.Status202Accepted);
        });

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
            if (job is null || !string.Equals(job.SessionId, sid, StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[GatewayVoiceTurn] poll sid={sid} turnId={turnId}: unknown or expired -> 404");
                return Results.Json(new { error = "turn not found or expired" }, statusCode: StatusCodes.Status404NotFound);
            }

            var s = job.Snapshot();

            // Issue #407: the poll is SLIM by default - it advertises that the reply audio is
            // ready and how long it is, but does NOT carry the (multi-megabyte) bytes. The phone
            // fetches the audio from the dedicated, resumable audio endpoint below. The slim
            // response size is small and constant regardless of reply length.
            //
            // Back-compat for one release (issue #407): an older phone that has not yet learned
            // the audio endpoint asks for the inline bytes with ?includeAudio=1 (or the
            // X-Include-Audio: 1 header) and still receives audioBase64 exactly as before. New
            // phones omit it and get the slim response. When the flag is absent audioBase64 is
            // null, so the field is always present (the shape never changes) but empty by default.
            var includeAudio = WantsInlineAudio(ctx);

            return Results.Json(new
            {
                turn_id = job.TurnId,
                stage = s.Stage,
                transcript = s.Transcript,
                summary = s.Summary,
                audioReady = s.AudioReady,
                audioLength = s.AudioLength,
                audioBase64 = includeAudio ? s.AudioBase64 : null,
                message = s.ErrorMessage,
                expires_at = job.ExpiresAt,
            });
        });

        // Issue #407: dedicated, resumable reply-audio fetch. Decoupling the heavy audio transfer
        // from cheap status polling means a mid-download drop resumes via an HTTP Range request
        // (206 Partial Content) instead of restarting the whole blob, and a poll never carries
        // megabytes again. Served from the same cached job; 404 once the job is unknown/expired.
        app.MapGet("/sessions/{sid}/voice-turn/{turnId}/audio", (string sid, string turnId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
            {
                FileLog.Write($"[GatewayVoiceTurn] audio sid={sid} turnId={turnId}: missing or invalid token from {ctx.Connection.RemoteIpAddress} -> 401");
                return Results.Json(new { error = "missing or invalid token" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var job = store.Get(turnId);
            if (job is null || !string.Equals(job.SessionId, sid, StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[GatewayVoiceTurn] audio sid={sid} turnId={turnId}: unknown or expired -> 404");
                return Results.Json(new { error = "turn not found or expired" }, statusCode: StatusCodes.Status404NotFound);
            }

            var audio = job.GetAudioBytes();
            if (audio.Length == 0)
            {
                // The job exists but has no audio yet (still in flight) or never will (no TTS
                // key). 404 is the honest answer: there is nothing to fetch from this endpoint.
                FileLog.Write($"[GatewayVoiceTurn] audio sid={sid} turnId={turnId}: no audio available -> 404");
                return Results.Json(new { error = "no audio available for this turn" }, statusCode: StatusCodes.Status404NotFound);
            }

            // Results.Bytes with enableRangeProcessing serves the full body (200) when no Range
            // header is present and a partial body (206 Partial Content) with Content-Range when
            // one is - the framework parses/validates the range and emits 416 on an unsatisfiable
            // one. This is the resume primitive: a dropped download re-requests only the missing tail.
            FileLog.Write($"[GatewayVoiceTurn] audio sid={sid} turnId={turnId}: serving {audio.Length} bytes (range={ctx.Request.Headers.Range})");
            return Results.Bytes(audio, contentType: "audio/mpeg", enableRangeProcessing: true,
                fileDownloadName: null, entityTag: null, lastModified: null);
        });
    }

    /// <summary>
    /// Whether the poll caller opted into the inline base64 audio (the pre-#407 contract). New
    /// phones omit this and take the slim poll + dedicated audio fetch; only an older phone that
    /// has not learned the audio endpoint asks for the bytes inline, via ?includeAudio=1 or the
    /// X-Include-Audio header. Accepts 1/true/yes (case-insensitive).
    /// </summary>
    private static bool WantsInlineAudio(HttpContext ctx)
    {
        if (ctx.Request.Query.TryGetValue("includeAudio", out var q) && IsTruthy(q.ToString()))
            return true;
        if (ctx.Request.Headers.TryGetValue("X-Include-Audio", out var h) && IsTruthy(h.ToString()))
            return true;
        return false;
    }

    private static bool IsTruthy(string? v)
        => string.Equals(v, "1", StringComparison.Ordinal)
        || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Background driver for one turn: POST the captured input to the owning Director's SSE
    /// voice-turn endpoint, mirror every stage event into <paramref name="job"/>, and ALWAYS
    /// leave the job terminal (reply or error). This is a top-level task entry point, so the
    /// try-catch boundary lives here.
    /// </summary>
    private static async Task RunTurnAsync(TurnJob job, string directorEndpoint, string sid,
        VoiceTurnInput input, string token)
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
