namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// HOW ONE PERSON WANTS TO BE TOLD THAT SOMETHING IS WAITING (<c>contributor_notice_choices</c>, Team implementation
/// piece 4.4, devthrottle_internal#2801): right away, once a day at a chosen hour, or never. ONE choice for the person, for
/// every team they are a contributor on (mockup.html: "Notifications" is one setting of the person), so the row lives in
/// the person's OWN tenant - the one their phone or browser key is bound to - and follows them into every team.
///
/// No row means the default, right away (owner decision of 8 November: "right away as the default"). Written only through
/// <see cref="Teams.ContributorNotices"/>; one row per tenant.
/// </summary>
public sealed class ContributorNoticeChoiceEntity : GatewayMintedKeyEntity
{
    /// <summary>The choice, as <see cref="Teams.ContributorNoticeModes"/> writes it.</summary>
    public string Mode { get; set; } = "";

    /// <summary>The hour of the day (0 to 23, in the person's own time zone) the daily email goes out.</summary>
    public int DailyHour { get; set; }

    /// <summary>The person's local date (yyyy-MM-dd) whose daily email has been sent, so a day is never mailed twice. It
    /// is set only during the chosen hour, when there is something waiting, just before the send, and a send that failed
    /// and will be tried again puts it back. An hour with nothing waiting leaves it alone, so something that
    /// arrives later in that hour is still sent the same day. Null until the first.</summary>
    public string? DailyHandledLocalDate { get; set; }

    /// <summary>When the person last changed the choice (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
