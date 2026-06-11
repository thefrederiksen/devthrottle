namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /launchers/register. A cc-launcher process sends this on startup and
/// heartbeats every 30 s so the Gateway knows which launchers are live and on which
/// machines, together with the loopback port the Gateway relay must hit.
///
/// Issue #331: the Gateway uses the registered port + token to forward lifecycle verbs
/// (restart/start/stop/launch) to the remote launcher's loopback REST API.
/// </summary>
public sealed class LauncherRegistrationRequest
{
    /// <summary>Hostname of the machine the launcher is running on (Environment.MachineName).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>Loopback port the launcher's REST API is bound to.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Bearer token the Gateway must send when calling back into the launcher.
    /// The launcher generates this on first start and writes it to launcher-token.txt;
    /// it is long-lived (survives restarts of the launcher process).
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>OS process id of the launcher (informational / diagnostics).</summary>
    public int Pid { get; set; }

    /// <summary>Launcher version string.</summary>
    public string Version { get; set; } = "";

    /// <summary>UTC timestamp when the launcher process started.</summary>
    public DateTime StartedAt { get; set; }
}
