namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A registered cc-launcher entry as served by GET /launchers and as embedded in the
/// machines listing. Issue #331.
/// </summary>
public sealed class LauncherDto
{
    /// <summary>Hostname of the machine.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>Loopback port the launcher's REST API is bound to (for local relay calls).</summary>
    public int Port { get; set; }

    /// <summary>
    /// Network address the Gateway uses when dialing this launcher from a different machine.
    /// Empty string means the launcher is co-located with the Gateway (loopback applies).
    /// </summary>
    public string NetworkAddress { get; set; } = "";

    /// <summary>OS process id of the launcher.</summary>
    public int Pid { get; set; }

    /// <summary>Launcher version string.</summary>
    public string Version { get; set; } = "";

    /// <summary>UTC timestamp when the launcher process started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp of the last successful registration or heartbeat.</summary>
    public DateTime LastSeenAt { get; set; }
}
