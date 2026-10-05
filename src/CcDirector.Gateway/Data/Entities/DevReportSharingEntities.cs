namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE MEMBER OF A TEAM A DEV REPORT WAS SENT TO (<c>dev_report_recipients</c>, devthrottle_internal#2309). The report's
/// author sends it from the Cockpit; the recipient - a Collaborator above all - reads it on their Reports page. A
/// recipient reads ONLY the reports with a row naming them, so this row is the whole of "sent to them".
///
/// The team IS a tenant, so <c>tenant_id</c> is the team id and the tenant query filter keeps one team's rows from
/// another's. One row per (team, report, recipient): sending again is the same row, moved to the newer version. Personally identifying (the
/// subjects): never logged.
/// </summary>
public sealed class DevReportRecipientEntity : GatewayMintedKeyEntity
{
    /// <summary>The report sent.</summary>
    public Guid ReportId { get; set; }

    /// <summary>The account subject of the member it was sent to.</summary>
    public string RecipientSubject { get; set; } = "";

    /// <summary>The account subject of the member who sent it - the report's author.</summary>
    public string SentBySubject { get; set; } = "";

    /// <summary>
    /// The version of the report this member was sent - and the ONLY version they read (Tech Lead ruling F1). A version the
    /// session publishes afterwards reaches them only when the author sends again, which moves this forward; an earlier
    /// one never reaches them.
    /// </summary>
    public int SentVersion { get; set; }

    /// <summary>When the version they hold was sent (UTC).</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary>When the recipient first opened the version they hold (UTC), or null while it is unread.</summary>
    public DateTime? ReadAtUtc { get; set; }
}

/// <summary>
/// ONE COMMENT A PERSON WROTE ON A DEV REPORT SENT TO THEM (<c>dev_report_comments</c>, devthrottle_internal#2309). It
/// goes to the report's AUTHOR PERSON and to nobody else: it is read only through the author's own account routes and
/// the commenter's own, and it is never handed to dev report delivery, never folded into a prompt, never placed in any
/// session's input, and never readable with a session key. That is the owner's rule for this whole track: a
/// Collaborator's own words never reach an agent. The agent's conversation is <see cref="DevReportItemEntity"/>; this
/// table is deliberately apart from it.
///
/// The team IS a tenant, so <c>tenant_id</c> is the team id. Personally identifying (the subject and the words): the
/// text is never logged.
/// </summary>
public sealed class DevReportCommentEntity : GatewayMintedKeyEntity
{
    /// <summary>The report commented on.</summary>
    public Guid ReportId { get; set; }

    /// <summary>The account subject of the person who wrote the comment.</summary>
    public string FromSubject { get; set; } = "";

    /// <summary>The account subject of the person the comment goes to: the report's author when it was written.</summary>
    public string ToSubject { get; set; } = "";

    /// <summary>The words, exactly as written.</summary>
    public string Text { get; set; } = "";

    /// <summary>When it was written (UTC).</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>
    /// The question the comment was written beside, when it came with an answer on the Questions page
    /// (devthrottle_internal#2307) - so the author reads which question it is about. Null for a comment on the whole
    /// report.
    /// </summary>
    public string? QuestionId { get; set; }
}
