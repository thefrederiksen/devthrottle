namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One invitation to join a team, sent to an email address (devthrottle_internal#2301). The Owner or a Manager
/// invites by ANY email address; the invitation names the role the person will hold once they accept, and is good
/// for <see cref="Teams.TeamInvitationRules.ValidFor"/> after it was last sent.
///
/// THE STORED STATE is one of <c>sent</c>, <c>accepted</c>, <c>declined</c> or <c>cancelled</c>. The fifth state a
/// person can see, <c>expired</c>, is never written: it is what a <c>sent</c> invitation reads as once
/// <see cref="ExpiresAtUtc"/> has passed. Keeping it derived means no sweep has to run for an invitation to expire on
/// time, and no reader can see a row that should have expired but has not been swept yet.
///
/// THE ACCEPT TOKEN is the secret the email's link carries, and only its HASH is stored (<see cref="AcceptTokenHash"/>).
/// The token itself exists in two places only: the email, and - for the moment it takes to ask for that email - the
/// Gateway's call to the website, which checks the token against this hash before it builds the link. So a copy of
/// this table cannot be turned into working links. The token is random and long, single use, dies with the
/// invitation's 7 days, and is replaced on every resend - an older email's link stops working the moment a newer one
/// is sent. No Gateway route ever returns it.
///
/// GLOBAL, like <see cref="TeamMemberEntity"/>: no <c>tenant_id</c> column and no query filter. An invitation is
/// opened by the person invited, from their own personal tenant, before they are a member of the team's tenant.
/// </summary>
public sealed class TeamInvitationEntity
{
    /// <summary>The invitation's id - a GUID string generated in code. Shown to the team's Owner and Managers so they
    /// can resend or cancel it; it is not a secret and does not open the accept page.</summary>
    public string Id { get; set; } = "";

    /// <summary>The team (<see cref="TeamEntity.Id"/>), which is also the team's tenant id.</summary>
    public string TeamId { get; set; } = "";

    /// <summary>The address invited, trimmed and lower-cased. Personally identifying: never logged.</summary>
    public string Email { get; set; } = "";

    /// <summary>The role the person holds once they accept. Never Owner - a team has exactly one.</summary>
    public Teams.TeamRole Role { get; set; }

    /// <summary>sent, accepted, declined or cancelled - see the class remarks for why expired is not stored.</summary>
    public string State { get; set; } = "";

    /// <summary>The account subject of the Owner or Manager who sent it. Never logged.</summary>
    public string InvitedBySubject { get; set; } = "";

    /// <summary>When the invitation was first created (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the invitation was last sent (UTC). Resending moves this and <see cref="ExpiresAtUtc"/>.</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary>The moment the invitation stops being acceptable (UTC): <see cref="SentAtUtc"/> plus the validity.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>The SHA-256 of the link's secret, lower-case hexadecimal. The secret itself is never stored, logged or
    /// returned by a Gateway route (see <see cref="Teams.TeamInvitationRules.HashAcceptToken"/>).</summary>
    public string AcceptTokenHash { get; set; } = "";

    /// <summary>When it was accepted, declined or cancelled (UTC); null while it is waiting.</summary>
    public DateTime? RespondedAtUtc { get; set; }

    /// <summary>The account that accepted it, or null - so the Owner and Managers can see who used the link, which need
    /// not be the address it was sent to. Never logged.</summary>
    public string? AcceptedBySubject { get; set; }
}
