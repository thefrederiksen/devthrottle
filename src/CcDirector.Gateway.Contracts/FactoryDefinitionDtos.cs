namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One version of a factory's definition held on the Gateway (Factory Definition mission, phase 1): who wrote it and
/// when, its state, and - on a read of one version - its content, a map of path to text.
/// </summary>
public sealed class FactoryDefinitionDto
{
    /// <summary>The factory this is the definition of.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The version this is, counting from 1.</summary>
    public int Version { get; set; }

    /// <summary><c>draft</c> or <c>published</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c>.</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, or the person's identifier.</summary>
    public string? AuthorId { get; set; }

    /// <summary>The change note the writer gave, or null.</summary>
    public string? ChangeNote { get; set; }

    /// <summary>The version this one is a copy of, when it was made by a restore; otherwise null.</summary>
    public int? RestoredFrom { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>When this version was published (UTC), or null while it is a draft.</summary>
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>The identifier of the person who published it, when it is known.</summary>
    public string? PublishedBy { get; set; }

    /// <summary>How many bytes the version's files take together, as UTF-8.</summary>
    public int ContentBytes { get; set; }

    /// <summary>How many files the version holds.</summary>
    public int FileCount { get; set; }

    /// <summary>
    /// The factory's newest version when this answer was made. It is what a save states as the version it read, and
    /// when it is higher than <see cref="Version"/> something newer than this version exists.
    /// </summary>
    public int NewestVersion { get; set; }

    /// <summary>The factory's published version when this answer was made, or null when none is published yet.</summary>
    public int? PublishedVersion { get; set; }

    /// <summary>
    /// The version's content, path to text in ordinal order of the paths. Present on a read of one version; null on
    /// the answer to a write, which the writer already holds.
    /// </summary>
    public Dictionary<string, string>? Files { get; set; }

    /// <summary>On the answer to an import only: true when the import made the factory's first version, so the
    /// factory's definition was created by it; false when a definition already existed. Null on every other answer.</summary>
    public bool? Created { get; set; }

    /// <summary>
    /// What the whole-definition check noticed on a save, a publish or a restore: notices only, because an error
    /// refuses the write. Empty on a read.
    /// </summary>
    public List<FactoryDefinitionFindingDto> Findings { get; set; } = new();

    /// <summary>
    /// On a publish or a restore: what changes against the version that was published before it (everything, on the
    /// first publish), so the person sees what they just did. Null on a save and on a read.
    /// </summary>
    public FactoryDefinitionChangesDto? Changes { get; set; }
}

/// <summary>
/// One thing the whole-definition check found. <see cref="File"/> is a path in the definition; <see cref="Line"/> is
/// 1-based, or 0 when the finding is about the file as a whole. <see cref="Message"/> is one plain sentence.
/// </summary>
public sealed class FactoryDefinitionFindingDto
{
    /// <summary><c>error</c> (refuses the write) or <c>notice</c> (never refuses).</summary>
    public string Severity { get; set; } = "";

    public string File { get; set; } = "";

    public int Line { get; set; }

    public string Message { get; set; } = "";
}

/// <summary>
/// One version in a page of a factory's history: everything about it EXCEPT its content. The content is read one
/// version at a time, from <c>GET /factory-definitions/{factory}/versions/{version}</c>.
/// </summary>
public sealed class FactoryDefinitionVersionSummaryDto
{
    public int Version { get; set; }

    /// <summary><c>draft</c> or <c>published</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c>.</summary>
    public string AuthorKind { get; set; } = "";

    public string? AuthorId { get; set; }

    public string? ChangeNote { get; set; }

    public int? RestoredFrom { get; set; }

    public DateTime WrittenAtUtc { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public string? PublishedBy { get; set; }

    public int ContentBytes { get; set; }

    public int FileCount { get; set; }
}

/// <summary>
/// ONE PAGE of one factory's history, newest first, without any version's content. Every version is kept for ever,
/// so the history is read a page at a time and the content one version at a time.
/// </summary>
public sealed class FactoryDefinitionHistoryResponse
{
    public string Factory { get; set; } = "";

    /// <summary>The factory's published version, or null when none has been published yet.</summary>
    public int? PublishedVersion { get; set; }

    /// <summary>The factory's newest version, or 0 when it has no definition on the Gateway.</summary>
    public int NewestVersion { get; set; }

    /// <summary>How many versions the factory has in all, on every page together.</summary>
    public int Total { get; set; }

    /// <summary>This page's versions, newest first.</summary>
    public List<FactoryDefinitionVersionSummaryDto> Versions { get; set; } = new();

