using System.Collections.Concurrent;
using System.Threading.Channels;
using CcDirector.Core.Boxes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Boxes;

/// <summary>
/// The Director's half of the desktop relay leg (Factory Boxes, issue #4258). It answers the Gateway's
/// <see cref="DesktopRelayProtocol.OpenVerb"/> command: it resolves the box id through the Director's own
/// hosted-desktop list - and REFUSES, and logs, a box id that list does not hold, whatever the Gateway believed -
/// opens the box's local WebSocket or starts its HTTP request on 127.0.0.1, and then calls the hub's
/// <see cref="DesktopRelayProtocol.HubMethod"/> to carry the stream both ways over this Director's own outbound
/// tunnel. Nothing connects in.
///
/// Like the terminal's up-stream verbs, this one is connection-bound (it needs the live hub connection), so it is
/// branched off before the unary verb dispatcher, in <see cref="GatewayStreamClient.HandleCommandAsync"/>. A tunnel
/// drop calls <see cref="CancelAll"/>, which closes every box socket and request this Director is relaying.
/// </summary>
internal sealed class DirectorDesktopRelayHandler
{
    /// <summary>Carries one stream over the tunnel: the frames up, and back the Gateway's frames down. In production
    /// <c>connection.StreamAsync&lt;DesktopRelayFrame&gt;("DesktopRelay", streamId, up, ct)</c>.</summary>
    public delegate IAsyncEnumerable<DesktopRelayFrame> RelayCall(string streamId, IAsyncEnumerable<DesktopRelayFrame> up, CancellationToken ct);

    /// <summary>How long the Director waits for a box's desktop to accept a WebSocket before refusing the open.</summary>
    public static readonly TimeSpan BoxConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostedDesktopLookup _desktops;
    private readonly IDesktopBoxConnector _connector;
    private readonly RelayCall _relay;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DesktopRateLimiter> _boxLimits = new(StringComparer.Ordinal);
    private readonly DesktopRelayBoxTally _tally = new();
    private readonly long _maxBoxBytesPerSecond;
    private int _sockets;
    private int _requests;

    /// <param name="desktops">The Director's hosted-desktop list: the allow-list.</param>
    /// <param name="connector">How the box is reached; production is the loopback connector.</param>
    /// <param name="relay">Carries one stream over the tunnel.</param>
    /// <param name="time">The clock for the rate cap, the connect timeout and the usage lines.</param>
    /// <param name="maxBoxBytesPerSecond">The per-box ceiling toward the browser; production passes nothing and gets
    /// <see cref="DesktopRelayLimits.MaxBoxBytesPerSecond"/>. A test that moves megabytes without a clock passes more.</param>
    public DirectorDesktopRelayHandler(IHostedDesktopLookup desktops, IDesktopBoxConnector connector, RelayCall relay, TimeProvider time,
        long maxBoxBytesPerSecond = DesktopRelayLimits.MaxBoxBytesPerSecond)
    {
        _maxBoxBytesPerSecond = maxBoxBytesPerSecond;
        _desktops = desktops ?? throw new ArgumentNullException(nameof(desktops));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>True for the one verb this handler answers.</summary>
    public static bool IsDesktopVerb(string verb) => string.Equals(verb, DesktopRelayProtocol.OpenVerb, StringComparison.Ordinal);

    /// <summary>Desktop WebSockets being relayed right now.</summary>
    public int ActiveSocketCount => Volatile.Read(ref _sockets);

    /// <summary>Desktop HTTP requests being relayed right now.</summary>
    public int ActiveRequestCount => Volatile.Read(ref _requests);

    /// <summary>The per-box byte totals this Director has relayed. For tests and the log.</summary>
    public DesktopRelayBoxTally Tally => _tally;

    /// <summary>Answer one open. Never throws for a refusal: every refusal is a typed result, and is logged.</summary>
    public async Task<DirectorCommandResult> HandleAsync(DirectorCommand command)
    {
        FileLog.Write($"[DirectorDesktopRelayHandler] HandleAsync: cmdId={command.CommandId}");
        var request = SessionCommandExecutor.Deserialize<OpenDesktopStreamRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.StreamId))
            return Refuse(DirectorCommandStatus.BadRequest, "A desktop stream needs a stream id.", request);
        if (!DesktopRelayBoxIds.IsValid(request.BoxId))
            return Refuse(DirectorCommandStatus.BadRequest, "That is not a box id.", request);

        // THE ALLOW-LIST CHECK. The Director forwards only to a port its own list holds for this one box id, so a
        // Gateway that was fooled into asking for another box, or for any other port, is refused here.
        if (!_desktops.TryResolvePort(request.BoxId, out var port))
            return Refuse(DirectorCommandStatus.NotFound, "This Director does not host a desktop for that box.", request);

        var isSocket = request.Mode == DesktopRelayMode.WebSocket;
        Uri address;
        try
        {
            address = DesktopBoxAddress.Build(port, request.PathAndQuery, isSocket);
        }
        catch (ArgumentException ex)
        {
            return Refuse(DirectorCommandStatus.BadRequest, $"That address cannot be relayed: {ex.Message}", request);
        }

