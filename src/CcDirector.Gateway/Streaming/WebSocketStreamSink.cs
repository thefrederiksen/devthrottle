using System.Net.WebSockets;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Gateway Cleanup mission, Phase 2: the browser-facing sink for a LIVE terminal up-stream. Wraps the
/// accepted browser WebSocket and translates each Director up-frame back into the EXACT terminal wire
/// protocol the browser has always spoken (the protocol <c>TerminalStreamEndpoint</c> defines), so the
/// browser contract is unchanged - only the Gateway's source of the frames moved from a dialed Director
/// WebSocket to the tunnel up-stream:
///   Size   -> a text frame {"type":"size","cols":C,"rows":R}
///   Binary -> a binary WebSocket message of the raw bytes
///   Closed -> a text frame {"type":"closed","reason":R}; the socket is then closed by <see cref="CompleteAsync"/>.
///
/// The backpressure invariant (Architect ruling 1) lives in <see cref="GatewayStreamRegistry"/>, which awaits
/// each <see cref="WriteFrameAsync"/> before pulling the next up-frame; this sink therefore just sends the one
/// frame and returns when that send has drained - it never buffers. It only ever WRITES to the socket; the
/// browser's keystrokes are read by a separate receive pump (WebSockets are full-duplex, so one sender and one
/// receiver do not conflict), and <see cref="CompleteAsync"/> uses <c>CloseOutputAsync</c> (send-only) so it
/// never contends with that pending receive.
/// </summary>
public sealed class WebSocketStreamSink : IStreamSink
{
    private readonly WebSocket _socket;

    /// <summary>One send at a time on the socket: a WebSocket allows no concurrent sends, and since issue #4380 two
    /// writers share it - the Director's terminal frames and the keystroke pump's refused frames.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WebSocketStreamSink(WebSocket socket) => _socket = socket ?? throw new ArgumentNullException(nameof(socket));

    public async Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
            throw new InvalidOperationException("terminal WebSocket is no longer open");

        switch (frame.Kind)
        {
            case DirectorStreamFrameType.Size:
                await SendTextAsync(new { type = "size", cols = frame.Cols, rows = frame.Rows }, cancellationToken);
                break;
            case DirectorStreamFrameType.Binary:
                var data = frame.Data ?? Array.Empty<byte>();
                if (data.Length > 0)
                    await SendAsync(data, WebSocketMessageType.Binary, cancellationToken);
                break;
            case DirectorStreamFrameType.Closed:
                await SendTextAsync(new { type = "closed", reason = frame.Reason ?? "" }, cancellationToken);
                break;
        }
    }

    public async Task CompleteAsync(string? reason)
    {
        try
        {
            // CloseOutputAsync (send-only) so this never contends with the keystroke receive pump that may be
            // mid ReceiveAsync; a plain CloseAsync would also try to RECEIVE the peer's close ack and conflict.
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, CloseReason(reason), CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WebSocketStreamSink] close failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tell the browser that keystrokes it sent did NOT reach the terminal, and why (issue #4380): a <c>refused</c> text
    /// frame, <c>{"type":"refused","reason":"..."}</c>. Until then a keystroke the Director dropped - Claude Code's
    /// usage-limit dialog showing - got no answer at all on this socket.
    /// </summary>
    public Task WriteRefusedAsync(string reason, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
            throw new InvalidOperationException("terminal WebSocket is no longer open");
        return SendTextAsync(new { type = "refused", reason }, cancellationToken);
    }

    private Task SendTextAsync(object payload, CancellationToken ct) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, ct);

    private async Task SendAsync(byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, type, endOfMessage: true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // A WebSocket close reason is capped at 123 UTF-8 bytes by the protocol; keep it short and never null.
    private static string CloseReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "done";
        return reason.Length <= 120 ? reason : reason[..120];
    }
}
