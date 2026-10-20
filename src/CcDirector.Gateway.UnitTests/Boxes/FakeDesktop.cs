using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace CcDirector.Gateway.UnitTests.Boxes;

/// <summary>
/// A box's desktop, in process: an in-memory host (no port) that answers the way the spike recorded Selkies
/// answering - a page at <c>/</c>, <c>api/status</c>, <c>api/upload</c>, file downloads, and one WebSocket at
/// <c>api/websockets</c> - plus a few routes that exist only to test the relay (an echo of the request headers,
/// redirects, a Set-Cookie).
///
/// The WebSocket echoes every message with its type, and obeys a few text commands: <c>big:N</c> sends one binary
/// message of N bytes, <c>close</c> closes from the box's side with 4000, <c>flood:N:S</c> sends N binary messages of
/// S bytes as fast as the relay takes them.
/// </summary>
internal sealed class FakeDesktop : IAsyncDisposable
{
    private WebApplication? _app;

    public TestServer Server => _app?.GetTestServer() ?? throw new InvalidOperationException("not started");

    /// <summary>Every request the box received, in order.</summary>
    public ConcurrentQueue<HttpRequestRecord> Requests { get; } = new();

    /// <summary>Completes when a browser-side close reaches the box: the status and reason it carried.</summary>
    public TaskCompletionSource<(int? Status, string? Reason)> SawClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the box's socket ended in any way (a close, or the relay aborting it).</summary>
    public TaskCompletionSource SocketEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The bytes the box's flood command managed to send.</summary>
    public long FloodBytesSent;

    /// <summary>Upload bodies received, by upload id.</summary>
    public ConcurrentDictionary<string, long> Uploads { get; } = new();

    public static byte Pattern(long i) => (byte)((i * 7 + 3) % 256);

    public static byte[] PatternBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = Pattern(i);
        return bytes;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleAsync);
        await app.StartAsync();
        _app = app;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        Requests.Enqueue(new HttpRequestRecord(ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Request.QueryString.Value ?? "",
            ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
        var path = ctx.Request.Path.Value;
        switch (path)
        {
            case "/api/websockets" when ctx.WebSockets.IsWebSocketRequest:
                await ServeSocketAsync(ctx);
                return;
            case "/":
                ctx.Response.ContentType = "text/html";
                await ctx.Response.WriteAsync("<html><body>fake desktop</body></html>");
                return;
            case "/api/status":
                await ctx.Response.WriteAsJsonAsync(new { mode = "websockets" });
                return;
            case "/headers":
                await ctx.Response.WriteAsJsonAsync(new
                {
                    path,
                    query = ctx.Request.QueryString.Value,
                    headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                });
                return;
            case "/api/upload":
                await ReceiveUploadAsync(ctx);
                return;
            case "/api/files/big":
                await SendBigFileAsync(ctx, long.Parse(ctx.Request.Query["bytes"].ToString()));
                return;
            case "/redirect-root":
                ctx.Response.StatusCode = StatusCodes.Status302Found;
                ctx.Response.Headers.Location = "/landing";
                return;
            case "/redirect-box":
                ctx.Response.StatusCode = StatusCodes.Status302Found;
                ctx.Response.Headers.Location = $"http://127.0.0.1:{DesktopRelayRig.BoxPort}/landing?x=1";
                return;
            case "/cookie":
                ctx.Response.Headers.Append("Set-Cookie", "cc-gateway-token=from-the-box; Path=/");
                ctx.Response.Headers.Append("X-Box-Header", "kept");
                await ctx.Response.WriteAsync("cookie page");
                return;
            default:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private async Task ReceiveUploadAsync(HttpContext ctx)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        var mismatch = -1L;
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < read && mismatch < 0; i++)
                if (buffer[i] != Pattern(total + i)) mismatch = total + i;
            total += read;
        }
        var id = ctx.Request.Headers["X-Upload-Id"].ToString();
        Uploads[id] = total;
        await ctx.Response.WriteAsJsonAsync(new
        {
            received = total,
            firstMismatch = mismatch,
            uploadId = id,
            offset = ctx.Request.Headers["X-Upload-Offset"].ToString(),
            total = ctx.Request.Headers["X-Upload-Total"].ToString(),
            final = ctx.Request.Headers["X-Upload-Final"].ToString(),
            contentLength = ctx.Request.ContentLength,
        });
    }

    private static async Task SendBigFileAsync(HttpContext ctx, long bytes)
    {
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = bytes;
        var chunk = new byte[64 * 1024];
        for (long sent = 0; sent < bytes;)
        {
            var n = (int)Math.Min(chunk.Length, bytes - sent);
            for (var i = 0; i < n; i++) chunk[i] = Pattern(sent + i);
            await ctx.Response.Body.WriteAsync(chunk.AsMemory(0, n));
            sent += n;
        }
    }

    private async Task ServeSocketAsync(HttpContext ctx)
    {
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        try
        {
            while (true)
            {
                var (type, data, closeStatus, closeReason) = await ReceiveMessageAsync(socket);
                if (type == WebSocketMessageType.Close)
                {
                    SawClose.TrySetResult((closeStatus, closeReason));
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                var text = type == WebSocketMessageType.Text ? Encoding.UTF8.GetString(data) : null;
                if (text is not null && text.StartsWith("big:", StringComparison.Ordinal))
                {
                    await socket.SendAsync(PatternBytes(int.Parse(text[4..])), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                else if (text == "close")
                {
                    await socket.CloseOutputAsync((WebSocketCloseStatus)4000, "box says bye", CancellationToken.None);
                }
                else if (text is not null && text.StartsWith("flood:", StringComparison.Ordinal))
                {
                    var parts = text.Split(':');
                    var payload = PatternBytes(int.Parse(parts[2]));
                    for (var i = 0; i < int.Parse(parts[1]); i++)
                    {
                        await socket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
                        Interlocked.Add(ref FloodBytesSent, payload.Length);
                    }
                }
                else
                {
                    await socket.SendAsync(data, type, true, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The relay aborted the box's socket; the box simply goes away.
        }
        finally
        {
            SocketEnded.TrySetResult();
        }
    }

    /// <summary>Receive one whole message (or the close) from a socket.</summary>
    public static async Task<(WebSocketMessageType Type, byte[] Data, int? CloseStatus, string? CloseReason)> ReceiveMessageAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, Array.Empty<byte>(), (int?)result.CloseStatus, result.CloseStatusDescription);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, message.ToArray(), null, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

internal sealed record HttpRequestRecord(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers);

/// <summary>Reads the JSON a fake desktop route answered.</summary>
internal static class RelayJson
{
    public static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
