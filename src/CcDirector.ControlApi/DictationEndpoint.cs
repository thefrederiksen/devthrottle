using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the <c>/dictate</c> WebSocket endpoint that exposes dictation to
/// browser-based UIs. It is the web twin of the desktop
/// <c>BatchDictationRecorder</c> and obeys the same canonical contract -
/// docs/architecture/dictation/DICTATION_UX_SPEC.md.
///
/// WHOLE-AUDIO BATCH, every mode. The browser captures PCM16 (24 kHz mono) via
/// Web Audio + AudioWorklet (<c>/dictate-worklet.js</c>) and ships chunks over
/// the WebSocket as it speaks. NO text is produced while talking: there is no
/// streaming/partial transcription and no realtime socket. When the client sends
/// <c>stop</c> the server wraps the WHOLE captured clip in one WAV blob and runs
/// it through the ONE shared <see cref="BatchTranscriptionPipeline"/> (issue #587)
/// - the exact pipeline the desktop uses - which transcribes once and applies the
/// validated dictionary corrector only. This is what gives every surface the same
/// whole-clip quality instead of the lightly-reworded realtime transcript.
///
/// Pause/Resume is a CLIENT concern: the client opens a fresh <c>/dictate</c>
/// connection per recording segment and accumulates the cleaned segments itself,
/// mirroring the desktop dialog. The server therefore stays single-shot per
/// connection (start -> audio -> stop -> final -> close).
///
/// Wire protocol (text frames are JSON; binary frames are PCM16 audio):
///
///   Server -> {"type":"ready"}
///   Client -> {"type":"start","profile":"default"}
///   Server -> {"type":"started"}
///   Client -> &lt;binary frames&gt;                         (PCM16 audio chunks)
///   Client -> {"type":"stop"}                              (or abort)
///   Server -> {"type":"transcribing"}
///   Server -> {"type":"final","raw":"...","cleaned":"...","cleanupApplied":true,"profile":"default","reason":null}
///   (Server closes the socket.)
///
///   On error: Server -> {"type":"error","message":"..."}   then close.
///
/// Audio completeness gate (issue #586): only an explicit <c>stop</c> frame
/// finalizes and transcribes - that is the client's signal that the WHOLE segment
/// has been sent. A clean close without <c>stop</c>, or an abnormal mid-stream
/// drop, NEVER transcribes the partial audio it captured; it records an explicit
/// "discarded" outcome and discards the bytes.
///
/// Localhost-only by default. The existing <see cref="ControlApiHost"/> auth
/// middleware applies if enabled.
/// </summary>
internal static class DictationEndpoint
{
    // The browser captures at this fixed format (see dictation-overlay.js: the
    // AudioContext is opened at 24 kHz and the pcm16-writer worklet emits mono
    // 16-bit PCM). The server wraps the accumulated PCM in a WAV header using
    // exactly this format before the single batch transcription.
    private const int CaptureSampleRate = 24000;
    private const int CaptureChannels = 1;
    private const int CaptureBitsPerSample = 16;

    public static void Map(IEndpointRouteBuilder app, AgentOptions options, OpenAiKeyResolver keyResolver, DictionaryResolver dictionaryResolver)
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

