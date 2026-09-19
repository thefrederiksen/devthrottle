namespace CcDirector.Reclaim.Scanning;

/// <summary>
/// What one entry in a directory listing is, as far as a disk scan is concerned. The scan counts
/// each kind separately because each kind answers a different question about disk space.
/// </summary>
public enum FileSystemEntryKind
{
    /// <summary>An ordinary file whose bytes are on this disk. Its length is added to the bytes seen.</summary>
    RegularFile,

    /// <summary>An ordinary directory. The scan walks into it.</summary>
    Directory,

    /// <summary>
    /// A link or a junction - a reparse point that names another place. The scan counts it and stops
    /// there: it never walks through one, because the bytes on the other side belong to wherever they
    /// really live and counting them here would count them twice.
    /// </summary>
    Link,

    /// <summary>
    /// A file whose content lives in a cloud store and not on this disk - a cloud placeholder. It is
    /// counted separately, and the length its directory entry claims is NOT added to the bytes seen,
    /// because none of those bytes occupy this disk.
    /// </summary>
    CloudPlaceholderFile
}
