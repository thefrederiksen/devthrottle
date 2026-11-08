namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /fleet/counts response: how many Directors and sessions the CALLER'S OWN account has right now.
///
/// The signed-in counterpart of the counts the public <c>/healthz</c> leaves out on the hosted Gateway. That probe
/// has no tenant, so it has no correct number to print; this route resolves the caller's account first and counts
/// only that account. Both numbers are always present - this route answers only when it has a tenant to count.
/// </summary>
public sealed class FleetCountsDto
{
    /// <summary>The account's registered Directors.</summary>
    public int Directors { get; set; }

    /// <summary>The sessions those Directors last pushed. A Director with no fresh pushed snapshot - one not
    /// connected to the tunnel - contributes none.</summary>
    public int Sessions { get; set; }
}
