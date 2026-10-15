namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE JOURNAL ENTRY of one seat of a factory, in the <c>factory_journal_entries</c> table (Factory Definition
/// mission, phase 2, section 5.6). A seat's journal is what it wrote down about a day's work. Until this table it
/// was a dated Markdown file under <c>%LOCALAPPDATA%\cc-factory\</c> on the one machine the seat ran on.
///
/// APPEND-ONLY, AS THE ACTIVITY RECORD IS (<see cref="FactoryActivityEntity"/>). An entry is written once and never
/// edited or deleted; a wrong entry is corrected by a NEW entry. Nothing here is pruned.
///
/// ATTACHMENTS ARE NAMED, NEVER HELD (decision A5). A screenshot taken during the day is a row of
/// <c>factory_attachments</c> with its bytes in their own table; this row carries only the ids, so reading a
/// journal never reads a megabyte of picture.
/// </summary>
public sealed class FactoryJournalEntryEntity : GatewayMintedKeyEntity
{
    /// <summary>The factory the entry belongs to - the lower-case factory id.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat whose journal this is - the lower-case seat id - or null for a journal kept by the factory
    /// as a whole rather than by one of its seats.</summary>
    public string? Seat { get; set; }

    /// <summary>The day the entry is about, <c>yyyy-MM-dd</c>. Stated by the writer, because a run that finishes
    /// after midnight still writes about the day it started.</summary>
    public string EntryDate { get; set; } = "";

    /// <summary>The entry's whole text, exactly as it was sent.</summary>
    public string Text { get; set; } = "";

    /// <summary>How many bytes <see cref="Text"/> takes as UTF-8, written once with the text so a listing can say how
    /// big each entry is without reading it.</summary>
    public int TextBytes { get; set; }

    /// <summary>The ids of the attachments this entry names, comma separated, in the order the writer gave them;
    /// empty when it names none. Every id was an attachment of the same factory when the entry was written.</summary>
    public string AttachmentIds { get; set; } = "";

    /// <summary>Who wrote it: <c>session</c>, <c>person</c> or <c>upload</c> (<see cref="FactoryRecordAuthorKinds"/>).</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, the person's identifier, or the machine an upload ran on.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When the Gateway stored it (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>
    /// The one-time upload's own name for the file this entry came from, or null for an entry written in the
    /// ordinary way. Unique within a factory, so running the upload twice stores nothing twice.
    /// </summary>
    public string? ImportKey { get; set; }
}

/// <summary>The things that can write a factory's records.</summary>
public static class FactoryRecordAuthorKinds
{
    /// <summary>A session of the factory.</summary>
    public const string Session = FactoryMemoryAuthorKinds.Session;

    /// <summary>A person, from their own device.</summary>
    public const string Person = FactoryMemoryAuthorKinds.Person;

    /// <summary>The one-time upload of a machine's local records (section 5.6).</summary>
    public const string Upload = "upload";
}
