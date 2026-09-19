namespace CcDirector.Reclaim.Scanning;

/// <summary>A link or junction the scan found and did not follow.</summary>
/// <param name="Path">The full path of the link.</param>
/// <param name="IsDirectory">True when the link stands where a directory would.</param>
/// <param name="Target">What the link names, when the file system will say; null otherwise.</param>
public sealed record FoundLink(string Path, bool IsDirectory, string? Target);

/// <summary>A folder the scan could not list, and why.</summary>
/// <param name="Path">The full path of the folder.</param>
/// <param name="Refusal">Why the listing was refused.</param>
/// <param name="RefusalCode">The numeric code the file system gave, or zero.</param>
public sealed record RefusedFolder(string Path, DirectoryReadRefusal Refusal, int RefusalCode);

/// <summary>What one folder holds, counting everything the scan saw beneath it.</summary>
/// <param name="Path">The full path of the folder.</param>
/// <param name="Depth">How far below the scan root the folder sits; the root's own children are depth one.</param>
/// <param name="BytesSeen">Bytes of ordinary files seen in this folder and every folder under it.</param>
/// <param name="FilesSeen">Ordinary files seen in this folder and every folder under it.</param>
public sealed record FolderTotal(string Path, int Depth, long BytesSeen, long FilesSeen);

/// <summary>
/// What the volume the scan root sits on says about itself. This is the other side of the unseen-gap
/// line: the scan says what it saw, and this says what the volume counts as used.
/// </summary>
/// <param name="Available">True when the volume answered. False when it would not.</param>
/// <param name="Name">The volume the root sits on, as the operating system names it.</param>
/// <param name="TotalBytes">The size of the volume.</param>
/// <param name="UsedBytes">What the volume counts as used: its size less its free space.</param>
/// <param name="FreeBytes">The free space on the volume.</param>
/// <param name="UnavailableReason">
/// Plain ASCII words for why the volume would not answer, or null when it did.
/// </param>
public sealed record VolumeUsage(
    bool Available,
    string Name,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    string? UnavailableReason);

/// <summary>
/// Everything one scan saw, and everything it could not see. This is the whole product of a scan: the
/// saved index holds exactly this, and the report is built from it without going back to the disk.
/// </summary>
public sealed record ScanResult
{
    /// <summary>The path the scan was asked to walk, in its canonical full form.</summary>
    public required string RootPath { get; init; }

    /// <summary>True when the scan root is the root of its volume, so the unseen gap covers the whole volume.</summary>
    public required bool RootIsVolumeRoot { get; init; }

    /// <summary>When the walk started, in coordinated universal time.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>When the walk finished, in coordinated universal time.</summary>
    public required DateTimeOffset FinishedUtc { get; init; }

    /// <summary>How long the walk took, in seconds.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>Ordinary files seen. Cloud placeholders and links are counted on their own lines.</summary>
    public required long FilesSeen { get; init; }

    /// <summary>
    /// Folders seen, not counting the scan root itself. A folder that refused its listing is counted
    /// here too: the scan saw the folder, it saw nothing inside it, and it names it below.
    /// </summary>
    public required long FoldersSeen { get; init; }

    /// <summary>The bytes of every ordinary file seen. Placeholders and links add nothing to this.</summary>
    public required long BytesSeen { get; init; }

    /// <summary>Cloud placeholder files seen.</summary>
    public required long PlaceholderFiles { get; init; }

    /// <summary>
    /// The bytes those placeholders claim. They are held in a cloud store and occupy nothing on this
    /// disk, which is exactly why they are kept apart from <see cref="BytesSeen"/>.
    /// </summary>
    public required long PlaceholderBytesInCloud { get; init; }

    /// <summary>Every link and junction found, ordered by path. None of them were followed.</summary>
    public required IReadOnlyList<FoundLink> Links { get; init; }

    /// <summary>Every folder that refused its listing, named, ordered by path.</summary>
    public required IReadOnlyList<RefusedFolder> RefusedFolders { get; init; }

    /// <summary>
    /// Folder totals, for every folder down to the depth the scan was asked to record. The report
    /// picks the largest from this list; the list itself is not ordered.
    /// </summary>
    public required IReadOnlyList<FolderTotal> FolderTotals { get; init; }

    /// <summary>What the volume says about itself.</summary>
    public required VolumeUsage Volume { get; init; }
}
