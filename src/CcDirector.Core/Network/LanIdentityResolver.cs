using CcDirector.Core.Utilities;

namespace CcDirector.Core.Network;

/// <summary>
/// Resolves the routable endpoint a Director advertises in the <c>lan</c> addressing mode
/// (issue #457), the LAN-IP counterpart of <see cref="TailnetIdentityResolver"/>. The shape
/// matches it exactly so the two are interchangeable behind the GatewayClient's
/// <c>ResolveAdvertisedEndpoint</c> seam, and both return a <see cref="TailnetEndpointResolution"/>
/// (the field is the routable endpoint regardless of mode).
///
/// Order: an explicit non-loopback <c>gateway.tailnetEndpoint</c> override wins (a hand-run
/// reverse proxy / static DNS), else this machine's primary LAN IPv4. A loopback override is
/// refused, never advertised - the same no-loopback policy the Tailscale resolver enforces.
///
/// The IPv4 probe is a seam so the resolution is unit-testable without a real NIC.
/// </summary>
public sealed class LanIdentityResolver
{
    /// <summary>The LAN IPv4 probe. Returns the address string (e.g. 192.168.1.42) or null.</summary>
    public Func<string?> LanIpProbe { get; set; } = LanIdentity.TryGetPrimaryLanIpv4;

    // Log-dedup state: this resolver runs every heartbeat cycle (issue #324), so identical
    // outcomes are logged ONCE per transition, not 4x/minute (the #197 log-drowning lesson).
    private string? _lastLoggedFailure;
    private string? _lastLoggedSuccess;

    /// <summary>
    /// Run the LAN resolution for this Director's <paramref name="port"/>.
    /// <paramref name="configOverride"/> is the optional <c>gateway.tailnetEndpoint</c> value.
    /// Never throws; an unresolvable address is an EXPECTED state reported via
    /// <see cref="TailnetEndpointResolution.FailureReason"/> so the caller can fail loudly.
    /// </summary>
    public TailnetEndpointResolution ResolveEndpoint(int port, string? configOverride)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1-65535");

        if (!string.IsNullOrWhiteSpace(configOverride))
        {
            if (TailnetIdentityResolver.IsLoopback(configOverride))
                return Unresolved($"gateway.tailnetEndpoint override '{configOverride}' is a loopback address and is never advertised - "
                                + "set it to a LAN-routable URL, or remove it to auto-detect this machine's LAN IP.");
            return Resolved(configOverride, "config-override");
        }

        var ip = LanIpProbe();
        if (string.IsNullOrWhiteSpace(ip))
            return Unresolved("No routable LAN IPv4 address found on this machine (only loopback, link-local, or a tailnet address). "
                            + "Connect this machine to the LAN, or set gateway.tailnetEndpoint to its reachable URL.");

        return Resolved(LanIdentity.BuildLanUrlForPort(ip, port), "lan");
    }

    private TailnetEndpointResolution Resolved(string endpoint, string source)
    {
        var line = $"{endpoint} via {source}";
        if (_lastLoggedSuccess != line)
        {
            _lastLoggedSuccess = line;
            _lastLoggedFailure = null;
            FileLog.Write($"[LanIdentityResolver] ResolveEndpoint: resolved {line}");
        }
        return new TailnetEndpointResolution { Endpoint = endpoint, Source = source };
    }

    private TailnetEndpointResolution Unresolved(string reason)
    {
        if (_lastLoggedFailure != reason)
        {
            _lastLoggedFailure = reason;
            _lastLoggedSuccess = null;
            FileLog.Write($"[LanIdentityResolver] ResolveEndpoint FAILED: {reason}");
        }
        return new TailnetEndpointResolution { FailureReason = reason };
    }
}
