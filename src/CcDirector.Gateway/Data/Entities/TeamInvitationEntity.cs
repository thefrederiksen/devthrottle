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
/// THE ACCEPT TOKEN is the secret the email's link carries. It is the only way to reach the accept page, so it is
/// random and long, it is never returned by any Gateway route, and it is replaced every time the invitation is sent
/// again - an older email's link stops working the moment a newer one is sent. The website reads it (through its own
/// database function) only to build the link in the email it sends.
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

    /// <summary>The secret the email's link carries. Never logged, never returned by a Gateway route.</summary>
    public string AcceptToken { get; set; } = "";

    /// <summary>When it was accepted, declined or cancelled (UTC); null while it is waiting.</summary>
    public DateTime? RespondedAtUtc { get; set; }

    /// <summary>The account that accepted it, or null. Never logged.</summary>
    public string? AcceptedBySubject { get; set; }
}
