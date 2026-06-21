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
/// Audio completeness gate (issue #586): only an explicit <c>stop</c> frame
/// finalizes and transcribes - that is the client's signal that the WHOLE
/// utterance has been streamed. An early or dropped connection (a clean close
/// without <c>stop</c>, or an abnormal mid-stream drop) never transcribes the
/// partial audio it captured; it sends a typed <c>error</c> frame and discards.
/// The earlier "recover whatever arrived and transcribe it" truncation path
/// (the byte-threshold recovery + server-side parked-transcript pickup) has been
/// removed so a partial recording can never reach the transcriber.
///
/// Localhost-only by default. The existing <see cref="ControlApiHost"/>
/// auth middleware applies if enabled.
/// </summary>
internal static class DictationEndpoint
{
    private const string BufferRootName = "buffer";

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

        app.MapGet("/dictate-client.js", () =>
        {
            var js = EmbeddedResources.Load("dictate-client.js");
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
        //   - BYO   -> the user's own OpenAI key against api.openai.com (the live Realtime path).
        //   - DevThrottle -> a dt_ key against devthrottle.com's OpenAI-compatible managed proxy.
        // The key comes from the Gateway vault when attached, the local Settings > Voice key when
        // standalone (BYO only). No key -> dictation is unavailable; tell the user where to set one
        // rather than failing with a raw error. The BYO OpenAI key is only ever paired with the
        // OpenAI base URL - it is never sent to devthrottle.com.
        var endpoint = await keyResolver.ResolveEndpointAsync(ct);
        if (endpoint is null)
        {
            FileLog.Write($"[DictationEndpoint] no transcription key available (usesGateway={keyResolver.UsesGateway})");
            await TrySendErrorAsync(ws, keyResolver.UnavailableMessage, ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "no api key");
            return;
        }
        var apiKey = endpoint.ApiKey;

        // Build the dictation pipeline for this connection. Always streaming
        // (PCM16 from the browser's AudioWorklet to the OpenAI Realtime API).
        // The offline AudioBuffer with disk spill handles the case batch
        // mode used to cover.
        // Pull the latest glossary from the Gateway when connected and refresh the local cache
        // file (#253); standalone or unreachable falls back to the existing cache. Resolving here
        // (start of each dictation) is the hot-reload path - a Cockpit edit lands on the next one.
        await dictionaryResolver.ResolveAsync(ct);
        var dictPath = options.ResolveDictationDictionaryPath();
        using var dictionary = new DictionaryLoader(dictPath, watch: false);

        // Route the pipeline by the routing TRANSPORT (issue #513), not the mode name: the routing
        // target now declares which wire the provider offers, and the pipeline honors exactly that.
        //   - realtime (BYO/OpenAI): the OpenAI Realtime WebSocket provider (true low-latency
        //     partials) against api.openai.com, with gpt-4o-transcribe, chat-completions cleanup, and
        //     the live preview - the existing path.
        //   - batch (DevThrottle/Groq): the OpenAI-COMPATIBLE batch endpoint
        //     (POST /audio/transcriptions) at devthrottle.com/api/v1 with whisper-large-v3. Groq has
        //     NO Realtime API, so the Realtime WebSocket is NEVER opened for a batch transport. Cleanup
        //     is SKIPPED (DevThrottle has no inference proxy and Whisper output is already clean - we
        //     do not route the cleanup LLM call to OpenAI in DevThrottle mode), and there is no
        //     streaming preview. The pipeline delivers one final raw transcript.
        // The provider-correct model travels with the routing target (issue #506/#513): on a Gateway
        // the Gateway serves it, standalone it is the per-mode value. The selected provider takes it.
        FileLog.Write($"[DictationEndpoint] routing: mode={endpoint.Mode.ToConfigString()} "
                      + $"transport={endpoint.Transport.ToConfigString()} model={endpoint.Model} baseUrl={endpoint.BaseUrl}");
        var pipeline = BuildPipelineComponents(endpoint, apiKey, options);
        var provider = pipeline.Provider;
        // The session owns the provider and the live preview (it disposes them); only the cleanup
        // orchestrator is disposed here. In batch/DevThrottle mode cleanup and preview are null.
        using var cleanup = pipeline.Cleanup;
        var preview = pipeline.Preview;

        var bufferSpillDir = ResolveBufferSpillDir();
        using var audioBuffer = new AudioBuffer(spillDirectory: bufferSpillDir);
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

        // Issue #226: a provider connect failure here (provider unreachable, invalid key) is a
        // mid-recording-class failure - the client already has the mic live (capture-first). Send a
        // TYPED {type:error} naming the human cause BEFORE closing, so the Cockpit dialog surfaces
        // the real reason (not a bare close code) and offers Retry with the audio it captured.
        // DictationConnectException already carries a human-readable message; other failures are
        // prefixed so the dialog names the operation that failed.
        try
        {
            await session.StartAsync(profile, ct);
        }
        catch (Exception ex)
        {
            var cause = ex is DictationConnectException ? ex.Message : "could not start dictation: " + ex.Message;
            FileLog.Write($"[DictationEndpoint] sid={sessionId} StartAsync FAILED: {ex.Message}");
            await TrySendErrorAsync(ws, cause, ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.InternalServerError, "start failed");
            return;
        }

        await SendJsonAsync(ws, new { type = "started" }, ct);

        FileLog.Write($"[DictationEndpoint] session started: sid={sessionId} profile={profile} "
                      + $"vocab={dict.Vocabulary.Count} patterns={dict.CommonMistranscriptions.Count} "
                      + $"dict={dictPath} spillDir={bufferSpillDir}");

        var rxBuffer = new byte[16 * 1024];

        // Audio completeness gate (issue #586): desktop dictation NEVER transcribes
        // partial audio. The "recover whatever arrived and transcribe it"
        // truncation path (MinRecoverableAudioBytes / TryRecoverOrDiscard) has been
        // removed. Only an explicit 'stop' frame - which the client sends after the
        // whole utterance has been streamed - finalizes and transcribes. An early
        // or dropped connection (clean close without 'stop', or an abnormal
        // mid-stream drop) yields an explicit failure and discards the partial
        // audio; it can never produce a transcript of partial input.
        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(rxBuffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // A clean close handshake WITHOUT a 'stop' frame. The client
                    // did not signal that the whole utterance was sent, so the
                    // audio is partial by definition. Fail explicitly - never
                    // transcribe it.
                    FileLog.Write($"[DictationEndpoint] sid={sessionId} client closed before stop "
                                  + $"({audioBytesReceived} bytes captured); discarding partial audio, no transcript");
                    LogSessionRecord(sessionId, sessionStartUtc, profile, dict, recordingStart, 0, 0,
                        audioBytesReceived, transcript: null, options, remoteIp,
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
            // The connection ended WITHOUT a clean close handshake and WITHOUT a
            // 'stop' frame - a mobile browser backgrounded, the network blipped,
            // or the request was aborted. ReceiveAsync can interrupt mid-stream,
            // so fewer bytes arrived than the client sent: the audio is partial.
            // We do NOT recover-and-transcribe it (that was the removed truncation
            // path). Fail explicitly and discard.
            FileLog.Write($"[DictationEndpoint] sid={sessionId} connection dropped before stop "
                          + $"({ex.GetType().Name}: {ex.Message}); discarding partial audio, no transcript");
            LogSessionRecord(sessionId, sessionStartUtc, profile, dict, recordingStart, 0, 0,
                audioBytesReceived, transcript: null, options, remoteIp,
                clientError: "connection dropped before stop - partial audio discarded (not transcribed)");
            await TrySendErrorAsync(ws, "connection dropped before stop; partial audio was not transcribed - please re-record", ct);
            await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "dropped before stop");
            return;
        }

        recordingStart.Stop();
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        await SendJsonAsync(ws, new { type = "transcribing" }, ct);

        TranscriptResult? transcript = null;
        try
        {
            transcript = await session.StopAsync(ct);
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
        }

        LogSessionRecord(sessionId, sessionStartUtc, profile, dict, recordingStart,
            transcribedElapsedMs, stopElapsedMs, audioBytesReceived, transcript, options, remoteIp,
            clientError);

        await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "done");
    }

