namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One dev report (<c>dev_reports</c>, issue #2958): the record an agent publishes for its own session, and
/// that the session's owner reads and answers. Re-publishing the same <see cref="Key"/> for the same session
/// makes a new VERSION of this report rather than a second report, so the key is unique per
/// (tenant, session, key). The bytes of every version live in <see cref="DevReportVersionEntity"/>.
///
/// <see cref="Title"/>, <see cref="Status"/> and <see cref="Version"/> are the LATEST version's, kept here so a
/// list never has to read an HTML column.
/// </summary>
public sealed class DevReportEntity : GatewayMintedKeyEntity
{
    /// <summary>The session that published the report (the calling session key's own id).</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The report key the tool chose - the file's full path. Same key, same report, new version.</summary>
    public string Key { get; set; } = "";

    /// <summary>The latest version's title, read by the Gateway from the parsed document.</summary>
    public string Title { get; set; } = "";

    /// <summary>The latest version's header status: waiting-on-you, agent-working or done.</summary>
    public string Status { get; set; } = "";

    /// <summary>The latest version number, starting at 1.</summary>
    public int Version { get; set; }

    /// <summary>When the first version was published (UTC).</summary>
    public DateTime PublishedAtUtc { get; set; }

    /// <summary>When the latest version was published (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
