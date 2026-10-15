namespace CcDirector.Gateway.Contracts;

// THE WIRE SHAPES OF A FACTORY'S RECORDS (Factory Definition mission, phase 2, section 5.6): its journals, its
// working documents and its attachments, held on the Gateway beside its memory and its activity record.

/// <summary>One journal entry, with its text.</summary>
public sealed class FactoryJournalEntryDto
{
    /// <summary>The entry's id, minted by the Gateway.</summary>
    public Guid Id { get; set; }

    /// <summary>The factory the entry belongs to.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat whose journal this is, or null for the factory's own journal.</summary>
    public string? Seat { get; set; }

    /// <summary>The day the entry is about, <c>yyyy-MM-dd</c>.</summary>
    public string Date { get; set; } = "";

    /// <summary>The entry's whole text. Absent in a listing, which never carries text.</summary>
    public string? Text { get; set; }

    /// <summary>How many bytes the text takes as UTF-8.</summary>
    public int TextBytes { get; set; }

    /// <summary>The attachments the entry names, in the order they were given.</summary>
    public List<Guid> AttachmentIds { get; set; } = new();

    /// <summary>Who wrote it: <c>session</c>, <c>person</c> or <c>upload</c>.</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, the person's identifier, or the machine an upload ran on.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When the Gateway stored it (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>For an entry the one-time upload stored: the file it came from, relative to the machine's local
    /// application data folder. Null for an entry written in the ordinary way. An entry has no name of its own, so
    /// this is how an uploaded day stays findable by where it was kept.</summary>
    public string? Source { get; set; }
}

/// <summary>A page of a factory's journal, newest day first, without any entry's text.</summary>
public sealed class FactoryJournalListResponse
{
    public string Factory { get; set; } = "";

    public List<FactoryJournalEntryDto> Entries { get; set; } = new();

    /// <summary>How many entries match in all. More than <see cref="Entries"/> holds means narrow the days.</summary>
    public int Total { get; set; }
}

/// <summary>Add one journal entry. Every field but <see cref="Text"/> may be left out.</summary>
public sealed class AddFactoryJournalEntryRequest
{
    /// <summary>The seat whose journal it goes in; left out for the factory's own journal.</summary>
    public string? Seat { get; set; }

    /// <summary>The day it is about, <c>yyyy-MM-dd</c>; left out for today by the Gateway's clock.</summary>
    public string? Date { get; set; }

    public string? Text { get; set; }

    /// <summary>The attachments it names - files the factory already holds.</summary>
    public List<Guid>? AttachmentIds { get; set; }

    /// <summary>The one-time upload's name for the file this entry came from. Only the upload sends one.</summary>
    public string? ImportKey { get; set; }
}

/// <summary>One version of a working document. <see cref="Text"/> is absent in a list or a history.</summary>
public sealed class FactoryDocumentDto
{
    public string Factory { get; set; } = "";

    /// <summary>The document's name, a relative path such as <c>outreach/ledger.json</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The version this is, counting from 1.</summary>
    public int Version { get; set; }

    /// <summary>The document's whole text at this version, or null where the answer carries no text.</summary>
    public string? Text { get; set; }

    /// <summary>How many bytes the text takes as UTF-8.</summary>
    public int TextBytes { get; set; }

    /// <summary>Who wrote this version: <c>session</c>, <c>person</c> or <c>upload</c>.</summary>
    public string AuthorKind { get; set; } = "";

    public string? AuthorId { get; set; }

    public DateTime WrittenAtUtc { get; set; }
}

/// <summary>A factory's working documents as they stand: the newest version of each, without text.</summary>
public sealed class FactoryDocumentListResponse
{
    public string Factory { get; set; } = "";

    public List<FactoryDocumentDto> Documents { get; set; } = new();

    /// <summary>The most one version of a document may take, in bytes.</summary>
    public int MaxDocumentBytes { get; set; }
}

/// <summary>One page of one document's history, newest first, without any version's text.</summary>
public sealed class FactoryDocumentHistoryResponse
{
    public string Factory { get; set; } = "";

    public string Name { get; set; } = "";

    public List<FactoryDocumentDto> Versions { get; set; } = new();

    /// <summary>How many versions the document has in all.</summary>
    public int Total { get; set; }

    /// <summary>The version to ask for the next page below, or null when this page reaches version 1.</summary>
    public int? NextBefore { get; set; }
}

/// <summary>Write a working document, stating the version last read.</summary>
public sealed class WriteFactoryDocumentRequest
{
    /// <summary>The document's name, a relative path such as <c>outreach/ledger.json</c>.</summary>
    public string? Name { get; set; }

    public string? Text { get; set; }

    /// <summary>The version the writer last read: 0 when it believes the document does not exist yet.</summary>
    public int? ExpectedVersion { get; set; }

    /// <summary>The one-time upload's name for the file. Only the upload sends one.</summary>
    public string? ImportKey { get; set; }
}

/// <summary>What one attachment is. <see cref="ContentBase64"/> is present only when one attachment is read.</summary>
public sealed class FactoryAttachmentDto
{
    public Guid Id { get; set; }

    public string Factory { get; set; } = "";

    /// <summary>The seat that kept it, or null for the factory as a whole.</summary>
    public string? Seat { get; set; }

    /// <summary>The file's name, a relative path such as <c>journal/2026-10-05/01-home.png</c>.</summary>
    public string Name { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long Bytes { get; set; }

    /// <summary>The SHA-256 of the bytes, lower-case hexadecimal.</summary>
    public string Sha256 { get; set; } = "";

    public string AuthorKind { get; set; } = "";

    public string? AuthorId { get; set; }

    public DateTime WrittenAtUtc { get; set; }

    /// <summary>The file's bytes, base64, when one attachment is read; null in a listing.</summary>
    public string? ContentBase64 { get; set; }
}

/// <summary>A factory's attachments, newest first, without their bytes.</summary>
public sealed class FactoryAttachmentListResponse
{
    public string Factory { get; set; } = "";

    public List<FactoryAttachmentDto> Attachments { get; set; } = new();

    /// <summary>How many attachments match in all.</summary>
    public int Total { get; set; }

    /// <summary>How many bytes of attachments the factory holds together.</summary>
    public long FactoryBytes { get; set; }

    /// <summary>The most one attachment may hold, in bytes.</summary>
    public int MaxAttachmentBytes { get; set; }

    /// <summary>The most the factory's attachments may hold together, in bytes.</summary>
    public long MaxFactoryBytes { get; set; }
}

/// <summary>Keep one file.</summary>
public sealed class AddFactoryAttachmentRequest
{
    /// <summary>The seat that keeps it; left out for the factory as a whole.</summary>
    public string? Seat { get; set; }

    /// <summary>The file's name, a relative path.</summary>
    public string? Name { get; set; }

    /// <summary>The file's media type, such as <c>image/png</c>.</summary>
    public string? ContentType { get; set; }

    /// <summary>The file's bytes, base64.</summary>
    public string? ContentBase64 { get; set; }

    /// <summary>The one-time upload's name for the file. Only the upload sends one.</summary>
    public string? ImportKey { get; set; }
}

/// <summary>
/// What a write to a factory's records did: <c>Written</c>, or <c>AlreadyStored</c> when the one-time upload sent a
/// file it had stored before, unchanged. Exactly one of the three records is present - the one that was written.
/// </summary>
public sealed class FactoryRecordWriteResponse
{
    public string Outcome { get; set; } = "";

    public FactoryJournalEntryDto? Entry { get; set; }

    public FactoryDocumentDto? Document { get; set; }

    public FactoryAttachmentDto? Attachment { get; set; }
}
