namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /directors/register. A Director sends this on startup (and after
/// reconnects) so the Gateway adds or refreshes its row in the directory.
///
/// The Director is the source of truth for its tailnet endpoint - the Gateway
/// never tries to infer the Director's routable URL from the inbound socket
/// because reverse proxies and Tailscale name-mapping make that unreliable.
/// </summary>
public sealed class DirectorRegistrationRequest
{
    /// <summary>Stable per-Director GUID (persisted in director-id.txt).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Cross-machine URL that the Gateway should deeplink browser users to.</summary>
    public string TailnetEndpoint { get; set; } = "";

    /// <summary>OS process id of the Director (informational, used for diagnostics).</summary>
    public int Pid { get; set; }

    /// <summary>Hostname of the machine the Director is running on.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>OS user the Director is running as.</summary>
    public string User { get; set; } = "";

    /// <summary>Director version string.</summary>
    public string Version { get; set; } = "";

    /// <summary>UTC timestamp when the Director process started.</summary>
    public DateTime StartedAt { get; set; }
}
