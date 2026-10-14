namespace CcDirector.Gateway.Contracts;

/// <summary>One version of a seat's brief held on the Gateway (Factory Seats mission, phase 2a).</summary>
public sealed class FactoryBriefDto
{
    /// <summary>The registered factory the seat belongs to.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat whose brief this is.</summary>
    public string Seat { get; set; } = "";

    /// <summary>The version this is, counting from 1.</summary>
    public int Version { get; set; }

    /// <summary>The brief's whole text at this version.</summary>
    public string Text { get; set; } = "";

    /// <summary><c>draft</c> or <c>published</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c>.</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, or the person's identifier.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>When this version was published (UTC), or null while it is a draft.</summary>
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>The identifier of the person who published it, when it is known.</summary>
    public string? PublishedBy { get; set; }

    /// <summary>
    /// The seat's newest version when this answer was made. It is what a save states as the version it read, and
    /// when it is higher than <see cref="Version"/> something newer than this version exists.
    /// </summary>
    public int NewestVersion { get; set; }

    /// <summary>The version a talk or a run of the seat is given when this answer was made, or null when no
    /// version has been published yet.</summary>
    public int? PublishedVersion { get; set; }
}

/// <summary>
/// One version in a page of a seat's history: everything about it EXCEPT its text. The text is read one version at
/// a time, from <c>GET /factory-briefs/{factory}/{seat}/versions/{version}</c>.
/// </summary>
public sealed class FactoryBriefVersionSummaryDto
{
    /// <summary>The version this is, counting from 1.</summary>
    public int Version { get; set; }

    /// <summary><c>draft</c> or <c>published</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c>.</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, or the person's identifier.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>When this version was published (UTC), or null while it is a draft.</summary>
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>The identifier of the person who published it, when it is known.</summary>
    public string? PublishedBy { get; set; }

    /// <summary>How many bytes this version's text takes as UTF-8.</summary>
    public int TextBytes { get; set; }
}

/// <summary>
/// ONE PAGE of one seat's history, newest first, without any version's text. Every version is kept for ever, so the
/// history is read a page at a time and the text one version at a time.
/// </summary>
public sealed class FactoryBriefHistoryResponse
{
    public string Factory { get; set; } = "";
    public string Seat { get; set; } = "";

    /// <summary>The version a talk or a run of the seat is given, or null when none has been published yet.</summary>
    public int? PublishedVersion { get; set; }

    /// <summary>The seat's newest version, or 0 when it has no brief on the Gateway.</summary>
    public int NewestVersion { get; set; }

    /// <summary>How many versions the seat has in all, on every page together.</summary>
    public int Total { get; set; }

    /// <summary>This page's versions, newest first.</summary>
    public List<FactoryBriefVersionSummaryDto> Versions { get; set; } = new();

    /// <summary>
    /// The value to send as <c>before</c> for the next, older page, or null when this page reaches the oldest
    /// version. The Gateway decides whether there is more; a reader never works it out for itself.
    /// </summary>
    public int? NextBefore { get; set; }
}

/// <summary>
/// Body of a save: the brief's whole text, and THE VERSION THE WRITER LAST READ. Zero means "I believe this seat
/// has no brief on the Gateway yet". A save whose expected version is not the seat's newest is refused with the
/// newest version attached, so two writers cannot silently overwrite one another.
/// </summary>
public sealed class SaveFactoryBriefDraftRequest
{
    public string Text { get; set; } = "";
    public int ExpectedVersion { get; set; }
}

/// <summary>Body of a person's publish: which version becomes the one talks and runs are given.</summary>
public sealed class PublishFactoryBriefRequest
{
    public int Version { get; set; }
}

/// <summary>Body of a person's bring back: which older version to publish a copy of as the newest version.</summary>
public sealed class BringBackFactoryBriefRequest
{
    /// <summary>The version whose text to bring back.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The seat's newest version the person was looking at when they decided, on the same terms as a save. Zero
    /// means "I did not check", and the copy is then added after whatever is newest.
    /// </summary>
    public int ExpectedVersion { get; set; }
}
