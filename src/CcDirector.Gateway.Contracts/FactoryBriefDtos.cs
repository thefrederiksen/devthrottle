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

    /// <summary>
    /// Which kind of version <see cref="Version"/>, <see cref="NewestVersion"/> and <see cref="PublishedVersion"/> are
    /// (<see cref="FactoryBriefVersionKinds"/>): "brief", the seat's own brief versions, or "definition" for a defined
    /// factory, whose brief versions are its definition's versions (Factory Definition, phase 4).
    /// </summary>
    public string VersionKind { get; set; } = FactoryBriefVersionKinds.Brief;

    /// <summary>
    /// For a defined factory, the Gateway's sentence saying this brief is part of the factory's definition and that a
    /// publish publishes the whole definition; null otherwise. Printed and drawn as it is.
    /// </summary>
    public string? Definition { get; set; }

    /// <summary>For a brief saved into a definition: the whole-definition check's notices on the saved version, each
    /// with its file and line. Empty otherwise.</summary>
    public List<FactoryDefinitionFindingDto> Findings { get; set; } = new();
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

    /// <summary>Which kind of version every version on the page is (<see cref="FactoryBriefVersionKinds"/>). For a
    /// defined factory the page holds the definition versions in which this seat's brief changed.</summary>
    public string VersionKind { get; set; } = FactoryBriefVersionKinds.Brief;

    /// <summary>For a defined factory, the Gateway's sentence that the brief is part of the definition; null otherwise.</summary>
    public string? Definition { get; set; }
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

/// <summary>
/// WHICH KIND OF VERSION A BRIEF VERSION IS (Factory Definition, phase 4). A seat of a factory with no published
/// definition has its own brief versions, held by the brief store; a seat of a DEFINED factory has none of its own -
/// its brief is part of the definition, and the version recorded is the definition's. Everywhere a brief version is
/// recorded or shown, this word goes beside it so a reader never takes a definition version for a brief version.
/// </summary>
public static class FactoryBriefVersionKinds
{
    /// <summary>A version of the seat's own brief, in the brief store.</summary>
    public const string Brief = "brief";

    /// <summary>A version of the factory's definition, whose seat file holds the brief.</summary>
    public const string Definition = "definition";
}
