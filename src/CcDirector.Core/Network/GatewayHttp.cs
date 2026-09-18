using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Network;

/// <summary>
/// Builds the HTTP plumbing every gateway-facing client dials through, so a gateway named by a
/// local (Bonjour/mDNS) computer name - <c>http://soren_north.local:7878</c> - connects as reliably
/// as an IP address does.
///
/// Why this exists: macOS answers a <c>.local</c> lookup with the target's link-local IPv6 address
/// FIRST and its IPv4 address second. The default .NET connect tries the addresses one at a time in
/// resolver order, and a TCP connect to a link-local IPv6 address is typically black-holed (no scope
/// id, or the gateway machine not listening on IPv6), so it hangs until the whole HttpClient timeout
/// elapses - the request dies without ever trying the IPv4 address that would have connected
/// instantly. curl succeeds on the same name only because it races both address families.
///
/// The fix is a connect callback that orders candidate addresses by how likely they are to work on a
/// local network - IPv4 first, globally routable IPv6 next, link-local IPv6 last - and gives each
/// address a short connect budget so one dead address can never consume the caller's whole timeout.
/// Hosts that are already IP literals (including loopback) bypass resolution and connect directly,
/// so nothing changes for them.
/// </summary>
public static class GatewayHttp
{
    /// <summary>
    /// Connect budget for a candidate address that HAS A NEXT ONE TO TRY. Short enough that a dead
    /// first address still leaves room for the next one inside the 5-second timeout the tightest
    /// gateway clients use.
    ///
    /// It does NOT apply to the last candidate - see <see cref="BudgetFor"/> for why.
    /// </summary>
    private static readonly TimeSpan PerAddressConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The connect budget for candidate <paramref name="index"/> of <paramref name="count"/>, or null
    /// for no budget at all - in which case the caller's own cancellation (its request timeout) is the
    /// only deadline.
    ///
    /// WHY THE LAST CANDIDATE IS NOT CAPPED. This budget buys exactly one thing: the chance to ABANDON a
    /// black-holed address and dial the NEXT one. On the last candidate there is no next one, so a cap
    /// cannot buy anything - it can only fail a connect that was merely slow, and hand the caller a
    /// "no address accepted a connection" that reads like an unreachable gateway when the gateway was
    /// fine. A gateway named on the local network resolves to two addresses (the internet protocol
    /// version six link-local address first, then the version four address) and still behaves exactly as
    /// before: the first is capped and the dial moves on. A hosted gateway name resolving to ONE address
    /// is now bounded by the caller's timeout instead of by three seconds.
    ///
    /// Measured before this change, on a Director whose machine was starved of processor time: 352
    /// dialing failures in one day, every one of them this budget expiring, not one of them the far side
    /// refusing - while a plain command-line request to the same host connected in 27 to 54
    /// milliseconds. Issue #3090.
    ///
    /// Pure, so the rule is tested without opening a socket.
    /// </summary>
    public static TimeSpan? BudgetFor(int index, int count) =>
        index < count - 1 ? PerAddressConnectTimeout : null;

    /// <summary>
    /// One shared invoker for websocket upgrades. Never disposed on purpose: the upgraded stream
    /// lives on inside the returned websocket, so tearing the handler down per-connect would rip the
    /// transport out from under a live connection.
    /// </summary>
    private static readonly HttpMessageInvoker WebSocketInvoker = new(Handler(), disposeHandler: true);

    /// <summary>
    /// A handler whose connect callback applies the local-name-friendly address ordering. The pooled
    /// connection lifetime is capped so a long-lived client re-resolves the gateway name within a
    /// couple of minutes of its address changing (a name is the only stable thing on a home network).
    /// </summary>
    public static SocketsHttpHandler Handler() => new()
    {
        ConnectCallback = ConnectAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>
    /// Opens a client websocket to <paramref name="uri"/> through the same address-ordered dialing,
    /// for the SignalR stream clients (their default websocket path uses the OS-ordered addresses
    /// and hangs on <c>.local</c> names exactly like the HTTP one). Sends the bearer token as an
    /// Authorization header, matching what the default SignalR websocket transport does.
    /// </summary>
    public static async Task<WebSocket> ConnectWebSocketAsync(Uri uri, string? bearerToken, CancellationToken cancellationToken)
    {
        var webSocket = new ClientWebSocket();
        if (!string.IsNullOrEmpty(bearerToken))
            webSocket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        await webSocket.ConnectAsync(uri, WebSocketInvoker, cancellationToken).ConfigureAwait(false);
        return webSocket;
    }

    /// <summary>
    /// Orders resolved addresses by dialing preference: IPv4, then globally routable IPv6, then
    /// link-local IPv6 as the last resort. The sort is stable, so within one rank the resolver's
    /// own order is kept. Pure, for direct unit testing.
    /// </summary>
    public static IReadOnlyList<IPAddress> OrderForDialing(IEnumerable<IPAddress> addresses)
    {
        static int Rank(IPAddress address) =>
            address.AddressFamily == AddressFamily.InterNetwork ? 0
            : address.IsIPv6LinkLocal ? 2
            : 1;

        return addresses.OrderBy(Rank).ToArray();
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(host, out var literal))
            addresses = new[] { literal };
        else
            addresses = OrderForDialing(await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false));

        if (addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception? lastFailure = null;
        for (var index = 0; index < addresses.Count; index++)
        {
            var address = addresses[index];
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (BudgetFor(index, addresses.Count) is { } budget)
                    attempt.CancelAfter(budget);
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token).ConfigureAwait(false);
                if (lastFailure is not null)
                    FileLog.Write($"[GatewayHttp] ConnectAsync: {host}:{port} connected via {address} after an earlier address failed: {lastFailure.Message}");
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                // The caller itself gave up (its HttpClient timeout or an app shutdown) - stop dialing.
                cancellationToken.ThrowIfCancellationRequested();
                lastFailure = ex;
            }
        }

        FileLog.Write($"[GatewayHttp] ConnectAsync FAILED: no address of {host}:{port} accepted a connection ({addresses.Count} tried): {lastFailure!.Message}");
        throw new HttpRequestException($"No address of {host}:{port} accepted a connection.", lastFailure);
    }
}