    /// <summary>
    /// The dictation components selected for a resolved routing target (issue #513). Pure data:
    /// the provider, an optional cleanup pass, and an optional live preview. The batch/DevThrottle
    /// shape is (batch provider, no cleanup, no preview); the realtime/BYO shape is (realtime
    /// provider, cleanup, preview).
    /// </summary>
    internal readonly record struct PipelineComponents(
        IDictationProvider Provider,
        CleanupOrchestrator? Cleanup,
        LivePreviewTranscriber? Preview);

    /// <summary>
    /// Select the dictation components from the routing TRANSPORT (issue #513), not the mode name -
    /// so the pipeline honors exactly the wire the provider offers and NEVER opens a transport the
    /// provider does not support. Pure (no I/O, no WebSocket); the connect happens later via the
    /// returned components, which is what makes this selection unit-testable.
    ///
    ///   - <see cref="TranscriptionTransport.Batch"/> (DevThrottle/Groq): the batch
    ///     <see cref="OpenAiTranscriptionProvider"/> against /audio/transcriptions, NO cleanup
    ///     (DevThrottle has no inference proxy; Whisper output is already clean), NO preview.
    ///     The OpenAI Realtime WebSocket is never constructed.
    ///   - <see cref="TranscriptionTransport.Realtime"/> (BYO/OpenAI): the
    ///     <see cref="OpenAiRealtimeProvider"/>, plus cleanup and the live preview.
    /// </summary>
    internal static PipelineComponents BuildPipelineComponents(
        ResolvedTranscription endpoint, string apiKey, AgentOptions options)
    {
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));

        if (endpoint.Transport == TranscriptionTransport.Batch)
        {
            var batchProvider = new OpenAiTranscriptionProvider(
                apiKey: apiKey,
                model: endpoint.Model,
                audioContentType: "audio/wav",
                audioFileName: "audio.wav",
                baseUrl: endpoint.BaseUrl);
            return new PipelineComponents(batchProvider, Cleanup: null, Preview: null);
        }

        var realtimeProvider = new OpenAiRealtimeProvider(apiKey: apiKey, model: endpoint.Model);
        var cleanup = new CleanupOrchestrator(
            apiKey: apiKey,
            model: options.DictationCleanupModel,
            baseUrl: endpoint.BaseUrl);
        var preview = new LivePreviewTranscriber(
            apiKey: apiKey,
            model: options.DictationPreviewModel,
            baseUrl: endpoint.BaseUrl);
        return new PipelineComponents(realtimeProvider, cleanup, preview);
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
