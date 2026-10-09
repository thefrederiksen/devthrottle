namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One person's membership of one team, carrying their role there (devthrottle_internal#2300). The key is
/// the team plus the verified account subject, so one account holds at most one role per team, and one
/// account may belong to several teams with a different role on each.
///
/// The subject is the key, never the email: an email can change over an account's life. The display email
/// is read from the person's own personal tenant row when a member list is shown, and is not copied here.
///
/// GLOBAL, like <see cref="TeamEntity"/>: no <c>tenant_id</c> column and no query filter.
/// </summary>
public sealed class TeamMemberEntity
{
    /// <summary>The team (<see cref="TeamEntity.Id"/>), which is also the team's tenant id.</summary>
    public string TeamId { get; set; } = "";

    /// <summary>The member's verified Supabase subject (<c>sub</c>). Never logged (personally identifying).</summary>
    public string AccountSubject { get; set; } = "";

    /// <summary>The member's role in this team. Stored as its name, so the column reads plainly and a later
    /// reordering of the roles can never silently change a stored role.</summary>
    public Teams.TeamRole Role { get; set; }

    /// <summary>When this account became a member (UTC).</summary>
    public DateTime JoinedAtUtc { get; set; }

    /// <summary>For a made-up showcase member only: the name shown in place of an account's email. Null for every
    /// real member, whose display email is read from their own tenant row.</summary>
    public string? DisplayName { get; set; }

    /// <summary>For a made-up showcase member only: the email shown. Never sent to - a showcase member is not an
    /// account, and nothing mails a member list.</summary>
    public string? DisplayEmail { get; set; }

    /// <summary>The showcase tag this row was written under by the administrator showcase route
    /// (<see cref="Teams.TeamShowcase"/>), or null for every real row. Removing a showcase deletes exactly the rows
    /// carrying its tag.</summary>
    public string? ShowcaseTag { get; set; }
}
