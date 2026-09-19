namespace CcDirector.Reclaim.Scanning;

/// <summary>What one scan is asked to do.</summary>
public sealed record ScanOptions
{
    /// <summary>The smallest folder depth a scan may record totals for.</summary>
    public const int MinimumFolderDepth = 1;

    /// <summary>
    /// The largest folder depth a scan may record totals for. It is capped so a saved index stays a
    /// bounded size whatever it is pointed at: a whole volume recorded to unlimited depth would hold a
    /// row for every folder on the disk, and the index is read by a screen.
    /// </summary>
    public const int MaximumFolderDepth = 10;

    /// <summary>The default depth: the scan root's children and their children.</summary>
    public const int DefaultFolderDepth = 2;

    /// <summary>The folder to walk.</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// How far below the root folder totals are recorded. The root's own children are depth one.
    /// </summary>
    public int MaxFolderDepth { get; init; } = DefaultFolderDepth;
}
