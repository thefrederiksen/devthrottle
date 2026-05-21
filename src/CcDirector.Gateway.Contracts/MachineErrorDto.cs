namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Describes a Director the Gateway tried to aggregate from but couldn't reach.
/// Surfaced in the envelope response of <c>GET /sessions?envelope=true</c> so the
/// UI can render an inline "unreachable" placeholder under the affected machine
/// instead of silently dropping that machine's sessions.
/// </summary>
public sealed class MachineErrorDto
{
    /// <summary>Director GUID of the unreachable Director.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Machine hostname (best-effort copy of the Director's registered MachineName).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>Short human-readable error reason, e.g. "timeout".</summary>
    public string Error { get; set; } = "";
}
