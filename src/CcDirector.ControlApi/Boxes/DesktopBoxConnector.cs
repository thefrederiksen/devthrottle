using System.Net.WebSockets;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi.Boxes;

/// <summary>
/// How the Director reaches a box's desktop: an HTTP client and a WebSocket connect, both aimed only at an address
/// <see cref="DesktopBoxAddress.Build"/> made. Injected so the relay's tests use a fake desktop server in process
/// instead of a real port.
/// </summary>
public interface IDesktopBoxConnector
{
    /// <summary>The client for HTTP requests to a box. It follows no redirect and keeps no cookie.</summary>
    HttpClient Http { get; }

    /// <summary>Open a WebSocket to the box, offering the browser's subprotocols. Throws when the box refuses.</summary>
    Task<WebSocket> ConnectWebSocketAsync(Uri address, IReadOnlyList<string> subProtocols, CancellationToken ct);
}

/// <summary>
/// The one place a box address is made. The host is always <c>127.0.0.1</c> and the port is always the one the
/// hosted-desktop list resolved; the caller supplies only the path and query under the box's prefix, and anything
/// that would leave the box - a host, a scheme, a second slash that starts a network path, a backslash - is refused.
/// This is what keeps the relay from becoming the generic pass-through that was removed on purpose
/// (TunnelCatchAllDispatch).
/// </summary>
public static class DesktopBoxAddress
{
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>Build <c>http://127.0.0.1:&lt;port&gt;&lt;path&gt;</c> (or <c>ws://</c>), or throw <see cref="ArgumentException"/>.</summary>
    public static Uri Build(int port, string pathAndQuery, bool webSocket)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentException($"port {port} is not a port", nameof(port));
        if (string.IsNullOrEmpty(pathAndQuery) || pathAndQuery[0] != '/')
            throw new ArgumentException("the path must start with one slash", nameof(pathAndQuery));
        if (pathAndQuery.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("the path must not start with two slashes (that names a host)", nameof(pathAndQuery));
        foreach (var c in pathAndQuery)
        {
            if (c == '\\' || char.IsControl(c) || c == ' ')
                throw new ArgumentException("the path holds a character an address may not carry", nameof(pathAndQuery));
        }

        var scheme = webSocket ? "ws" : "http";
        var address = new Uri($"{scheme}://{LoopbackHost}:{port}{pathAndQuery}", UriKind.Absolute);
        // Belt and braces: whatever the parser made of the path, the address must still be this host and this port.
        if (!string.Equals(address.Host, LoopbackHost, StringComparison.Ordinal) || address.Port != port || !string.IsNullOrEmpty(address.UserInfo))
            throw new ArgumentException("the path changed the address's host or port", nameof(pathAndQuery));
        return address;
    }
}

/// <summary>The production connector: a direct connection on this machine's loopback, no proxy, no redirect, no cookie.</summary>
public sealed class LoopbackDesktopConnector : IDesktopBoxConnector
{
    public static readonly LoopbackDesktopConnector Instance = new();

    private LoopbackDesktopConnector()
    {
        Http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            // A desktop download or upload lasts as long as it lasts; the relay's own close and idle rules end it.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public HttpClient Http { get; }

    public async Task<WebSocket> ConnectWebSocketAsync(Uri address, IReadOnlyList<string> subProtocols, CancellationToken ct)
    {
        FileLog.Write($"[LoopbackDesktopConnector] ConnectWebSocketAsync: {address}");
        if (!string.Equals(address.Host, DesktopBoxAddress.LoopbackHost, StringComparison.Ordinal))
            throw new ArgumentException($"a desktop WebSocket must be on {DesktopBoxAddress.LoopbackHost}, not {address.Host}", nameof(address));
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        foreach (var protocol in subProtocols)
            socket.Options.AddSubProtocol(protocol);
        try
        {
            await socket.ConnectAsync(address, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
