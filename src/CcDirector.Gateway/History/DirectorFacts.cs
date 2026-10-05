namespace CcDirector.Gateway.History;

/// <summary>
/// What the Gateway knows about the Director a session is running on, taken from its connection
/// record rather than from the session payload.
///
/// WHY THIS EXISTS AS A TYPE. Both facts on it were missing from every session history row ever
/// written - the machine name because the pushed field is hard-coded empty
/// (<c>ControlEndpoints.Map</c> defaults <c>machineName</c> to <c>""</c> and no caller passes it),
/// and the version because the session payload has never carried one. Both DO arrive on
/// <c>DirectorStreamHello</c> and sit in <see cref="Discovery.DirectorRegistry"/> from the moment a
/// Director connects.
///
/// Stamping them from there rather than from the push is what makes the fix work for clients
/// ALREADY IN THE FIELD: nothing has to be released, and nobody has to upgrade, for the next
/// session they run to be recorded properly.
/// </summary>
/// <param name="MachineName">The host the Director runs on, or null when it is not known.</param>
/// <param name="Version">The Director's version string, or null when it is not known.</param>
/// <param name="PersonSubject">In a TEAM's tenant, the account subject of the person the Director's key was issued to
/// (devthrottle_internal#2305), read through the team caller resolver of devthrottle_internal#2311. Null in a personal
/// tenant, and null when the resolver cannot name the person - never guessed.</param>
public readonly record struct DirectorFacts(string? MachineName, string? Version, string? PersonSubject = null)
{
    /// <summary>Nothing known - the Gateway has no live record for this Director. Distinct from a
    /// record whose fields are blank, and both are written as null rather than "".</summary>
    public static readonly DirectorFacts Unknown = new(null, null, null);
}
