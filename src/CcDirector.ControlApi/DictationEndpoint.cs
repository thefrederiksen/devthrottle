using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the <c>/dictate</c> WebSocket endpoint that exposes the dictation
/// library to browser-based UIs. Same Director process; same dictionary
/// file; same cleanup pass as the desktop consumers.
///
/// Wire protocol (all JSON text frames except where noted):
///
///   Server -> {"type":"ready"}
///   Client -> {"type":"start","profile":"default","contentType":"audio/webm","fileName":"audio.webm"}
///   Server -> {"type":"started"}
///   Client -> &lt;binary frames&gt;   (one or more, in order; opaque audio bytes the provider will decode)
///   Client -> {"type":"stop"}
///   Server -> {"type":"transcribing"}
///   Server -> {"type":"partial","text":"..."}    (zero or more, batch provider sends one)
///   Server -> {"type":"final","raw":"...","cleaned":"...","cleanupApplied":true,"profile":"default","reason":null}
///   (Server closes the socket.)
///
///   On error: Server -> {"type":"error","message":"..."}  then close.
///
/// Localhost-only by default. The existing <see cref="ControlApiHost"/>
/// auth middleware applies if enabled.
/// </summary>
internal static class DictationEndpoint
{
    public static void Map(IEndpointRouteBuilder app, AgentOptions options)
    {
        app.MapGet("/dictate.html", () =>
        {
            var html = EmbeddedResources.Load("dictate.html");
            return Results.Content(html, "text/html; charset=utf-8");
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
            try
            {
                await ServeSessionAsync(ws, options, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DictationEndpoint] session FAILED: {ex.Message}");
                await TrySendErrorAsync(ws, ex.Message, ctx.RequestAborted);
                await TryCloseAsync(ws, WebSocketCloseStatus.InternalServerError, "internal error");
            }
        });
    }

    private static async Task ServeSessionAsync(WebSocket ws, AgentOptions options, CancellationToken ct)
    {
        await SendJsonAsync(ws, new { type = "ready" }, ct);

        // Wait for the client's "start" frame before allocating any of the
        // dictation pipeline.  Lets us stay cheap while the connection is idle.
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
        var contentType = TryReadString(startMsg, "contentType", out var ctType) && !string.IsNullOrWhiteSpace(ctType) ? ctType : "audio/webm";
        var fileName = TryReadString(startMsg, "fileName", out var fn) && !string.IsNullOrWhiteSpace(fn) ? fn : "audio.webm";

        // Build the dictation pipeline for this connection. Fresh DictionaryLoader
        // means we pick up the latest YAML on disk every time the client (re)starts.
        var dictPath = options.ResolveDictationDictionaryPath();
        using var dictionary = new DictionaryLoader(dictPath, watch: false);
        await using var provider = new OpenAiTranscriptionProvider(
            apiKey: options.ResolveOpenAiKey(),
            audioContentType: contentType,
            audioFileName: fileName);
        var cleanup = new CleanupOrchestrator(options.ClaudePath);

        await using var session = new DictationSession(dictionary, provider, cleanup);

        session.OnPartial += partial =>
        {
            // Fire-and-forget; the cancellation token guards against shutdown.
            _ = SendJsonAsync(ws, new { type = "partial", text = partial }, ct);
        };

        await session.StartAsync(profile, ct);
        await SendJsonAsync(ws, new { type = "started" }, ct);

        FileLog.Write($"[DictationEndpoint] session started: profile={profile}, contentType={contentType}, dict={dictPath}");

        // Receive loop: binary frames are audio; text frames are control messages.
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                FileLog.Write("[DictationEndpoint] client closed before stop frame");
                return;
            }
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var audio = await ReadFullMessageAsync(ws, buffer, result, ct);
                if (audio.Length > 0)
                    await session.PushAudioAsync(audio, ct);
                continue;
            }
            // Text frame: a control message.
            var textMsg = await ReadFullTextAsync(ws, buffer, result, ct);
            using var doc = TryParseJson(textMsg);
            if (doc is null) continue;
            if (TryReadString(doc.RootElement, "type", out var t))
            {
                if (t == "stop") break;
                if (t == "abort")
                {
                    FileLog.Write("[DictationEndpoint] client aborted");
                    await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "aborted");
                    return;
                }
            }
        }

        await SendJsonAsync(ws, new { type = "transcribing" }, ct);

        var transcript = await session.StopAsync(ct);

        await SendJsonAsync(ws, new
        {
            type = "final",
            raw = transcript.RawTranscript,
            cleaned = transcript.CleanedTranscript,
            cleanupApplied = transcript.CleanupApplied,
            profile = transcript.ProfileUsed,
            reason = transcript.CleanupFailureReason,
        }, ct);

        FileLog.Write($"[DictationEndpoint] session done: raw_len={transcript.RawTranscript.Length}, cleaned_len={transcript.CleanedTranscript.Length}, applied={transcript.CleanupApplied}");

        await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "done");
    }

    // ===== helpers ===========================================================

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
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

    private static async Task<byte[]> ReadFullMessageAsync(WebSocket ws, byte[] firstBuffer, WebSocketReceiveResult firstResult, CancellationToken ct)
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
        var bytes = await ReadFullMessageAsync(ws, firstBuffer, firstResult, ct);
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
