using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the <c>/dictate</c> WebSocket endpoint that exposes the dictation
/// library to browser-based UIs.
///
/// One mode: streaming PCM16 over the OpenAI Realtime API. The browser
/// captures audio through Web Audio + AudioWorklet
/// (<c>/dictate-worklet.js</c>) at 24 kHz mono and ships chunks over the
/// WebSocket as they arrive. The earlier "batch via MediaRecorder" path
/// was removed once it became clear the Phase 4 offline AudioBuffer
/// covers the only case batch mode was needed for (transient upstream
/// failures).
///
/// Wire protocol (text frames are JSON; binary frames are PCM16 audio):
///
///   Server -> {"type":"ready"}
///   Client -> {"type":"start","profile":"default"}
///   Server -> {"type":"started"}
///   Server -> {"type":"state","value":"connected"}        (and on every transition)
///   Client -> &lt;binary frames&gt;                         (PCM16 audio chunks)
///   Server -> {"type":"partial","text":"..."}              (zero or more)
///   Server -> {"type":"state","value":"buffering"}         (e.g. on transient failure)
///   Client -> {"type":"stop"}                              (or abort)
///   Server -> {"type":"transcribing"}
///   Server -> {"type":"final","raw":"...","cleaned":"...","cleanupApplied":true,"profile":"default","reason":null}
///   (Server closes the socket.)
///
///   On error: Server -> {"type":"error","message":"..."}   then close.
///
/// Each connection gets its own offline AudioBuffer with a session-scoped
/// disk spill directory under
/// <c>%LOCALAPPDATA%/cc-director/dictation/buffer/&lt;session&gt;/</c>, so
/// transient provider failures do not lose spoken audio.
///
/// Localhost-only by default. The existing <see cref="ControlApiHost"/>
/// auth middleware applies if enabled.
/// </summary>
internal static class DictationEndpoint
{
    private const string BufferRootName = "buffer";

    // 24 kHz mono PCM16 = 48000 bytes/sec, so half a second is 24000 bytes.
    // This is the floor below which a close-without-stop is treated as a
    // genuine cancel (nothing worth saying was captured) rather than a dropped
    // recording whose audio we should recover.
    private const int MinRecoverableAudioBytes = 24_000;