    /// <summary>
    /// The value to send as <c>before</c> for the next, older page, or null when this page reaches the oldest
    /// version. The Gateway decides whether there is more; a reader never works it out for itself.
    /// </summary>
    public int? NextBefore { get; set; }
}

/// <summary>What changes from one version of a definition to another, as <c>GET .../diff</c> answers it.</summary>
public sealed class FactoryDefinitionDiffResponse
{
    public string Factory { get; set; } = "";

    /// <summary>The version compared from, or null when nothing is published yet and no version was named.</summary>
    public int? From { get; set; }

    /// <summary>The version compared to.</summary>
    public int To { get; set; }

    /// <summary>What changes, every sentence composed on the Gateway.</summary>
    public FactoryDefinitionChangesDto Changes { get; set; } = new();
}

/// <summary>
/// Body of a save: the whole definition (path to text), a change note, and THE VERSION THE WRITER LAST READ. Zero
/// means "I believe this factory has no definition on the Gateway yet". A save whose expected version is not the
/// factory's newest is refused with the newest version attached.
/// </summary>
public sealed class SaveFactoryDefinitionDraftRequest
{
    public int ExpectedVersion { get; set; }

    public string? Note { get; set; }

    public Dictionary<string, string>? Content { get; set; }
}

/// <summary>Body of a person's publish: which version becomes the factory's published definition.</summary>
public sealed class PublishFactoryDefinitionRequest
{
    public int Version { get; set; }
}

/// <summary>Body of a person's restore: which older version to publish a copy of as the newest version.</summary>
public sealed class RestoreFactoryDefinitionRequest
{
    /// <summary>The version whose content to restore.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The factory's newest version the person was looking at when they decided, on the same terms as a save. Zero
    /// means "I did not check", and the copy is then added after whatever is newest.
    /// </summary>
    public int ExpectedVersion { get; set; }
}

/// <summary>
/// A factory's definition as a folder of files (Factory Definition mission, phase 3): what
/// <c>GET /factory-definitions/{factory}/export</c> answers and the command line writes to disk, byte for byte.
/// </summary>
public sealed class FactoryDefinitionExportDto
{
    /// <summary>The factory exported.</summary>
    public string Factory { get; set; } = "";

    /// <summary>
    /// <c>definition</c> when the files are one stored version, exactly as stored; <c>live</c> when the factory has no
    /// definition and the files were written from what the Gateway holds today (its registry entry, published briefs,
    /// schedules and triggers).
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>The version exported, when <see cref="Source"/> is <c>definition</c>; otherwise null.</summary>
    public int? Version { get; set; }

    /// <summary>That version's state, <c>draft</c> or <c>published</c>, when <see cref="Source"/> is <c>definition</c>.</summary>
    public string? State { get; set; }

    /// <summary>One sentence saying what was exported, composed by the Gateway.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Every file, path to text, in ordinal order of the paths.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What the factory has today that the folder does not hold, each with the file it would have gone in.
    /// Always empty for a stored version, which is exported whole.</summary>
    public List<FactoryDefinitionExportReportDto> Reported { get; set; } = new();
}

/// <summary>One thing an export could not carry into the folder: the file it would have gone in, and why.</summary>
public sealed class FactoryDefinitionExportReportDto
{
    public string File { get; set; } = "";

    public string Message { get; set; } = "";
}

/// <summary>
/// Body of an import (phase 3): a folder's files, checked exactly as a draft save is and stored as the next draft of
/// the factory its factory.yaml names, never published.
/// </summary>
public sealed class ImportFactoryDefinitionRequest
{
    public string? Note { get; set; }

    public Dictionary<string, string>? Content { get; set; }
}

/// <summary>The definition's JSON Schemas, as the Gateway's check enforces them (phase 3).</summary>
public sealed class FactoryDefinitionSchemasDto
{
    /// <summary>One sentence on what the schemas cover and what only the whole check decides.</summary>
    public string Note { get; set; } = "";

    public List<FactoryDefinitionSchemaDto> Schemas { get; set; } = new();
}

/// <summary>One file type's schema.</summary>
public sealed class FactoryDefinitionSchemaDto
{
    /// <summary>The schema's own file name, such as <c>seat.schema.json</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The paths in a definition this schema checks, such as <c>seats/&lt;seat&gt;.yaml</c>.</summary>
    public string Applies { get; set; } = "";

    /// <summary>The schema itself, a JSON Schema (draft 2020-12) document, exactly as embedded in the Gateway.</summary>
    public System.Text.Json.JsonElement Schema { get; set; }
}