        if (!TryTakeSlot(isSocket))
        {
            return Refuse(DirectorCommandStatus.Conflict, isSocket
                ? $"This Director is already relaying {DesktopRelayLimits.MaxConcurrentDesktopSockets} desktop connections, the most it carries at once."
                : $"This Director is already relaying {DesktopRelayLimits.MaxConcurrentDesktopRequests} desktop requests, the most it carries at once.", request);
        }

        IDesktopRelayEndpoint endpoint;
        string? subProtocol = null;
        var label = $"box {request.BoxId} stream {request.StreamId}";
        if (isSocket)
        {
            try
            {
                using var connectTimeout = new CancellationTokenSource(BoxConnectTimeout, _time);
                var socket = await _connector.ConnectWebSocketAsync(address, request.SubProtocols, connectTimeout.Token).ConfigureAwait(false);
                subProtocol = socket.SubProtocol;
                endpoint = new WebSocketRelayEndpoint(socket, label);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
            {
                ReleaseSlot(isSocket);
                return Refuse(DirectorCommandStatus.Error, $"The box's desktop did not accept the connection: {ex.Message}", request);
            }
        }
        else
        {
            endpoint = new BoxHttpRelayEndpoint(_connector.Http, request.Method, address, request.Headers, request.HasBody, label);
        }

        var cts = new CancellationTokenSource();
        if (!_streams.TryAdd(request.StreamId, cts))
        {
            cts.Dispose();
            ReleaseSlot(isSocket);
            await endpoint.CloseAsync(DesktopRelayEndpointClose.InternalError, "duplicate stream id").ConfigureAwait(false);
            return Refuse(DirectorCommandStatus.Conflict, "That stream id is already open on this Director.", request);
        }

        FileLog.Write($"[DirectorDesktopRelayHandler] open: box={request.BoxId} port={port} mode={request.Mode} {request.Method} {request.PathAndQuery} stream={request.StreamId} (sockets={ActiveSocketCount}, requests={ActiveRequestCount})");
        _ = RunStreamAsync(request, endpoint, isSocket, cts);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new OpenDesktopStreamResponse { SubProtocol = subProtocol }));
    }

    /// <summary>Close every stream this Director is relaying: the tunnel dropped, so none of them can reach the browser.</summary>
    public void CancelAll(string reason)
    {
        var count = 0;
        foreach (var streamId in _streams.Keys)
        {
            if (_streams.TryRemove(streamId, out var cts))
            {
                try { cts.Cancel(); count++; }
                catch (ObjectDisposedException) { /* the stream already ended and released it */ }
            }
        }
        FileLog.Write($"[DirectorDesktopRelayHandler] CancelAll: {count} desktop stream(s) closed ({reason})");
    }

    // The stream's whole life, in the background: the open command has already answered. This task is the only one
    // that disposes the stream's token source and gives its slot back.
    private async Task RunStreamAsync(OpenDesktopStreamRequest request, IDesktopRelayEndpoint endpoint, bool isSocket, CancellationTokenSource cts)
    {
        try
        {
            var up = Channel.CreateUnbounded<DesktopRelayFrame>(new UnboundedChannelOptions { SingleReader = true });
            var down = _relay(request.StreamId, up.Reader.ReadAllAsync(), cts.Token);
            var usage = new DesktopRelayUsage(request.BoxId, request.StreamId, "director");
            var limit = _boxLimits.GetOrAdd(request.BoxId, _ => DesktopRateLimiter.ForBox(_maxBoxBytesPerSecond, _time));
            var pump = new DesktopRelayPump(endpoint, up.Writer, usage, _tally, endpointIsBrowser: false, _time,
                idleTimeout: null, outboundLimit: limit,
                peerGoneReason: "the connection to the Gateway ended",
                endpointGoneReason: "the box's desktop went away");
            var reason = await pump.RunAsync(down, cts.Token, CancellationToken.None).ConfigureAwait(false);
            FileLog.Write($"[DirectorDesktopRelayHandler] stream ended: box={request.BoxId} stream={request.StreamId} reason={reason}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorDesktopRelayHandler] stream FAILED: box={request.BoxId} stream={request.StreamId}: {ex.Message}");
            await endpoint.CloseAsync(DesktopRelayEndpointClose.InternalError, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _streams.TryRemove(request.StreamId, out _);
            ReleaseSlot(isSocket);
            cts.Dispose();
            if (endpoint is IDisposable disposable) disposable.Dispose();
        }
    }

    private bool TryTakeSlot(bool isSocket)
    {
        ref var count = ref isSocket ? ref _sockets : ref _requests;
        var max = isSocket ? DesktopRelayLimits.MaxConcurrentDesktopSockets : DesktopRelayLimits.MaxConcurrentDesktopRequests;
        if (Interlocked.Increment(ref count) <= max) return true;
        Interlocked.Decrement(ref count);
        return false;
    }

    private void ReleaseSlot(bool isSocket)
    {
        if (isSocket) Interlocked.Decrement(ref _sockets);
        else Interlocked.Decrement(ref _requests);
    }

    private static DirectorCommandResult Refuse(DirectorCommandStatus status, string sentence, OpenDesktopStreamRequest? request)
    {
        FileLog.Write($"[DirectorDesktopRelayHandler] REFUSED ({status}): box={request?.BoxId ?? "(none)"} mode={request?.Mode} path={request?.PathAndQuery ?? "(none)"} stream={request?.StreamId ?? "(none)"}: {sentence}");
        return DirectorCommandResult.Fail(status, sentence);
    }
}
