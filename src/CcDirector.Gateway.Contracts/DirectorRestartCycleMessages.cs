namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The payload of the <c>director/restart-cycle</c> command the Gateway sends down a Director's stream
/// when the owner accepts a restart request (issue #2725). Everything the Director needs to run the
/// cycle alone and to report against the right request.
/// </summary>
public sealed class DirectorRestartCycleOrder
{
    /// <summary>The request this cycle answers. Every report the Director sends names it.</summary>
    public string RequestId { get; set; } = "";

    /// <summary>The machine as the requester named it - the name the Director uses when it asks the
    /// Gateway about its own launcher.</summary>
    public string Machine { get; set; } = "";

    /// <summary>Why, in the requester's words. Written into the drain's record.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The session that asked, for the record.</summary>
    public string RequestedBySessionId { get; set; } = "";

    /// <summary>Its name at the time, for the record.</summary>
    public string RequestedBySessionName { get; set; } = "";

    /// <summary>When the owner accepted.</summary>
    public DateTime AcceptedAtUtc { get; set; }
}

/// <summary>
/// The Director's answer to <c>director/restart-eligibility</c>: is THIS Director the one its launcher
/// supervises and would restart? Asked by the Gateway before the owner is ever shown a request.
///
/// <see cref="Eligible"/> is nullable so that a Director which could not establish the answer says so
/// rather than saying no. All three answers are refusals on the Gateway except an explicit true.
/// </summary>
public sealed class DirectorRestartEligibilityDto
{
    /// <summary>True when this Director is the instance and executable its launcher supervises. False when
    /// it is provably not (a named instance, a development slot). Null when it could not tell.</summary>
    public bool? Eligible { get; set; }

    /// <summary>The reason, in a sentence, whichever way the answer went.</summary>
    public string Reason { get; set; } = "";

    /// <summary>This Director's executable path, as evidence.</summary>
    public string? ProcessPath { get; set; }

    /// <summary>The executable its launcher would start, as evidence.</summary>
    public string? SupervisedPath { get; set; }

    /// <summary>The instance slug this Director runs as.</summary>
    public string InstanceSlug { get; set; } = "";
}

/// <summary>The two host-level verbs of the restart cycle, spelled once for the Gateway that sends them
/// and the Director that answers them.</summary>
public static class DirectorRestartVerbs
{
    /// <summary>Ask a Director whether a launcher restart would restart IT. A read.</summary>
    public const string Eligibility = "director/restart-eligibility";

    /// <summary>Hand a Director the accepted request; it runs the cycle alone from here.</summary>
    public const string Cycle = "director/restart-cycle";
}
