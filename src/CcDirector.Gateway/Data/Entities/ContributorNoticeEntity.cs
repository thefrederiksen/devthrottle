namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE THING SENT TO A CONTRIBUTOR THAT THEY HAVE NOT BEEN TOLD ABOUT YET (<c>contributor_notices</c>, Team implementation
/// piece 4.4, devthrottle_internal#2801). A report sent to them, a factory shared with them, or the team's Owner deciding
/// on something they wrote each add one row; the notice sweep (<see cref="Teams.ContributorNoticeSweep"/>) tells the person
/// by email according to their own choice and then settles the row.
///
/// THE ROW HOLDS NO CONTENT. It names the team (the tenant), the person, what kind of thing arrived and when - never a
/// report id, a title, a question, a factory or who sent it - so nothing it holds can reach an email even by mistake
/// (security review Risk 8: the email only says something is waiting).
///
/// The team IS a tenant, so <c>tenant_id</c> is the team id, and a person removed from the team takes their waiting rows
/// with them (<see cref="Teams.TeamRegistry"/>). Subjects only, never an address. Written and settled only through
/// <see cref="Teams.ContributorNotices"/>. Personally identifying (the subject): never logged.
/// </summary>
public sealed class ContributorNoticeEntity : GatewayMintedKeyEntity
{
    /// <summary>The account subject of the contributor it was sent to.</summary>
    public string RecipientSubject { get; set; } = "";

    /// <summary>What arrived, as <see cref="Teams.ContributorNoticeKinds"/> writes it. For the log and the tests only: it
    /// never reaches the email, which says the same words whatever arrived.</summary>
    public string Kind { get; set; } = "";

    /// <summary>When it arrived (UTC), from the injected clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the person was told, or it was settled without an email (UTC). Null while it waits.</summary>
    public DateTime? SettledAtUtc { get; set; }

    /// <summary>How it was settled, as <see cref="Teams.ContributorNoticeOutcomes"/> writes it. Null while it waits.</summary>
    public string? Outcome { get; set; }

    /// <summary>How many sends of its email have failed in a row.</summary>
    public int FailedTries { get; set; }

    /// <summary>The next send is not tried before this time (UTC), after a failed send. Null when it may go now.</summary>
    public DateTime? NextTryAtUtc { get; set; }
}