    public static void Map(IEndpointRouteBuilder app, AgentOptions options)
    {
        app.MapGet("/dictate.html", () =>
        {
            var html = EmbeddedResources.Load("dictate.html");
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/dictate-worklet.js", () =>
        {
            var js = EmbeddedResources.Load("dictate-worklet.js");
            return Results.Content(js, "application/javascript; charset=utf-8");
        });

        app.MapGet("/dictate-client.js", () =>
        {
            var js = EmbeddedResources.Load("dictate-client.js");
            return Results.Content(js, "application/javascript; charset=utf-8");
        });

        // Recovered-dictation pickup: the browser polls this on load and on
        // tab-visible to see if a dropped recording was transcribed server-side
        // while it was gone, then offers to insert it.
        app.MapGet("/dictate/recovered", () =>
        {
            var items = RecoveredDictationStore.GetFresh()
                .Select(e => new
                {
                    id = e.Id,
                    text = e.Text,
                    ageSeconds = (int)(DateTime.UtcNow - e.CreatedUtc).TotalSeconds,
                })
                .ToArray();
            return Results.Json(new { recovered = items });
        });

        app.MapPost("/dictate/recovered/{id}/dismiss", (string id) =>
        {
            var removed = RecoveredDictationStore.Remove(id);
            return Results.Json(new { dismissed = removed });
        });

        app.MapGet("/dictate", async (HttpContext ctx) =>
        {
            FileLog.Write($"[DictationEndpoint] GET /dictate from {ctx.Connection.RemoteIpAddress}");
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("expected websocket upgrade");
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var remoteIp = ctx.Connection.RemoteIpAddress?.ToString();
            try
            {
                await ServeSessionAsync(ws, options, remoteIp, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DictationEndpoint] session FAILED: {ex.Message}");
                await TrySendErrorAsync(ws, ex.Message, ctx.RequestAborted);
                await TryCloseAsync(ws, WebSocketCloseStatus.InternalServerError, "internal error");
            }
        });
    }

    private static async Task ServeSessionAsync(WebSocket ws, AgentOptions options, string? remoteIp, CancellationToken ct)
    {
        await SendJsonAsync(ws, new { type = "ready" }, ct);

        var (startOk, startMsg, startError) = await ReceiveTextJsonAsync(ws, ct);
        if (!startOk)
        {
            await TrySendErrorAsync(ws, startError ?? "expected start frame", ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.ProtocolError, "no start frame");
            return;
        }

        if (!TryReadString(startMsg, "type", out var type) || type != "start")
        {
            await TrySendErrorAsync(ws, "first JSON frame must be {\"type\":\"start\"}", ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.ProtocolError, "bad start frame");
            return;
        }

        var profile = TryReadString(startMsg, "profile", out var p) && !string.IsNullOrWhiteSpace(p) ? p : "default";

        // Build the dictation pipeline for this connection. Always streaming
        // (PCM16 from the browser's AudioWorklet to the OpenAI Realtime API).
        // The offline AudioBuffer with disk spill handles the case batch
        // mode used to cover.
        var dictPath = options.ResolveDictationDictionaryPath();
        using var dictionary = new DictionaryLoader(dictPath, watch: false);

        IDictationProvider provider = new OpenAiRealtimeProvider(apiKey: options.ResolveOpenAiKey());

        using var cleanup = new CleanupOrchestrator(
            apiKey: options.ResolveOpenAiKey(),
            model: options.DictationCleanupModel);

        var bufferSpillDir = ResolveBufferSpillDir();
        using var audioBuffer = new AudioBuffer(spillDirectory: bufferSpillDir);
        // Live transcript preview (#215): the browser gets "partial" frames
        // continuously while the user talks, not only at the final commit.
        // The session owns and disposes it.
        var preview = new LivePreviewTranscriber(
            apiKey: options.ResolveOpenAiKey(),
            model: options.DictationPreviewModel);
        await using var session = new DictationSession(dictionary, provider, cleanup, audioBuffer, preview);

        session.OnPartial += partial =>
        {
            _ = SendJsonAsync(ws, new { type = "partial", text = partial }, ct);
        };

        session.OnStateChanged += state =>
        {
            _ = SendJsonAsync(ws, new { type = "state", value = StateToString(state) }, ct);
        };

        // Session-scoped diagnostics so we can audit every dictation cycle.
        var sessionId = Guid.NewGuid().ToString("N");
        var dict = dictionary.Current;
        var sessionStartUtc = DateTime.UtcNow;
        var recordingStart = System.Diagnostics.Stopwatch.StartNew();
        long stopElapsedMs = 0;
        long transcribedElapsedMs = 0;
        long audioBytesReceived = 0;
        string? clientError = null;

        await session.StartAsync(profile, ct);
        await SendJsonAsync(ws, new { type = "started" }, ct);

        FileLog.Write($"[DictationEndpoint] session started: sid={sessionId} profile={profile} "
                      + $"vocab={dict.Vocabulary.Count} patterns={dict.CommonMistranscriptions.Count} "
                      + $"dict={dictPath} spillDir={bufferSpillDir}");

        var rxBuffer = new byte[16 * 1024];
        var recoveredFromEarlyClose = false;
        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(rxBuffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // A clean close handshake without a 'stop' frame. If a
                    // meaningful amount of audio was captured, treat it as an
                    // implicit stop and recover (see RecoverOrDiscard).
                    if (TryRecoverOrDiscard("client closed before stop", out clientError))
                    {
                        recoveredFromEarlyClose = true;
                        break;
                    }
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var audio = await ReadFullBinaryAsync(ws, rxBuffer, result, ct);
                    if (audio.Length > 0)
                    {
                        audioBytesReceived += audio.Length;
                        await session.PushAudioAsync(audio, ct);
                    }
                    continue;
                }
                var textMsg = await ReadFullTextAsync(ws, rxBuffer, result, ct);
                using var doc = TryParseJson(textMsg);
                if (doc is null) continue;
                if (TryReadString(doc.RootElement, "type", out var t))
                {
                    if (t == "stop") break;
                    if (t == "abort")
                    {
                        FileLog.Write($"[DictationEndpoint] sid={sessionId} client aborted");
                        LogSessionRecord(sessionId, sessionStartUtc, profile, dict, recordingStart, 0, 0,
                            audioBytesReceived, transcript: null, options, remoteIp,
                            clientError: "client aborted");
                        await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "aborted");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // The connection ended WITHOUT a clean close handshake - the common
            // real-world case when a mobile browser is backgrounded or the
            // network blips. ReceiveAsync throws (cancellation via
            // RequestAborted, a transport-level WebSocketException, or an
            // IOException) instead of returning a Close message, and it can
            // interrupt mid-stream so fewer bytes were received than the client
            // sent. Whatever the cause, if we captured real audio it is the same
            // "lost recording" situation as a clean early close: recover it
            // rather than letting the outer handler discard everything.
            FileLog.Write($"[DictationEndpoint] sid={sessionId} receive loop ended abnormally: "
                          + $"{ex.GetType().Name}: {ex.Message}");
            if (!TryRecoverOrDiscard("connection dropped before stop", out clientError))
                return;
            recoveredFromEarlyClose = true;
        }

        // Local helper: decide whether an early/abnormal end-of-stream carried
        // enough audio to be worth finalizing. Returns true (with a "recovered:"
        // clientError) to fall through to the finalize block, or false (after
        // logging a discard record) to bail out.
        bool TryRecoverOrDiscard(string what, out string? error)
        {
            if (audioBytesReceived >= MinRecoverableAudioBytes)
            {
                FileLog.Write($"[DictationEndpoint] sid={sessionId} {what} with "
                              + $"{audioBytesReceived} bytes captured; recovering transcript");
                error = "recovered: " + what + " frame";
                return true;
            }
            FileLog.Write($"[DictationEndpoint] sid={sessionId} {what} "
                          + $"({audioBytesReceived} bytes, below recovery threshold)");
            LogSessionRecord(sessionId, sessionStartUtc, profile, dict, recordingStart, 0, 0,
                audioBytesReceived, transcript: null, options, remoteIp,
                clientError: "client closed before stop");
            error = null;
            return false;
        }

        recordingStart.Stop();
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        await SendJsonAsync(ws, new { type = "transcribing" }, ct);

        // On the recovery path the client connection is already gone, so its
        // cancellation token (RequestAborted) may already be firing. Finalizing
        // the transcription talks to OpenAI over a SEPARATE socket and must not
        // be cancelled just because the client vanished - the provider's own
        // 30s StopTimeout still bounds it. Use an independent token there.
        var finalizeCt = recoveredFromEarlyClose ? CancellationToken.None : ct;

        TranscriptResult? transcript = null;
        try
        {
            transcript = await session.StopAsync(finalizeCt);
            transcribedElapsedMs = stopWatch.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationEndpoint] sid={sessionId} StopAsync FAILED: {ex.Message}");
            clientError = "stop failed: " + ex.Message;
            await TrySendErrorAsync(ws, clientError, ct);
        }

        stopElapsedMs = stopWatch.ElapsedMilliseconds;

        if (transcript is not null)
        {
            await SendJsonAsync(ws, new
            {
                type = "final",
                raw = transcript.RawTranscript,
                cleaned = transcript.CleanedTranscript,
                cleanupApplied = transcript.CleanupApplied,
                profile = transcript.ProfileUsed,
                reason = transcript.CleanupFailureReason,
            }, ct);

            FileLog.Write($"[DictationEndpoint] sid={sessionId} done in {stopElapsedMs}ms: "
                          + $"raw_len={transcript.RawTranscript.Length} cleaned_len={transcript.CleanedTranscript.Length} "
                          + $"applied={transcript.CleanupApplied}");

            // The 'final' frame above could not reach a client that already
            // dropped the socket, so park the recovered transcript for the
            // browser to pick up on reconnect. Only on the recovery path - a
            // normal stop already delivered the text live.
            if (recoveredFromEarlyClose)
            {
                var recoveredText = string.IsNullOrWhiteSpace(transcript.CleanedTranscript)
                    ? transcript.RawTranscript
                    : transcript.CleanedTranscript;
                RecoveredDictationStore.Add(recoveredText);
                FileLog.Write($"[DictationEndpoint] sid={sessionId} parked recovered transcript "
                              + $"({recoveredText?.Length ?? 0} chars) for browser pickup");
            }
        }

        LogSessionRecord(sessionId, sessionStartUtc, profile, dict, recordingStart,
            transcribedElapsedMs, stopElapsedMs, audioBytesReceived, transcript, options, remoteIp,
            clientError);

        await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "done");
    }

    private static string ResolveBufferSpillDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bufRoot = Path.Combine(localAppData, "cc-director", "dictation", BufferRootName);
        // One subdir per session so concurrent dictations do not collide.
        return Path.Combine(bufRoot, Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Write one JSONL session record for offline analysis. Fire-and-forget;
    /// errors are logged but never surfaced. Called from both the normal
    /// completion path and the abort/close paths so every session leaves a
    /// trace.
    /// </summary>
    private static void LogSessionRecord(
        string sessionId,
        DateTime sessionStartUtc,
        string profile,
        DictationDictionary dict,
        System.Diagnostics.Stopwatch recordingStopwatch,
        long stopToTranscribedMs,
        long stopToCleanedMs,
        long audioBytesReceived,
        TranscriptResult? transcript,
        AgentOptions options,
        string? remoteIp,
        string? clientError)
    {
        var record = new DictationSessionRecord(
            TimestampUtc: sessionStartUtc.ToString("o"),
            SessionId: sessionId,
            Profile: profile,
            VocabularyTermCount: dict.Vocabulary.Count,
            MistranscriptionPatternCount: dict.CommonMistranscriptions.Count,
            RecordingDurationMs: recordingStopwatch.ElapsedMilliseconds,
            StopToTranscribedMs: stopToTranscribedMs,
            StopToCleanedMs: stopToCleanedMs,
            AudioBytesReceived: (int)Math.Min(audioBytesReceived, int.MaxValue),
            RawTranscript: transcript?.RawTranscript ?? "",
            CleanedTranscript: transcript?.CleanedTranscript ?? "",
            CleanupApplied: transcript?.CleanupApplied ?? false,
            CleanupReason: transcript?.CleanupFailureReason,
            CleanupModel: options.DictationCleanupModel,
            RemoteIp: remoteIp,
            ClientError: clientError,
            Source: "endpoint");

        // Off the WebSocket hot path; never block the close-out on disk I/O.
        Task.Run(() => DictationSessionLog.TryAppend(record));
    }

    private static string StateToString(ConnectionState s) => s switch
    {
        ConnectionState.Idle => "idle",
        ConnectionState.Connected => "connected",
        ConnectionState.Buffering => "buffering",
        ConnectionState.Reconnecting => "reconnecting",
        ConnectionState.Failed => "failed",
        _ => "unknown",
    };

    // ===== helpers ===========================================================

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationEndpoint] SendJsonAsync failed: {ex.Message}");
        }
    }

    private static async Task<(bool ok, JsonElement msg, string? error)> ReceiveTextJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close)
            return (false, default, "client closed");
        if (result.MessageType != WebSocketMessageType.Text)
            return (false, default, "expected text frame");
        var text = await ReadFullTextAsync(ws, buffer, result, ct);
        var doc = TryParseJson(text);
        if (doc is null) return (false, default, "invalid JSON");
        return (true, doc.RootElement.Clone(), null);
    }

    private static async Task<byte[]> ReadFullBinaryAsync(WebSocket ws, byte[] firstBuffer, WebSocketReceiveResult firstResult, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        ms.Write(firstBuffer, 0, firstResult.Count);
        var result = firstResult;
        while (!result.EndOfMessage)
        {
            result = await ws.ReceiveAsync(firstBuffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            ms.Write(firstBuffer, 0, result.Count);
        }
        return ms.ToArray();
    }

    private static async Task<string> ReadFullTextAsync(WebSocket ws, byte[] firstBuffer, WebSocketReceiveResult firstResult, CancellationToken ct)
    {
        var bytes = await ReadFullBinaryAsync(ws, firstBuffer, firstResult, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private static JsonDocument? TryParseJson(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch { return null; }
    }

    private static bool TryReadString(JsonElement obj, string key, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(key, out var prop)) return false;
        if (prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString() ?? "";
        return true;
    }

    private static async Task TrySendErrorAsync(WebSocket ws, string message, CancellationToken ct)
    {
        try { await SendJsonAsync(ws, new { type = "error", message }, ct); }
        catch { /* best effort */ }
    }

    private static async Task TryCloseAsync(WebSocket ws, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                await ws.CloseAsync(status, description, CancellationToken.None);
        }
        catch { /* best effort */ }
    }
}
