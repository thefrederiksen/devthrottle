namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /cockpit response. Tells a caller where this machine's Cockpit lives so it never
/// has to hardcode a host or port. ONE URL (docs/plans/one-url-cockpit.md): the Cockpit is
/// served through the Gateway's Tailscale front door via the fallback proxy, so the URL is
/// the front door itself - never a :7470 URL, never loopback (the tailnet is the trust
/// boundary and a localhost URL would only work on this one machine).
/// </summary>
public sealed class CockpitInfoDto
{
    /// <summary>
    /// The front-door URL serving the Cockpit, e.g. https://machine-a.tail0123.ts.net/.
    /// Null when Tailscale is unavailable on this machine, in which case the caller must surface
    /// the problem rather than fall back to localhost.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>The loopback port the supervised Cockpit child listens on (diagnostics only).</summary>
    public int Port { get; set; }

    /// <summary>True when the Cockpit process is accepting connections on its loopback port.</summary>
    public bool Up { get; set; }
}
