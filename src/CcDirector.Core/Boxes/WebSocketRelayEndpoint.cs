using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Boxes;

/// <summary>
/// A WebSocket as one end of a relay stream: the browser's socket on the Gateway, or the socket the Director opened
/// to the box. Both ends are the same shape, so one class serves both.
///
/// <para><b>Reading</b> splits each message into Data frames of at most <see cref="DesktopRelayLimits.MaxFrameBytes"/>,
/// keeping its type (text or binary) and marking the last piece. <b>Delivering</b> puts the pieces back together
/// and sends the whole message in one send, so the far side receives exactly the message that was sent, byte for
/// byte, however the tunnel cut it. A message larger than <see cref="DesktopRelayLimits.MaxWebSocketMessageBytes"/>
/// ends the stream with 1009 rather than growing without bound.</para>
///
/// <para>Sends are serialised (the pump delivers and may close at the same moment); the receive runs on its own,
/// which a WebSocket allows. Closing uses the send-only close, so it never fights the pending receive.</para>
/// </summary>
public sealed class WebSocketRelayEndpoint : IDesktopRelayEndpoint, IDisposable
{
    private readonly WebSocket _socket;
    private readonly string _label;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ArrayBufferWriter<byte> _message = new();
    private bool _messageIsText;
    private int _closed;

    /// <param name="socket">The open socket.</param>
    /// <param name="label">Who this end is, for the log: "browser" or "box", with the box and stream ids.</param>
    public WebSocketRelayEndpoint(WebSocket socket, string label)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _label = label;
    }

    public async IAsyncEnumerable<DesktopRelayFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[DesktopRelayLimits.MaxFrameBytes];
        while (true)
        {
            var (result, failure) = await ReceiveOnceAsync(buffer, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                FileLog.Write($"[WebSocketRelayEndpoint] {_label}: the socket dropped: {failure}");
                yield return new DesktopRelayFrame
                {
                    Kind = DesktopRelayFrameKind.Closed,
                    CloseStatus = DesktopRelayEndpointClose.GoingAway,
                    Reason = $"the {_label} connection dropped",
                };
                yield break;
            }
            if (result!.MessageType == WebSocketMessageType.Close)
            {
                var status = (int?)_socket.CloseStatus ?? DesktopRelayEndpointClose.Normal;
                FileLog.Write($"[WebSocketRelayEndpoint] {_label}: closed by its own side, status={status}, reason={_socket.CloseStatusDescription}");
                yield return new DesktopRelayFrame
                {
                    Kind = DesktopRelayFrameKind.Closed,
                    CloseStatus = status,
                    Reason = string.IsNullOrEmpty(_socket.CloseStatusDescription) ? $"the {_label} closed the connection" : _socket.CloseStatusDescription,
                };
                yield break;
            }
            yield return new DesktopRelayFrame
            {
                Kind = DesktopRelayFrameKind.Data,
                Data = buffer.AsSpan(0, result.Count).ToArray(),
                IsText = result.MessageType == WebSocketMessageType.Text,
                EndOfMessage = result.EndOfMessage,
            };
        }
    }

    // A receive that fails (the peer vanished without a close) is the expected end of a dropped socket, not a
    // fault of the relay; it is reported as a value so the iterator above can turn it into a Closed frame.
    private async Task<(WebSocketReceiveResult? Result, string? Failure)> ReceiveOnceAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            return (await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    public async Task DeliverAsync(DesktopRelayFrame frame, CancellationToken ct)
    {
        if (frame.Kind != DesktopRelayFrameKind.Data)
            throw new InvalidOperationException($"a WebSocket end takes only Data frames, not {frame.Kind}");

        var data = frame.Data ?? Array.Empty<byte>();
        if (_message.WrittenCount == 0)
            _messageIsText = frame.IsText;
        else if (_messageIsText != frame.IsText)
            throw new InvalidOperationException($"{_label}: a message changed type between its pieces");
        if ((long)_message.WrittenCount + data.Length > DesktopRelayLimits.MaxWebSocketMessageBytes)
            throw new DesktopRelayMessageTooBigException(
                $"a message to the {_label} is over {DesktopRelayLimits.MaxWebSocketMessageBytes} bytes");
        _message.Write(data);
        if (!frame.EndOfMessage) return;

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(_message.WrittenMemory,
                _messageIsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _message.Clear();
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(int closeStatus, string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        var status = DesktopRelayEndpointClose.Sendable(closeStatus);
        var text = DesktopRelayEndpointClose.Trim(reason);
        FileLog.Write($"[WebSocketRelayEndpoint] {_label}: closing, status={status}, reason={text}");
        // A send still in flight (a slow browser, mid-message) would hold the close behind it for as long as it
        // takes. The stream is over, so the socket is aborted instead of waiting on it.
        if (!await _sendLock.WaitAsync(0).ConfigureAwait(false))
        {
            FileLog.Write($"[WebSocketRelayEndpoint] {_label}: a send is still in flight, so the socket is aborted instead of closed");
            _socket.Abort();
            return;
        }
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync((WebSocketCloseStatus)status, text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The socket is already gone - the close it would have sent has nobody to reach.
            FileLog.Write($"[WebSocketRelayEndpoint] {_label}: close not sent, the socket is gone: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Dispose the socket. Safe after a close, and safe twice.</summary>
    public void Dispose()
    {
        _socket.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>A WebSocket message larger than <see cref="DesktopRelayLimits.MaxWebSocketMessageBytes"/>; the stream ends with 1009.</summary>
public sealed class DesktopRelayMessageTooBigException : Exception
{
    public DesktopRelayMessageTooBigException(string message) : base(message) { }
}
