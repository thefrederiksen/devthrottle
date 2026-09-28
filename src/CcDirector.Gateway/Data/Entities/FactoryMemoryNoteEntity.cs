namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE VERSION OF ONE NOTE in a factory's memory, in the <c>factory_memory_notes</c> table (Factory Memory
/// mission, phase 2). A factory's memory is many small named notes, one idea each; this table holds every
/// version of every one of them, and the note a reader sees is simply its highest version.
///
/// APPEND-ONLY. A change never edits a stored row: it adds the next version, and a DELETE adds a version too,
/// which is what makes a delete undoable by a person - the owner's decision of 26 September, taken against the
/// Architect's recommendation, that factory sessions may delete their own factory's notes.
///
/// THE PRIMARY KEY IS THE CONCURRENCY CONTROL, and that is deliberate (review finding 7). The hosted Gateway
/// runs more than one replica over one database, so a lock inside one process cannot stop two writers from both
/// minting version 4 of the same note - the second would silently supersede the first, losing one session's
/// lesson, which is exactly the LESSONS.md failure this mission exists to end. With (tenant, factory, name,
/// version) as the key, the second insert cannot exist: the database refuses it, and the caller is told to
/// re-read and merge.
///
/// Sizes are capped on the CURRENT text rather than on the whole history (review finding 6): a cap counting
/// every version would be reached by a daily Scout rewriting one note, and could never be freed, because a
/// delete adds a version too. Versions are pruned instead - the last 50, or 90 days, whichever keeps more.
/// </summary>
public sealed class FactoryMemoryNoteEntity : TenantScopedEntity
{
    /// <summary>The factory whose memory this is - the lower-case factory id (e.g. <c>website-factory</c>).
    /// A session never states this: the Gateway reads it from the caller's own record.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The note's name, one idea per note (e.g. <c>domains</c>, <c>deliverability</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>The version, counting from 1. The note a reader sees is the highest version of that name.</summary>
    public int Version { get; set; }

    /// <summary>The note's text at this version, or null when this version is a DELETE. Null and empty are kept
    /// apart on purpose: an empty note is a note somebody emptied, a deleted one is gone from the listing.</summary>
    public string? Text { get; set; }

    /// <summary>True when this version is a delete. The name still exists and its history is intact; a later
    /// <c>set</c> continues the same chain and brings the note back.</summary>
    public bool Deleted { get; set; }

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c>. Kept because "who changed the memory"
    /// is the first question asked of a note that turned out to be wrong.</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, or the person's identifier. Null when neither is known.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }
}

/// <summary>The two things that can write a factory's memory.</summary>
public static class FactoryMemoryAuthorKinds
{
    public const string Session = "session";
    public const string Person = "person";
}
