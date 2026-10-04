namespace CcDirector.Core.Teams;

/// <summary>
/// The team this Director works for (devthrottle_internal#2311). Chosen once when the Director is set up
/// (screen D1), shown beside the Director's name (screen D2), and changed in Settings with every session
/// closed (screen D3).
///
/// A null <see cref="TeamId"/> means the person's own personal account ("Personal"): the hosted
/// Gateway never lists the personal tenant as a team, and enrolling without a team id lands there, exactly
/// as before Teams existed.
/// </summary>
/// <param name="TeamId">The team's id on the hosted Gateway, or null for the personal account.</param>
/// <param name="Name">What the team is called, as shown on the chip - "DevThrottle", or "Personal".</param>
public sealed record DirectorTeam(string? TeamId, string Name)
{
    /// <summary>True when this Director works for the person's own personal account, not a team.</summary>
    public bool IsPersonal => TeamId is null;

    /// <summary>
    /// How the personal account is named wherever a team name would be. Not "Soren - personal": the Gateway's
    /// teams reply does not carry the person's name and the Director has no one established place for it, so
    /// the Director does not guess one from the token.
    /// </summary>
    public const string PersonalName = "Personal";

    /// <summary>The personal account.</summary>
    public static DirectorTeam Personal { get; } = new(null, PersonalName);
}
