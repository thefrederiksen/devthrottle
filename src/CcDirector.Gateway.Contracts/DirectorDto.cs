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
    /// How this Director got into the registry. "file" = FSW-discovered same-machine.
    /// "http" = registered via POST /directors/register from anywhere on the network.
    /// Defaults to "file" for backward compat with existing JSON registration files.
    /// </summary>
    public string Source { get; set; } = "file";
}
