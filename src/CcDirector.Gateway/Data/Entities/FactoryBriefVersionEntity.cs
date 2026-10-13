namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE VERSION OF ONE SEAT'S BRIEF, in the <c>factory_seat_briefs</c> table (Factory Seats mission, phase 2a).
/// A seat's brief is what the seat is told to do. Until this table it lived only as a file in a repository on one
/// computer, so renaming a seat or changing what it does took a merged pull request, and the owner's screen could
/// not show it for a seat with no schedule.
///
/// APPEND-ONLY, AND THE PRIMARY KEY IS THE CONCURRENCY CONTROL, for the reason factory memory is
/// (<see cref="FactoryMemoryNoteEntity"/>): the hosted Gateway runs more than one replica over one database, so a
/// lock inside one process cannot stop two writers from both minting version 4 of the same brief. With
/// (tenant, factory, seat, version) as the key the second insert cannot exist, and its writer is told to read
/// again and merge.
///
/// A VERSION'S TEXT IS NEVER EDITED. The one change a stored row ever sees is the mark that it was published -
/// set once, by a person, and never taken off. Bringing an older version back does not move that mark backwards:
/// it adds a copy of the old text as the newest version, already published, so the history keeps showing what
/// happened.
///
/// EVERY VERSION IS KEPT (the mission's goal 5). The size cap is on one version's text, never on the history.
/// </summary>
public sealed class FactoryBriefVersionEntity : TenantScopedEntity
{
    /// <summary>The registered factory the seat belongs to - the lower-case factory id.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat whose brief this is - the lower-case seat id of the factory's registration.</summary>
    public string Seat { get; set; } = "";

    /// <summary>The version, counting from 1.</summary>
    public int Version { get; set; }

    /// <summary>The brief's whole text at this version, exactly as it was sent.</summary>
    public string Text { get; set; } = "";

    /// <summary><see cref="FactoryBriefStates.Draft"/> or <see cref="FactoryBriefStates.Published"/>. The brief a
    /// talk or a run is given is the highest version that is published.</summary>
    public string State { get; set; } = FactoryBriefStates.Draft;

    /// <summary>Who wrote this version: <c>session</c> or <c>person</c> (<see cref="FactoryMemoryAuthorKinds"/>).</summary>
    public string AuthorKind { get; set; } = "";

    /// <summary>The writing session's id, or the person's identifier. Null when it is not known.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When this version was written (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>When this version was published (UTC), or null while it is a draft.</summary>
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>The identifier of the person who published it, when it is known. Only a person publishes.</summary>
    public string? PublishedBy { get; set; }
}

/// <summary>The two states a version of a brief can be in.</summary>
public static class FactoryBriefStates
{
    public const string Draft = "draft";
    public const string Published = "published";
}
