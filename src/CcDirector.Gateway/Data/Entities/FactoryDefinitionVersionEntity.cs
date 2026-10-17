namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE VERSION OF ONE FACTORY'S DEFINITION, in the <c>factory_definition_versions</c> table (Factory Definition
/// mission, phase 1). A factory's definition is everything that says what the factory is: the factory file, one file
/// per seat, its grants, its rule sets, its files, its checks and its own skills. On the Gateway the whole definition
/// is one version row, and its content is a map of path to text.
///
/// BUILT EXACTLY LIKE A SEAT'S BRIEF (<see cref="FactoryBriefVersionEntity"/>), for the same reasons:
///
/// APPEND-ONLY, AND THE PRIMARY KEY IS THE CONCURRENCY CONTROL. The hosted Gateway runs more than one replica over
/// one database, so a lock inside one process cannot stop two writers from both minting version 4 of the same
/// definition. With (tenant, factory, version) as the key the second insert cannot exist, and its writer is told to
/// read again and merge.
///
/// A VERSION'S CONTENT IS NEVER EDITED. The one change a stored row ever sees is the mark that it was published - set
/// once, by a person, and never taken off. Restoring an older version does not move that mark backwards: it adds a
/// copy of the old content as the newest version, already published, and records which version it copied.
///
/// EVERY VERSION IS KEPT. The caps are on one file and on one version, never on the history.
///
/// NOTHING READS THIS TABLE YET. Until phase 4 of the mission a factory runs exactly as it did before.
/// </summary>
public sealed class FactoryDefinitionVersionEntity : TenantScopedEntity
{
    /// <summary>The factory this is the definition of - the lower-case factory id. The factory need not be
    /// registered: importing a definition is how a factory comes to exist (phase 3).</summary>
    public string Factory { get; set; } = "";

    /// <summary>The version, counting from 1.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The whole definition at this version: a JSON object of path to text, its keys sorted ordinal, so the same map
    /// is always the same bytes. Written once and never edited.
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// How many bytes the files take as UTF-8, summed over every file, written once with the content. It is held
    /// beside the content so the history can say how big each version is WITHOUT reading any version's content.
    /// </summary>
    public int ContentBytes { get; set; }

    /// <summary>How many files the content holds, written once with it, for the same reason as
    /// <see cref="ContentBytes"/>.</summary>
    public int FileCount { get; set; }

    /// <summary><see cref="FactoryDefinitionStates.Draft"/> or <see cref="FactoryDefinitionStates.Published"/>. The
    /// published definition is the highest version that is published.</summary>
    public string State { get; set; } = FactoryDefinitionStates.Draft;

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c> (<see cref="FactoryMemoryAuthorKinds"/>).</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, or the person's identifier. Null when it is not known.</summary>
    public string? AuthorId { get; set; }

    /// <summary>What the writer said this version changes, at most 500 characters. Null when nothing was said.</summary>
    public string? ChangeNote { get; set; }

    /// <summary>The version a restore copied this one from, or null for a version that was written, not restored.</summary>
    public int? RestoredFrom { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>When this version was published (UTC), or null while it is a draft.</summary>
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>The identifier of whoever published it, when it is known: the person's identifier, or the publishing
    /// session's id. A person, or a session in no factory, publishes (the owner's ruling of 16 October 2026).</summary>
    public string? PublishedBy { get; set; }

    /// <summary>Who published it: <c>person</c> or <c>session</c> (<see cref="FactoryMemoryAuthorKinds"/>). Null while it
    /// is a draft, and on a version published before this was recorded - every one of those was published by a person.</summary>
    public string? PublishedByKind { get; set; }

    /// <summary>The publishing session's name as it was at the moment of the publish, so the history says which session
    /// it was in words. Null for a person, for a draft, and for a session that had no name.</summary>
    public string? PublishedByName { get; set; }
}

/// <summary>The two states a version of a definition can be in.</summary>
public static class FactoryDefinitionStates
{
    public const string Draft = "draft";
    public const string Published = "published";
}
