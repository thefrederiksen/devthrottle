namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A registered cc-launcher entry as served by GET /launchers and as embedded in the
/// machines listing. Issue #331.
///
/// Remove-the-network-port mission, phase 6: no port and no network address any more. The launcher
/// listens on nothing; commands reach it down the persistent stream it opens to the Gateway, so
/// there is no dial-back address to publish. Liveness is the heartbeat (<see cref="LastSeenAt"/>)
/// and, for command delivery, the stream connection being up.
/// </summary>
public sealed class LauncherDto
{
    /// <summary>Hostname of the machine.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>OS process id of the launcher.</summary>
    public int Pid { get; set; }

    /// <summary>Launcher version string.</summary>
    public string Version { get; set; } = "";

    /// <summary>UTC timestamp when the launcher process started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp of the last successful registration or heartbeat.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// UTC timestamp of the last time this machine's launcher command stream closed, or null when this Gateway
    /// has not seen one close since it started.
    ///
    /// IT IS THE PROOF THAT SEPARATES "RESTARTING" FROM "TOO OLD" (issue #4423). A launcher that heartbeats
    /// while holding no stream used to be called too old for commands - and that is also exactly what a
    /// launcher looks like in the minute it takes to restart into a new version: the new process registers
    /// before it opens its stream. A launcher this Gateway has watched hold a stream is not too old, whatever
    /// it looks like now. Kept across a re-registration, because the new process is the one that has not
    /// opened its stream yet.
    /// </summary>
    public DateTime? StreamClosedAt { get; set; }
}
