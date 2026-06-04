namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /cockpit response. Tells a caller where this machine's Cockpit lives so it never
/// has to hardcode a host or port. The URL is ALWAYS the Tailscale front door
/// (https://&lt;magicdns&gt;:&lt;port&gt;), never a loopback URL: the tailnet is the trust
/// boundary and a localhost URL would only work on this one machine.
/// </summary>
public sealed class CockpitInfoDto
{
    /// <summary>
    /// Tailscale front-door URL of the Cockpit, e.g. https://soren-north.taildb08ed.ts.net:7470.
    /// Null when Tailscale is unavailable on this machine, in which case the caller must surface
    /// the problem rather than fall back to localhost.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>The Cockpit's port (default 7470, or CC_COCKPIT_PORT).</summary>
    public int Port { get; set; }

    /// <summary>True when the Cockpit process is accepting connections on its loopback port.</summary>
    public bool Up { get; set; }
}
