using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Network;

/// <summary>
/// Resolves this machine's own LAN IPv4 address for the <c>lan</c> addressing mode (issue #457):
/// the address a Director advertises (<c>http://&lt;lan-ip&gt;:&lt;port&gt;</c>) so any other machine on
/// the same physical/Wi-Fi network can reach it WITHOUT Tailscale.
///
/// Selection rules (a routable LAN address, never a misleading one):
///   - IPv4 only, on an interface that is Up and is not loopback/tunnel.
///   - Exclude loopback (127/8) and APIPA/link-local (169.254/16).
///   - Exclude the Tailscale CGNAT range (100.64/10): in LAN mode we want the REAL LAN IP,
///     not the tailnet address.
///   - Prefer RFC-1918 private addresses (192.168/16, 10/8, 172.16/12) - the normal LAN case.
///
/// Returns null when no such address exists (e.g. only loopback / only a tailnet IP). The
/// caller surfaces that truthfully rather than substituting a loopback URL.
/// </summary>
public static class LanIdentity
{
    /// <summary>
    /// This machine's best LAN IPv4 as a string (e.g. <c>192.168.1.42</c>), or null when none
    /// is available. Enumerates the live network interfaces; see the class doc for the rules.
    /// </summary>
    public static string? TryGetPrimaryLanIpv4()
    {
        try
        {
            var candidates = new List<IPAddress>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue; // IPv4 only
                    if (IsExcluded(ua.Address)) continue;
                    candidates.Add(ua.Address);
                }
            }

            if (candidates.Count == 0) return null;

            // Prefer a private RFC-1918 address (the normal LAN case); else take the first
            // remaining routable IPv4 (e.g. a directly-assigned public address).
            var best = candidates.FirstOrDefault(IsPrivate) ?? candidates[0];
            return best.ToString();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LanIdentity] TryGetPrimaryLanIpv4 failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Compose the LAN endpoint URL. Pure - unit-tested.</summary>
    public static string BuildLanUrlForPort(string lanIp, int port)
    {
        if (string.IsNullOrWhiteSpace(lanIp))
            throw new ArgumentException("LAN IP is required", nameof(lanIp));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1-65535");
        return $"http://{lanIp}:{port}";
    }

    /// <summary>Loopback, APIPA/link-local, and the Tailscale CGNAT range are never LAN candidates.</summary>
    private static bool IsExcluded(IPAddress ip)
    {
        var b = ip.GetAddressBytes(); // 4 bytes for IPv4
        if (IPAddress.IsLoopback(ip)) return true;            // 127/8
        if (b[0] == 169 && b[1] == 254) return true;          // 169.254/16 APIPA / link-local
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // 100.64/10 Tailscale CGNAT
        return false;
    }

    /// <summary>True for RFC-1918 private IPv4 (the normal home/office LAN ranges).</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;                          // 10/8
        if (b[0] == 192 && b[1] == 168) return true;          // 192.168/16
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16/12
        return false;
    }
}
