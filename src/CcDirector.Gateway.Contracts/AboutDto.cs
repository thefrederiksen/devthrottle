namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /about response: the "what is this Gateway running and what's installed" diagnostics the
/// Cockpit About page renders. Built on the Gateway box (it owns installed.json + its own version).
/// </summary>
public sealed class AboutDto
{
    public string Product { get; set; } = "CC Director";

    /// <summary>Full informational version, e.g. "0.6.15+sha".</summary>
    public string Version { get; set; } = "";

    /// <summary>Build date of the running Gateway exe ("yyyy-MM-dd HH:mm:ss"), or null.</summary>
    public string? BuildDate { get; set; }

    public string MachineName { get; set; } = "";

    /// <summary>The per-user install root on the Gateway box (%LOCALAPPDATA%\cc-director).</summary>
    public string InstallRoot { get; set; } = "";

    /// <summary>The one front-door URL the Cockpit is reached at, or null when Tailscale is down.</summary>
    public string? CockpitUrl { get; set; }

    /// <summary>Installed component id -> version (from installed.json on the Gateway box).</summary>
    public Dictionary<string, string> InstalledComponents { get; set; } = new();

    public DateTime ServerTime { get; set; } = DateTime.UtcNow;
}
