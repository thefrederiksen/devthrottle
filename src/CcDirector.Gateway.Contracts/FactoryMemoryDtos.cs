namespace CcDirector.Gateway.Contracts;

/// <summary>One note in a factory's memory, as it stands (Factory Memory mission, phase 2).</summary>
public sealed class FactoryMemoryNoteDto
{
    /// <summary>The factory whose memory this note belongs to.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The note's name - one idea per note.</summary>
    public string Name { get; set; } = "";

    /// <summary>The version this is. A writer sends it back on the next write, which is how two writers on one
    /// note are kept from silently overwriting each other.</summary>
    public int Version { get; set; }

    /// <summary>The text, or null when this version is a delete.</summary>
    public string? Text { get; set; }

    /// <summary>True when the note is deleted. It is still restorable by a person.</summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// When the note is deleted, the sentence that says so: which version deleted it, who, when, that a person can
    /// restore it, and that writing the name again continues the same history (phase 2 review, finding 10). Null
    /// when the note is not deleted. It is a sentence rather than a shape so an agent is told what to do next,
    /// and it rides on the note so that one answer serves every reader.
    /// </summary>
    public string? DeletedNotice { get; set; }

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c>.</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, or the person's identifier.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }
}

/// <summary>A factory's notes as they stand. Deleted notes are not listed.</summary>
public sealed class FactoryMemoryListResponse
{
    public string Factory { get; set; } = "";
    public List<FactoryMemoryNoteDto> Notes { get; set; } = new();

    /// <summary>What the notes take together, in bytes, against the factory's cap - so an agent can see it is
    /// running out of room before a write is refused.</summary>
    public long Bytes { get; set; }
    public int MaxBytes { get; set; }
    public int MaxNotes { get; set; }
}

/// <summary>Every kept version of one note, newest first.</summary>
public sealed class FactoryMemoryHistoryResponse
{
    public string Factory { get; set; } = "";
    public string Name { get; set; } = "";
    public List<FactoryMemoryNoteDto> Versions { get; set; } = new();
}

/// <summary>
/// Body of the write: the text, and THE VERSION THE WRITER LAST READ. Zero means "I believe this note does not
/// exist yet". A write whose expected version is not the current one is refused with the current note attached,
/// so two agents that both learned something cannot silently overwrite one another.
/// </summary>
public sealed class SetFactoryMemoryNoteRequest
{
    public string Text { get; set; } = "";
    public int ExpectedVersion { get; set; }
}

/// <summary>Body of the delete: the version the caller last read, on the same terms as a write.</summary>
public sealed class DeleteFactoryMemoryNoteRequest
{
    public int ExpectedVersion { get; set; }
}

/// <summary>Body of a person's restore: which version's text to put back as a new version.</summary>
public sealed class RestoreFactoryMemoryNoteRequest
{
    /// <summary>The version whose text to put back.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The CURRENT version the person was looking at when they decided to restore, on the same terms as a write
    /// (phase 2 review, finding 6). Without it a restore is the one write that cannot lose a race: an agent can
    /// add a version between the owner reading the history and pressing the button, and the restore would bury it
    /// without anybody seeing. Zero means "I did not check", which is accepted so an older client keeps working.
    /// </summary>
    public int ExpectedVersion { get; set; }
}
