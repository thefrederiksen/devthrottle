namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Describes a running CC Director instance discovered by the Gateway.
/// Serialized to and from the instances/{guid}.json registration files
/// and to JSON for GET /directors.
/// </summary>
public sealed class DirectorDto
{
    /// <summary>Stable per-process GUID assigned by the Director on startup.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>OS process id of the Director.</summary>
    public int Pid { get; set; }

    /// <summary>UTC timestamp when the Director started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>HTTP base URL for the Director's internal control endpoint, e.g. http://127.0.0.1:55321 .</summary>
    public string ControlEndpoint { get; set; } = "";

    /// <summary>Hostname of the machine the Director is running on.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>OS user name the Director is running as.</summary>
    public string User { get; set; } = "";

    /// <summary>Director version string (informational).</summary>
    public string Version { get; set; } = "";

    /// <summary>Schema version for forward-compat. Always 1 in v1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>UTC timestamp the Gateway last successfully reached this Director. Server-side only.</summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Cross-machine endpoint that browser users should be deeplinked to. Set by HTTP
    /// registration (the Director knows its own routable URL). Null for FSW-discovered
    /// Directors (use <see cref="ControlEndpoint"/> in that case).
    /// </summary>
    public string? TailnetEndpoint { get; set; }

    /// <summary>
    /// Issue #324: the Director's own declaration that it has NO reachable advertised
    /// endpoint (no tailnet identity resolved), with the reason naming the fix. Null when
    /// the endpoint is claimed reachable. While set, the Gateway must not probe the
    /// (empty) endpoint - the Director told us why it cannot answer.
    /// </summary>
    public string? EndpointUnreachableReason { get; set; }

    /// <summary>
    /// How this Director got into the registry. "file" = FSW-discovered same-machine.
    /// "http" = registered via POST /directors/register from anywhere on the network.
    /// Defaults to "file" for backward compat with existing JSON registration files.
    /// </summary>
    public string Source { get; set; } = "file";

    /// <summary>
    /// UTC timestamp of the last PASSING two-way handshake (issues #223/#224): the
    /// Director reached the Gateway AND the Gateway's nonce callback reached the Director.
    /// Null = never verified for this registration (resets on re-register, which is
    /// truthful - a fresh registration may carry a different endpoint). Server-side stamp.
    /// </summary>
    public DateTime? TwoWayVerifiedAt { get; set; }

    /// <summary>Value of <see cref="AdvertisedEndpointState"/> when the last probe answered as this Director.</summary>
    public const string EndpointStateOk = "ok";

    /// <summary>Value of <see cref="AdvertisedEndpointState"/> when the advertised name stopped answering (or answered as the wrong process).</summary>
    public const string EndpointStateUnreachableByName = "unreachable-by-name";

    /// <summary>
    /// Issue #325: result of the Gateway's PERIODIC re-verification of the advertised
    /// endpoint (every heartbeat cycle, not just at registration like #223/#224).
    /// <see cref="EndpointStateOk"/> = the last probe answered /healthz as this Director;
    /// <see cref="EndpointStateUnreachableByName"/> = the Director is alive (heartbeating)
    /// but its advertised name no longer answers - distinct from heartbeat loss.
    /// Null = not probed yet, or not applicable (FSW-discovered local, or a #324 flagged
    /// no-endpoint registration). Server-side stamp; resets on re-register.
    /// </summary>
    public string? AdvertisedEndpointState { get; set; }

    /// <summary>UTC timestamp of the most recent advertised-endpoint probe (issue #325). Server-side stamp.</summary>
    public DateTime? AdvertisedEndpointCheckedAt { get; set; }

    /// <summary>UTC timestamp when the advertised endpoint STARTED failing probes (issue #325).
    /// Null while the state is not <see cref="EndpointStateUnreachableByName"/>.</summary>
    public DateTime? AdvertisedEndpointUnreachableSince { get; set; }

    /// <summary>Why the last advertised-endpoint probe failed (issue #325). Null while reachable.</summary>
    public string? AdvertisedEndpointError { get; set; }
}
