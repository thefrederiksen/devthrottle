namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE VERSION OF ONE WORKING DOCUMENT of a factory, in the <c>factory_document_versions</c> table (Factory
/// Definition mission, phase 2, section 5.6). A working document is a named file a seat reads and rewrites: the
/// outreach ledger, the playbook, <c>focus.md</c>, the health log. Until this table each one lived only under
/// <c>%LOCALAPPDATA%\cc-factory\</c> on one machine, with no copy anywhere else.
///
/// APPEND-ONLY, AND THE PRIMARY KEY IS THE CONCURRENCY CONTROL, as it is for factory memory
/// (<see cref="FactoryMemoryNoteEntity"/>): the hosted Gateway runs more than one replica over one database, so a
/// lock in one process cannot stop two runs from both writing version 4 of the ledger. With (tenant, factory,
/// name, version) as the key the second insert cannot exist, and its writer is refused WITH THE CURRENT TEXT so it
/// can merge. That is the owner's "never a row deleted" ledger kept true across two runs at once.
///
/// EVERY VERSION IS KEPT. The cap is on one version's text, never on the history.
/// </summary>
public sealed class FactoryDocumentVersionEntity : TenantScopedEntity
{
    /// <summary>The factory the document belongs to - the lower-case factory id.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The document's name: a relative path such as <c>outreach/ledger.json</c>, exactly as written.</summary>
    public string Name { get; set; } = "";

    /// <summary>The version, counting from 1. The document a reader sees is its highest version.</summary>
    public int Version { get; set; }

    /// <summary>The document's whole text at this version, exactly as it was sent.</summary>
    public string Text { get; set; } = "";

    /// <summary>How many bytes <see cref="Text"/> takes as UTF-8, written once with the text so the list and the
    /// history can say how big each version is without reading any text.</summary>
    public int TextBytes { get; set; }

    /// <summary>Who wrote this version: <c>session</c>, <c>person</c> or <c>upload</c> (<see cref="FactoryRecordAuthorKinds"/>).</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, the person's identifier, or the machine an upload ran on.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>The one-time upload's own name for the file this version came from, or null for an ordinary write.</summary>
    public string? ImportKey { get; set; }
}
