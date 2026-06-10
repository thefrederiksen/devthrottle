using CcDirector.Core.Configuration;

namespace CcDirector.Avalonia;

/// <summary>
/// Resolves the gateway base URL the Cockpit toolbar button should probe for its
/// <c>GET {base}/cockpit</c> request. This is selection by a single configured source
/// of truth (<see cref="GatewayConfig"/>'s <c>gateway.url</c> block of <c>config.json</c>),
/// NOT a fallback chain: a configured gateway URL is used everywhere; the loopback default
/// is used only when no gateway URL is configured at all (correct for same-machine setups).
/// </summary>
public static class CockpitUrlResolver
{
    /// <summary>
    /// Loopback gateway base used only when no <c>gateway.url</c> is configured. Mirrors
    /// <see cref="Controls.DirectorView.DirectorView.DefaultGatewayUrl"/>.
    /// </summary>
    public const string LocalhostDefault = "http://127.0.0.1:7878";

    /// <summary>
    /// Returns the gateway base URL to probe for the Cockpit front door. When a gateway
    /// URL is configured (<see cref="GatewayConfig.IsEnabled"/>), its <see cref="GatewayConfig.Url"/>
    /// is used with any trailing slashes stripped so <c>{base}/cockpit</c> never yields
    /// <c>//cockpit</c>. When no gateway URL is configured, the loopback default is returned.
    /// </summary>
    /// <param name="cfg">The gateway configuration, typically from <see cref="GatewayConfig.Load"/>.</param>
    /// <returns>The base URL (no trailing slash) to which <c>/cockpit</c> is appended.</returns>
    public static string ResolveCockpitBase(GatewayConfig cfg)
    {
        if (cfg is null)
            throw new ArgumentNullException(nameof(cfg));

        if (cfg.IsEnabled)
            return cfg.Url.TrimEnd('/');

        return LocalhostDefault;
    }

    /// <summary>
    /// True when the resolved base is the loopback default, i.e. no remote gateway is
    /// configured. The Cockpit button uses this to decide whether the "Is the Gateway tray
    /// app running on this machine?" hint applies (it only applies to the local case).
    /// </summary>
    public static bool IsLocalhostDefault(string baseUrl) =>
        string.Equals(baseUrl, LocalhostDefault, StringComparison.OrdinalIgnoreCase);
}