        app.MapGet("/dictation-overlay.js", () =>
        {
            // The shared Dictate overlay (same file the Cockpit serves), embedded as a linked
            // resource. Replaces the old per-Director dictate-client.js.
            var js = EmbeddedResources.Load("dictation-overlay.js");
            return Results.Content(js, "application/javascript; charset=utf-8");
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
                await ServeSessionAsync(ws, options, keyResolver, dictionaryResolver, remoteIp, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DictationEndpoint] session FAILED: {ex.Message}");
                await TrySendErrorAsync(ws, ex.Message, ctx.RequestAborted);
                await TryCloseAsync(ws, WebSocketCloseStatus.InternalServerError, "internal error");
            }
        });
    }

    private static async Task ServeSessionAsync(WebSocket ws, AgentOptions options, OpenAiKeyResolver keyResolver, DictionaryResolver dictionaryResolver, string? remoteIp, CancellationToken ct)
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

        // Resolve the transcription routing target for this Director's mode (issue #497):
        //   - BYO        -> the user's own OpenAI key against api.openai.com.
        //   - DevThrottle -> a dt_ key against devthrottle.com's OpenAI-compatible managed proxy.
        // The key comes from the Gateway vault when attached, the local Settings > Voice key when
        // standalone (BYO only). No key -> dictation is unavailable; tell the user where to set one
        // rather than failing with a raw error. The BYO OpenAI key is only ever paired with the
        // OpenAI base URL - it is never sent to devthrottle.com. Whichever mode resolves, the SAME
        // whole-clip batch POST is used (BatchTranscriptionPipeline), so there is no realtime socket.
        var routing = await keyResolver.ResolveEndpointAsync(ct);
        if (routing is null)
        {
            FileLog.Write($"[DictationEndpoint] no transcription key available (usesGateway={keyResolver.UsesGateway})");
            await TrySendErrorAsync(ws, keyResolver.UnavailableMessage, ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "no api key");
            return;
        }

        // Pull the latest glossary from the Gateway when connected and refresh the local cache
        // (#253); standalone or unreachable falls back to the existing cache. Resolving here (start
        // of each dictation) is the hot-reload path - a Cockpit edit lands on the next utterance.
        var dictionary = await dictionaryResolver.ResolveAsync(ct);

        // Session-scoped diagnostics so we can audit every dictation cycle.
        var sessionId = Guid.NewGuid().ToString("N");
        var sessionStartUtc = DateTime.UtcNow;
        var recordingStart = System.Diagnostics.Stopwatch.StartNew();
        long audioBytesReceived = 0;

        // Client-measured capture-health carried on the stop frame (issue #863): the recording
        // wall-clock the browser mic was open, the frame count, and the worst inter-frame gap.
        // Comparing the client recording wall-clock to the bytes the SERVER actually received is the
        // end-to-end audio deficit (capture loss plus any network loss); the gap tells a local stall
        // apart from clean capture. Zero until a stop frame supplies them.
        double clientRecordingMs = 0;
        int clientFrames = 0;
        double clientMaxGapMs = 0;

        await SendJsonAsync(ws, new { type = "started" }, ct);

        FileLog.Write($"[DictationEndpoint] session started: sid={sessionId} profile={profile} "
                      + $"vocab={dictionary.Vocabulary.Count} patterns={dictionary.CommonMistranscriptions.Count} "
                      + $"mode={routing.Mode.ToConfigString()} model={routing.Model} baseUrl={routing.BaseUrl}");

        // Accumulate the whole segment's PCM16 locally. Nothing leaves the machine until an explicit
        // 'stop' wraps it in one WAV and sends it through the shared batch pipeline. This is the same
        // capture-everything-then-transcribe-once shape as the desktop BatchDictationRecorder.
        using var pcm = new MemoryStream();
        var rxBuffer = new byte[16 * 1024];

        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(rxBuffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // A clean close handshake WITHOUT a 'stop' frame. The client did not signal that
                    // the whole segment was sent, so the audio is partial by definition. Fail
                    // explicitly - never transcribe it (issue #586).
                    FileLog.Write($"[DictationEndpoint] sid={sessionId} client closed before stop "
                                  + $"({audioBytesReceived} bytes captured); discarding partial audio, no transcript");
                    recordingStart.Stop();
                    LogSessionRecord(sessionId, sessionStartUtc, profile, dictionary, recordingStart, 0, 0,
                        audioBytesReceived, raw: null, cleaned: null, applied: false, reason: null, options, remoteIp,
                        clientError: "client closed before stop - partial audio discarded (not transcribed)");
                    await TrySendErrorAsync(ws, "recording ended before stop; partial audio was not transcribed - please re-record", ct);
                    await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "closed before stop");
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var audio = await ReadFullBinaryAsync(ws, rxBuffer, result, ct);
                    if (audio.Length > 0)
                    {
                        audioBytesReceived += audio.Length;
                        pcm.Write(audio, 0, audio.Length);
                    }
                    continue;
                }
                var textMsg = await ReadFullTextAsync(ws, rxBuffer, result, ct);
                using var doc = TryParseJson(textMsg);
                if (doc is null) continue;
                if (TryReadString(doc.RootElement, "type", out var t))
                {
                    if (t == "stop")
                    {
                        ReadCaptureHealth(doc.RootElement, ref clientRecordingMs, ref clientFrames, ref clientMaxGapMs);
                        break;
                    }
                    if (t == "abort")
                    {
                        FileLog.Write($"[DictationEndpoint] sid={sessionId} client aborted");
                        recordingStart.Stop();
                        LogSessionRecord(sessionId, sessionStartUtc, profile, dictionary, recordingStart, 0, 0,
                            audioBytesReceived, raw: null, cleaned: null, applied: false, reason: null, options, remoteIp,
                            clientError: "client aborted");
                        await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "aborted");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // The connection ended WITHOUT a clean close handshake and WITHOUT a 'stop' frame - a
            // mobile browser backgrounded, the network blipped, or the request was aborted. The audio
            // is partial; we do NOT transcribe it. Fail explicitly and discard.
            FileLog.Write($"[DictationEndpoint] sid={sessionId} connection dropped before stop "
                          + $"({ex.GetType().Name}: {ex.Message}); discarding partial audio, no transcript");
            recordingStart.Stop();
            LogSessionRecord(sessionId, sessionStartUtc, profile, dictionary, recordingStart, 0, 0,
                audioBytesReceived, raw: null, cleaned: null, applied: false, reason: null, options, remoteIp,
                clientError: "connection dropped before stop - partial audio discarded (not transcribed)");
            await TrySendErrorAsync(ws, "connection dropped before stop; partial audio was not transcribed - please re-record", ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "dropped before stop");
            return;
        }

        recordingStart.Stop();
        await SendJsonAsync(ws, new { type = "transcribing" }, ct);

        var pcmBytes = pcm.ToArray();

        // Completeness gate: an empty capture can never produce a real transcript. The shared
        // pipeline itself refuses an empty blob; we check first so the user gets a clear message.
        if (pcmBytes.Length == 0)
        {
            FileLog.Write($"[DictationEndpoint] sid={sessionId} stop with no audio captured");
            LogSessionRecord(sessionId, sessionStartUtc, profile, dictionary, recordingStart, 0, 0,
                audioBytesReceived, raw: null, cleaned: null, applied: false, reason: null, options, remoteIp,
                clientError: "stop with no audio captured");
            await TrySendErrorAsync(ws, "no audio was captured - please check your microphone and re-record", ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "no audio");
            return;
        }

        // Wrap the whole captured PCM in one WAV blob and transcribe ONCE through the shared batch
        // pipeline (the same one the desktop uses). The dictionary corrector is the only text transform.
        var wav = PcmWav.Wrap(pcmBytes, CaptureSampleRate, CaptureChannels, CaptureBitsPerSample);

        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        BatchTranscriptionResult? transcript = null;
        string? clientError = null;
        try
        {
            using var pipeline = new BatchTranscriptionPipeline(cleanupModel: options.DictationCleanupModel);
            transcript = await pipeline.TranscribeAsync(wav, "dictation.wav", routing, dictionary, profile, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationEndpoint] sid={sessionId} transcription FAILED: {ex.Message}");
            clientError = "transcription failed: " + ex.Message;
            await TrySendErrorAsync(ws, clientError, ct);
        }
        stopWatch.Stop();

        if (transcript is not null)
        {
            await SendJsonAsync(ws, new
            {
                type = "final",
                raw = transcript.RawTranscript,
                cleaned = transcript.CorrectedTranscript,
                cleanupApplied = transcript.DictionaryApplied,
                profile,
                reason = transcript.Reason,
            }, ct);

            FileLog.Write($"[DictationEndpoint] sid={sessionId} done in {stopWatch.ElapsedMilliseconds}ms: "
                          + $"raw_len={transcript.RawTranscript.Length} cleaned_len={transcript.CorrectedTranscript.Length} "
                          + $"applied={transcript.DictionaryApplied}");
        }

        LogSessionRecord(sessionId, sessionStartUtc, profile, dictionary, recordingStart,
            stopWatch.ElapsedMilliseconds, stopWatch.ElapsedMilliseconds, audioBytesReceived,
            raw: transcript?.RawTranscript, cleaned: transcript?.CorrectedTranscript,
            applied: transcript?.DictionaryApplied ?? false, reason: transcript?.Reason,
            options, remoteIp, clientError,
            clientRecordedMs: clientRecordingMs, clientFrames: clientFrames, clientMaxGapMs: clientMaxGapMs);

        await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "done");
    }

    /// <summary>
    /// Write one JSONL session record for offline analysis. Fire-and-forget; errors are logged but
    /// never surfaced. Called from both the normal completion path and the abort/close/drop paths so
    /// every session leaves a trace.
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
        string? raw,
        string? cleaned,
        bool applied,
        string? reason,
        AgentOptions options,
        string? remoteIp,
        string? clientError,
        double clientRecordedMs = 0,
        int clientFrames = 0,
        double clientMaxGapMs = 0)
    {
        // Expected bytes from the CLIENT recording wall-clock (24 kHz mono PCM16 = 48000 bytes/sec);
        // comparing it to the bytes the server actually received is the end-to-end audio deficit.
        var expectedBytes = (long)(48000.0 * clientRecordedMs / 1000.0);

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
            RawTranscript: raw ?? "",
            CleanedTranscript: cleaned ?? "",
            CleanupApplied: applied,
            CleanupReason: reason,
            CleanupModel: options.DictationCleanupModel,
            RemoteIp: remoteIp,
            ClientError: clientError,
            Source: "endpoint",
            ExpectedAudioBytes: expectedBytes,
            CaptureCallbackCount: clientFrames,
            MaxCaptureCallbackGapMs: clientMaxGapMs,
            RecordedWallMs: clientRecordedMs);

        // Off the WebSocket hot path; never block the close-out on disk I/O.
        Task.Run(() => DictationSessionLog.TryAppend(record));
    }

    /// <summary>
    /// Read the optional capture-health object the client attaches to its stop frame
    /// (<c>{"type":"stop","health":{"recordingMs":..,"frames":..,"maxFrameGapMs":..}}</c>). Missing or
    /// malformed fields leave the outputs untouched (they stay 0) - it is diagnostics, never required.
    /// </summary>
    private static void ReadCaptureHealth(JsonElement stop, ref double recordingMs, ref int frames, ref double maxGapMs)
    {
        if (stop.ValueKind != JsonValueKind.Object) return;
        if (!stop.TryGetProperty("health", out var h) || h.ValueKind != JsonValueKind.Object) return;
        if (h.TryGetProperty("recordingMs", out var r) && r.ValueKind == JsonValueKind.Number) recordingMs = r.GetDouble();
        if (h.TryGetProperty("frames", out var f) && f.ValueKind == JsonValueKind.Number) frames = f.GetInt32();
        if (h.TryGetProperty("maxFrameGapMs", out var g) && g.ValueKind == JsonValueKind.Number) maxGapMs = g.GetDouble();
    }

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
