namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE FILE A FACTORY KEPT - a screenshot, a walk's JSON, a drafted email - in the <c>factory_attachments</c> table
/// (Factory Definition mission, phase 2, section 5.6, decision A5). This row says what the file is; its bytes are a
/// row of <c>factory_attachment_contents</c> (<see cref="FactoryAttachmentContentEntity"/>), so listing a factory's
/// attachments never reads one of them.
///
/// Measured on 14 October 2026 across every factory on the owner's machine: 484 files, 236 MB, the largest 2.69 MB.
/// That is why the bytes have a table of their own rather than living inside a journal row, and why each file and each
/// factory's total are capped.
///
/// WRITTEN ONCE. An attachment is never edited; a changed file is a new attachment.
/// </summary>
public sealed class FactoryAttachmentEntity : GatewayMintedKeyEntity
{
    /// <summary>The factory the file belongs to - the lower-case factory id.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat that kept it, or null for a file kept by the factory as a whole.</summary>
    public string? Seat { get; set; }

    /// <summary>The file's name: a relative path such as <c>journal/2026-10-05/01-home.png</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The file's media type, such as <c>image/png</c>, as the writer stated it.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>How many bytes the file holds.</summary>
    public long Bytes { get; set; }

    /// <summary>The SHA-256 of the bytes, lower-case hexadecimal, so a reader can prove it got what was stored.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Who kept it: <c>session</c>, <c>person</c> or <c>upload</c> (<see cref="FactoryRecordAuthorKinds"/>).</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, the person's identifier, or the machine an upload ran on.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When the Gateway stored it (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>The one-time upload's own name for the file, or null for an ordinary write. Unique within a factory.</summary>
    public string? ImportKey { get; set; }
}

/// <summary>
/// THE BYTES OF ONE ATTACHMENT, in the <c>factory_attachment_contents</c> table, keyed by the attachment's id. Kept
/// apart from <see cref="FactoryAttachmentEntity"/> so that every read of what a factory holds stays small, and the
/// bytes are read only when somebody asks for that one file.
/// </summary>
public sealed class FactoryAttachmentContentEntity : TenantScopedEntity
{
    /// <summary>The attachment these bytes belong to.</summary>
    public Guid AttachmentId { get; set; }

    /// <summary>The file's bytes, exactly as they were sent.</summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();
}
