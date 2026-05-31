using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CcDirector.Core.Network;

/// <summary>
/// Pure helpers backing the Settings page "Detect" buttons for gateway connectivity:
///
///   - <see cref="LocalGatewayCandidates"/>: the base URLs to probe when looking for a
///     gateway running on THIS machine (the co-located case).
///   - <see cref="BuildAdvertisedUrl"/>: assemble the "Director public URL" a remote gateway
///     calls back to, from a host and the Director's control port.
///   - <see cref="BestLocalAddress"/>: pick the most reachable non-loopback IPv4 address of
///     this machine, preferring a Tailscale tailnet address, so the advertised endpoint is
///     something the gateway can actually reach (loopback is the default and is unreachable
///     from another machine - that is why a Mac Director never shows up on a remote gateway).
///
/// The HTTP probing itself lives at the call site (an entry-point handler) so these stay
/// side-effect-free and unit-testable.
/// </summary>
public static class EndpointProbe
{
    /// <summary>The gateway's conventional port (matches GatewayHost.DefaultPort).</summary>
    public const int DefaultGatewayPort = 7878;

    /// <summary>
    /// Base URLs to probe for a gateway co-located on this machine, in priority order.
    /// </summary>
    public static IReadOnlyList<string> LocalGatewayCandidates(int gatewayPort = DefaultGatewayPort) =>
        new[]
        {
            $"http://127.0.0.1:{gatewayPort}",
            $"http://localhost:{gatewayPort}",
        };

    /// <summary>
    /// Build the "http://host:port" URL the gateway uses to reach this Director. Accepts a
    /// bare host/IP or one that already carries a scheme; the port is always this Director's
    /// control port. Throws on an empty host or non-positive port - detection must produce a
    /// real URL, never a loopback fallback.
    /// </summary>
    public static string BuildAdvertisedUrl(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("host is required", nameof(host));
        if (port <= 0)
            throw new ArgumentOutOfRangeException(nameof(port), port, "port must be positive");

        var trimmed = host.Trim();

        // If a full URL was passed, keep its host but force our control port.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Host}:{port}";

        return $"http://{trimmed}:{port}";
    }

    /// <summary>
    /// True when an IPv4 address sits in the Tailscale CGNAT range (100.64.0.0/10), i.e. it
    /// is a tailnet address. Preferred for the advertised endpoint because both the Director
    /// and a co-tailnet gateway are reachable to each other over it.
    /// </summary>
    public static bool IsTailscaleAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        // 100.64.0.0/10 -> first octet 100, second octet 64-127.
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    /// <summary>
    /// True for the RFC1918 private LAN ranges (10/8, 172.16/12, 192.168/16).
    /// </summary>
    public static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    /// <summary>
    /// From a set of candidate IPv4 addresses, choose the best for the advertised endpoint:
    /// a Tailscale address first, then a private LAN address, then any remaining IPv4. Returns
    /// null when nothing usable is present (the caller surfaces that, it never substitutes
    /// loopback). Pure so it can be unit-tested without touching real interfaces.
    /// </summary>
    public static IPAddress? ChooseBest(IEnumerable<IPAddress> candidates)
    {
        var usable = candidates
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Where(a => !IPAddress.IsLoopback(a))
            .Where(a => !IsLinkLocal(a))
            .ToList();

        return usable.FirstOrDefault(IsTailscaleAddress)
            ?? usable.FirstOrDefault(IsPrivateLanAddress)
            ?? usable.FirstOrDefault();
    }

    /// <summary>169.254.0.0/16 auto-config addresses, never routable.</summary>
    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    /// <summary>
    /// Enumerate this machine's up, non-loopback interfaces and return the best IPv4 address
    /// for the advertised endpoint (see <see cref="ChooseBest"/>). Returns null when the
    /// machine has no usable address.
    /// </summary>
    public static IPAddress? BestLocalAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Select(ua => ua.Address);

        return ChooseBest(addresses);
    }
}
