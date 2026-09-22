using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CcDirector.Gateway.Traffic;

/// <summary>
/// Hands out WebSockets that count their payload bytes. Installed on every WebSocket request, so the terminal
/// stream (<c>/sessions/{sid}/stream</c>) and the SignalR hubs' WebSocket transport are both counted, each under
/// its own route template, account and client kind. It changes nothing about when a frame is sent or received.
/// </summary>
internal sealed class CountingWebSocketFeature : IHttpWebSocketFeature
{
    private readonly IHttpWebSocketFeature _inner;
    private readonly TrafficMeter _meter;
    private readonly string _name;
    private readonly string _client;
    private readonly string _account;

    public CountingWebSocketFeature(IHttpWebSocketFeature inner, TrafficMeter meter, string name, string client, string account)
    {
        _inner = inner;
        _meter = meter;
        _name = name;
        _client = client;
        _account = account;
    }

    public bool IsWebSocketRequest => _inner.IsWebSocketRequest;

    public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
    {
        var socket = await _inner.AcceptAsync(context);
        return new CountingWebSocket(socket, _meter, _name, _client, _account);
    }
}

/// <summary>
/// A WebSocket that adds up the payload bytes of each frame it sends and receives (frame headers are not
/// counted), and counts a message each time one ends. Every call is the inner socket's own.
/// </summary>
internal sealed class CountingWebSocket : WebSocket
{
    private readonly WebSocket _inner;
    private readonly TrafficMeter _meter;
    private readonly string _name;
    private readonly string _client;
    private readonly string _account;

    public CountingWebSocket(WebSocket inner, TrafficMeter meter, string name, string client, string account)
    {
        _inner = inner;
        _meter = meter;
        _name = name;
        _client = client;
        _account = account;
    }

    public override WebSocketCloseStatus? CloseStatus => _inner.CloseStatus;
    public override string? CloseStatusDescription => _inner.CloseStatusDescription;
    public override WebSocketState State => _inner.State;
    public override string? SubProtocol => _inner.SubProtocol;

    public override void Abort() => _inner.Abort();

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => _inner.Dispose();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await _inner.ReceiveAsync(buffer, cancellationToken);
        Received(result.Count, result.EndOfMessage, result.MessageType);
        return result;
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await _inner.ReceiveAsync(buffer, cancellationToken);
        Received(result.Count, result.EndOfMessage, result.MessageType);
        return result;
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        await _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        Sent(buffer.Count, endOfMessage);
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        await _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        Sent(buffer.Length, endOfMessage);
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
    {
        await _inner.SendAsync(buffer, messageType, messageFlags, cancellationToken);
        Sent(buffer.Length, (messageFlags & WebSocketMessageFlags.EndOfMessage) != 0);
    }

    private void Sent(int bytes, bool endOfMessage)
    {
        var cell = _meter.Cell(TrafficMeter.WebSocket, _name, _client, _account);
        if (endOfMessage) cell.AddMessage(bytes, 0);
        else cell.AddBytes(bytes, 0);
    }

    private void Received(int bytes, bool endOfMessage, WebSocketMessageType type)
    {
        if (type == WebSocketMessageType.Close) return;
        var cell = _meter.Cell(TrafficMeter.WebSocket, _name, _client, _account);
        if (endOfMessage) cell.AddMessage(0, bytes);
        else cell.AddBytes(0, bytes);
    }
}
