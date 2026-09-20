namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One repository the Gateway knows about on one machine. This catalog is deliberately
/// independent of the ninety-day session-history retention window: it answers the mobile repository
/// picker after the source session row has been pruned.
///
/// The catalog holds BOTH halves of the one repository list (the one-repository-list mission,
/// phase 2), in ONE table rather than two:
/// <list type="bullet">
///   <item>the USED half - a repository observed in a session, which carries a
///     <see cref="LastUsedUtc"/>; and</item>
///   <item>the DISCOVERED half - a repository a Director found under one of its registered root
///     folders and nobody has ever opened, whose <see cref="LastUsedUtc"/> is NULL.</item>
/// </list>
/// Null last-used IS "found but never opened"; it is already the Director dialog's own rule
/// (<c>NewSessionDialog.BuildRepositoryList</c> leaves the last-used time unset for a discovered
/// repository). One table, because a repository that is found today and opened tomorrow then stays
/// ONE row that gains a time, rather than becoming a de-duplication problem across two stores.
/// </summary>
public sealed class KnownRepositoryEntity : GatewayMintedKeyEntity
{
    /// <summary>Normalized machine identity used by reads and in-process deduplication.</summary>
    public string MachineKey { get; set; } = "";

    /// <summary>Normalized repository path used by the database observation lookup and deduplication.</summary>
    public string PathKey { get; set; } = "";

    /// <summary>The machine name shown to the user.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The repository path shown to the user and sent when a session is created.</summary>
    public string Path { get; set; } = "";

    /// <summary>The most recently observed non-blank repository name.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The newest session observation for this machine and repository, or NULL when this repository
    /// has only ever been FOUND under a registered root folder and never opened. A discovered
    /// observation never writes, moves or clears this; only a session observation does.
    /// </summary>
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>
    /// The id of the Director that REPORTED this repository under one of its registered root folders,
    /// or null for a row that only a session observation ever created. It is the reconciliation scope
    /// for the discovered half: removing a root folder removes that Director's never-opened rows, and
    /// one Director can never remove another's. It is the id BOUND to the pushing connection, never a
    /// Director id carried in a payload.
    /// </summary>
    public string? DiscoveredByDirectorId { get; set; }

    /// <summary>
    /// When <see cref="DiscoveredByDirectorId"/> last reported this repository in a root-folder scan.
    /// Null for a row no root-folder scan has ever covered.
    /// </summary>
    public DateTime? LastSeenUtc { get; set; }
}
