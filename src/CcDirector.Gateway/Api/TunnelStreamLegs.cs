using System.Net.WebSockets;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission, Phase 2: serve the browser-facing UP-STREAM legs (live terminal, session file
/// read, screenshot bytes) over the tunnel instead of dialing the owning Director over HTTP. Used ONLY when
/// stream mode is on AND the session's owning Director has an active tunnel connection (the caller resolves
/// that via <see cref="PushedSessionStore.TryLocate"/>); otherwise the caller keeps the existing HTTP proxy
/// path, byte-identical. So this is purely additive behind the stream-mode kill switch.
///
/// Each leg mints a fresh stream id, registers a browser-facing <see cref="IStreamSink"/> in the
/// <see cref="GatewayStreamRegistry"/>, sends the matching open command (open-terminal-stream / read-file /
/// screenshot-file) DOWN the tunnel, and lets the Director's up-frames flow into the sink with the registry's
/// pull-then-forward backpressure. A browser disconnect (or a natural end) sends close-stream - the
/// load-bearing stop signal (Architect ruling 3) - and tears the sink down. The browser wire contract is
/// unchanged; only the Gateway's Director-facing leg moves from an HTTP dial to the tunnel.
/// </summary>
internal sealed class TunnelStreamLegs
{
    private readonly GatewayStreamRegistry _registry;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _sendCommand;

    public TunnelStreamLegs(GatewayStreamRegistry registry, DirectorCommandRouter.SendDirectorCommandAsync sendCommand)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
    }

    // ---------------------------------------------------------------- Terminal (open-ended) ----

    /// <summary>
    /// Serve <c>GET /sessions/{sid}/stream</c> over the tunnel: accept the browser WebSocket, open a terminal
    /// up-stream on the owning Director, translate the up-frames back into the browser terminal protocol, and
    /// forward the browser's keystrokes DOWN as terminal-input unary verbs. There is no HTTP fallback after the
    /// upgrade is accepted (the caller already committed to the tunnel because the owner is stream-connected);
    /// an open failure closes the socket with a reason and the browser reconnects.
    /// </summary>
    public async Task ServeTerminalAsync(HttpContext ctx, string sid, TenantId tenant, string directorId)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("expected websocket upgrade");
            return;
        }

        // permessage-deflate matches the old dialed terminal leg (ANSI compresses 5-10x on the tailnet).
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync(
            new WebSocketAcceptContext { DangerousEnableCompression = true });

        var streamId = Guid.NewGuid().ToString("N");
        var sink = new WebSocketStreamSink(ws);
        // Issue #1923: record WHO owns this stream - the tenant that located the session and the Director the
        // open command below is sent to. Only that identity may stream frames back up under this id.
        var teardown = _registry.Register(streamId, new StreamOwner(tenant, directorId), sink);

        // The browser is "gone" when the request aborts OR the keystroke receive loop ends (a close frame or a
        // socket fault). Either tears the stream down and sends close-stream.
        using var browserGone = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        // WHO IS TYPING (source logging, 2026-09-05): the credential kind this request's gate verified, read
        // here in request scope - the socket outlives the HttpContext, and one browser holds one identity for
        // the life of its terminal, so it is stamped once and carried on every frame.
        var typist = new SubmissionProvenanceDto
        {
            Route = Core.Sessions.SubmissionRoutes.GatewayTerminal,
            IdentityKind = Util.AuthMiddleware.IdentityKind(ctx),
        };
        var keystrokes = PumpKeystrokesAsync(ws, sid, directorId, typist, browserGone);

        FileLog.Write($"[TunnelStreamLegs] terminal open sid={sid} director={directorId} stream={streamId}");
        DirectorCommandResult? open;
        try
        {
            open = await DirectorCommandRouter.TrySendAsync(
                _sendCommand, directorId, "open-terminal-stream", sid,
                new OpenStreamRequest { StreamId = streamId }, browserGone.Token);
        }
        catch (OperationCanceledException)
        {
            _registry.Close(streamId);
            await Swallow(keystrokes);
            return;
        }

        if (open is null || !open.Ok)
        {
            var reason = open is null ? "owning director stream unavailable"
                       : open.Status == DirectorCommandStatus.NotFound ? "session not found"
                       : $"director returned {open.Status}";
            FileLog.Write($"[TunnelStreamLegs] terminal open FAILED sid={sid} stream={streamId}: {reason}");
            await SafeSendClosedAsync(ws, reason, browserGone.Token);
            _registry.Close(streamId);
            browserGone.Cancel();
            await Swallow(keystrokes);
            return;
        }

        // Ok: the up-frames now flow StreamUp -> registry -> sink -> browser WS. Wait until the stream is torn
        // down: a Closed frame completed it, the browser left, or the open-timeout fired.
        await WaitForCancelAsync(teardown, browserGone.Token);

        _registry.Close(streamId);                                  // idempotent; completes the sink
        await SendCloseStreamAsync(directorId, streamId);           // stop the Director producer (ruling 3)
        browserGone.Cancel();
        await Swallow(keystrokes);
        FileLog.Write($"[TunnelStreamLegs] terminal closed sid={sid} stream={streamId}");
    }

    private async Task PumpKeystrokesAsync(WebSocket ws, string sid, string directorId, SubmissionProvenanceDto typist, CancellationTokenSource browserGone)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        try
        {
            while (!browserGone.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, browserGone.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0) message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (message.Length == 0) continue;

                var bytes = message.ToArray();
                message.SetLength(0);
                var payload = new TerminalInputRequest { Bytes = Convert.ToBase64String(bytes), Provenance = typist };
                try
                {
                    await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, "terminal-input", sid, payload, browserGone.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { FileLog.Write($"[TunnelStreamLegs] terminal-input send failed sid={sid}: {ex.Message}"); }
            }
        }
        catch
        {
            // A receive fault (browser dropped, socket aborted) is the expected end-of-life for this pump.
        }
        finally
        {
            browserGone.Cancel();
        }
    }

    // ------------------------------------------------------------- File / screenshot (finite) ----

    /// <summary>
    /// Serve <c>GET /sessions/{sid}/file?path=...</c> over the tunnel (read-file). Returns true when handled
    /// (streamed, or answered 404/400); false ONLY when nothing was written and the caller should fall back to
    /// the HTTP proxy path (the owning Director's stream was lost between resolution and open).
    /// </summary>
    public Task<bool> TryServeFileAsync(HttpContext ctx, string sid, TenantId tenant, string directorId, string? path) =>
        TryServeReadAsync(ctx, sid, tenant, directorId, "read-file", new OpenStreamRequest { StreamId = "", Path = path }, "file");

    /// <summary>
    /// Serve <c>GET /sessions/{sid}/screenshots/file?name=...</c> over the tunnel (screenshot-file). Same
    /// contract as <see cref="TryServeFileAsync"/>.
    /// </summary>
    public Task<bool> TryServeScreenshotAsync(HttpContext ctx, string sid, TenantId tenant, string directorId, string? name) =>
        TryServeReadAsync(ctx, sid, tenant, directorId, "screenshot-file", new OpenStreamRequest { StreamId = "", ScreenshotId = name }, "screenshot");

    private async Task<bool> TryServeReadAsync(HttpContext ctx, string sid, TenantId tenant, string directorId, string verb, OpenStreamRequest req, string label)
    {
        var streamId = Guid.NewGuid().ToString("N");
        req.StreamId = streamId;
        var sink = new HttpResponseStreamSink(ctx.Response);
        // Issue #1923: same ownership record as the terminal leg - the finite reads are the same primitive and
        // are injectable in exactly the same way (a screenshot or file body written into another account's
        // response), so they carry the same owner.
        var teardown = _registry.Register(streamId, new StreamOwner(tenant, directorId), sink);

        FileLog.Write($"[TunnelStreamLegs] {label} open sid={sid} director={directorId} stream={streamId}");
        DirectorCommandResult? open;
        try
        {
            open = await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, verb, sid, req, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            sink.Fail();
            _registry.Close(streamId);
            return true; // the browser left mid-open; there is nothing to fall back to
        }

        if (open is null)
        {
            // The owning Director's stream was lost between resolution and open; nothing has been written, so
            // let the caller fall back to the coexisting HTTP path (this is explicit routing, not a silent
            // degrade - the HTTP dial is the same-behaviour path that is live when stream mode is off).
            sink.Fail();
            _registry.Close(streamId);
            FileLog.Write($"[TunnelStreamLegs] {label} sid={sid} stream={streamId}: no stream at open -> HTTP fallback");
            return false;
        }

        if (!open.Ok)
        {
            sink.Fail();
            _registry.Close(streamId);
            var code = open.Status switch
            {
                DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
                DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
                // Issue #4078: the Director answers a FOLDER asked for as a file with Conflict.
                DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status502BadGateway,
            };
            FileLog.Write($"[TunnelStreamLegs] {label} open FAILED sid={sid} stream={streamId}: {open.Status} {open.Error}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = code;
                // Issue #4078: a refused file read carries the Director's decision - its sentence AND its code
                // (not on disk, not allowed, a folder) - so the viewer shows the sentence and, for a folder,
                // opens the listing. The sentence is the error, verbatim; the Gateway writes none of its own.
                var refusal = ReadRefusal(open);
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = open.Error ?? $"director returned {open.Status}",
                    code = refusal?.Code,
                });
            }
            return true;
        }

        // Ok: set Content-Type/Content-Length from the open reply (this OPENS the sink's write gate), then let
        // the up-frames stream into the response body. Wait until eof/teardown, then stop the producer.
        sink.ApplyMetadata(DirectorCommandRouter.ReadBody<OpenReadResponse>(open));
        await WaitForCancelAsync(teardown, ctx.RequestAborted);
        _registry.Close(streamId);
        await SendCloseStreamAsync(directorId, streamId);
        FileLog.Write($"[TunnelStreamLegs] {label} closed sid={sid} stream={streamId}");
        return true;
    }

    // ------------------------------------------------------------------------------- helpers ----

    // The decision a Director attaches to a refused file read (issue #4078), or null when it sent none - an older
    // Director, or a refusal that is not about the path (a bad session id).
    private static PathStatusDto? ReadRefusal(DirectorCommandResult result)
    {
        if (string.IsNullOrEmpty(result.BodyJson))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PathStatusDto>(result.BodyJson, RefusalJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            FileLog.Write($"[TunnelStreamLegs] refusal body could not be read: {ex.Message}");
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions RefusalJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private async Task SendCloseStreamAsync(string directorId, string streamId)
    {
        try
        {
            await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, "close-stream", "", new CloseStreamRequest { StreamId = streamId }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TunnelStreamLegs] close-stream send failed stream={streamId}: {ex.Message}");
        }
    }

    private static async Task SafeSendClosedAsync(WebSocket ws, string reason, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        try
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "closed", reason });
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TunnelStreamLegs] send closed-frame failed: {ex.Message}");
        }
    }

    // Complete when EITHER token fires (the stream torn down, or the browser gone). No polling.
    private static async Task WaitForCancelAsync(CancellationToken a, CancellationToken b)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(a, b);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = linked.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), tcs);
        await tcs.Task;
    }

    private static async Task Swallow(Task task)
    {
        try { await task; } catch { /* the pump's own end-of-life; already logged where it matters */ }
    }
}
