using System.Net.WebSockets;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps <c>GET /sessions/{sid}/stream</c>: a WebSocket that streams a session's
/// raw PTY bytes to a browser-side xterm.js terminal.
///
/// This replaces the old "poll the server-rendered HTML grid" Raw view, which
/// snapshotted Claude Code's constantly-repainting TUI on a timer and stacked
/// half-drawn frames as ghost lines. Streaming the byte stream in order to a real
/// terminal emulator (xterm.js) means the client applies every cursor move and
/// repaint in sequence, so the screen is always coherent -- the same way the
/// desktop terminal control renders.
///
/// Wire protocol:
///   Server -> {"type":"size","cols":C,"rows":R}   (immediately, and again on every PTY resize)
///   Server -> &lt;binary frames&gt;                  (raw PTY bytes: full history first, then live)
///   Server -> {"type":"closed","reason":"..."}     (session ended) then the socket closes
///
/// The client sends nothing meaningful -- keyboard input still flows through the
/// existing POST /sessions/{sid}/prompt path. A close frame from the client ends
/// the stream. Localhost-only by default; the ControlApiHost auth middleware
/// applies when enabled, exactly like /dictate.
/// </summary>
internal static class TerminalStreamEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager)
    {
        // Vendored xterm.js assets (offline; no CDN -- the phone reaches the Director
        // over Tailscale and may have no path to a public CDN).
        app.MapGet("/xterm.js", () =>
            Results.Content(EmbeddedResources.Load("xterm.js"), "application/javascript; charset=utf-8"));
        app.MapGet("/xterm.css", () =>
            Results.Content(EmbeddedResources.Load("xterm.css"), "text/css; charset=utf-8"));
        app.MapGet("/xterm-addon-canvas.js", () =>
            Results.Content(EmbeddedResources.Load("xterm-addon-canvas.js"), "application/javascript; charset=utf-8"));

        app.MapGet("/sessions/{sid}/stream", async (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[TerminalStreamEndpoint] GET /sessions/{sid}/stream from {ctx.Connection.RemoteIpAddress}");

            if (!Guid.TryParse(sid, out var guid))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("invalid session id format");
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("expected websocket upgrade");
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            try
            {
                await StreamSessionAsync(ws, sessionManager, guid, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client navigated away or the server is shutting down. Normal.
            }
            catch (WebSocketException ex)
            {
                FileLog.Write($"[TerminalStreamEndpoint] sid={guid} socket dropped: {ex.Message}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TerminalStreamEndpoint] stream FAILED: sid={guid} {ex.Message}");
            }
        });
    }

    private static async Task StreamSessionAsync(WebSocket ws, SessionManager sessionManager, Guid guid, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = cts.Token;

        // We don't need anything the client says, but we must drain its frames to
        // observe the close handshake and notice a dropped connection. Any close or
        // receive error cancels the send pump below.
        var receiveTask = DrainClientAsync(ws, cts);

        // GetWrittenSince(0) returns the full retained history on the first call, then
        // only the bytes appended since the previous call. One monotonic cursor drives
        // both the initial replay and the live tail -- never a snapshot, never a frame
        // boundary, so xterm renders a coherent screen.
        long cursor = 0;
        short lastCols = -1;
        short lastRows = -1;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var session = sessionManager.GetSession(guid);
            if (session is null)
            {
                await SendJsonAsync(ws, new { type = "closed", reason = "session not found" }, ct);
                break;
            }

            // Report the current PTY size up front and whenever the desktop pane resizes
            // it, so xterm renders the grid at the true width instead of guessing.
            if (session.CurrentCols != lastCols || session.CurrentRows != lastRows)
            {
                lastCols = session.CurrentCols;
                lastRows = session.CurrentRows;
                await SendJsonAsync(ws, new { type = "size", cols = (int)lastCols, rows = (int)lastRows }, ct);
            }

            var buffer = session.Buffer;
            if (buffer is not null)
            {
                var (data, newCursor) = buffer.GetWrittenSince(cursor);
                if (data.Length > 0)
                {
                    await ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct);
                    cursor = newCursor;
                    continue; // drain at full speed while bytes are flowing before sleeping
                }
            }

            // Once a dead session's buffer is fully drained (no more new bytes above),
            // tell the client and end the stream.
            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            {
                await SendJsonAsync(ws, new { type = "closed", reason = "session exited" }, ct);
                break;
            }

            await Task.Delay(40, ct);
        }

        cts.Cancel();
        await TryCloseAsync(ws);
        try { await receiveTask; } catch { /* receive loop already unwound */ }
    }

    private static async Task DrainClientAsync(WebSocket ws, CancellationTokenSource cts)
    {
        var buffer = new byte[1024];
        try
        {
            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // Receive faulting (client dropped, socket aborted) is the expected end-of-life
            // signal for this loop, not an error worth surfacing.
        }
        finally
        {
            cts.Cancel();
        }
    }

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
            FileLog.Write($"[TerminalStreamEndpoint] SendJsonAsync failed: {ex.Message}");
        }
    }

    private static async Task TryCloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch
        {
            // Best effort -- the socket may already be gone.
        }
    }
}
