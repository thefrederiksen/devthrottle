namespace CcDirector.Reclaim.Scanning;

/// <summary>
/// Decides what one directory entry is, from the attributes the file system reports for it.
///
/// This is a pure decision with no input and output of its own, so every combination of attributes
/// can be proven exactly, on any platform, without a disk. The walk in <see cref="DirectoryScanner"/>
/// asks this and nothing else; it holds no rules of its own about what an entry is.
/// </summary>
public static class EntryClassifier
{
    /// <summary>
    /// FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS. Set on a file whose content lives in a cloud store and is
    /// fetched when something reads it. It is not in the <see cref="FileAttributes"/> enumeration, so
    /// the bit is named here rather than written as a number at the point of use.
    /// </summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>
    /// FILE_ATTRIBUTE_RECALL_ON_OPEN. The same idea one step earlier: the content is fetched when
    /// something opens the file. Also absent from the <see cref="FileAttributes"/> enumeration.
    /// </summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x40000;

    /// <summary>
    /// The three bits that together mean "the bytes of this file are not on this disk". Windows sets
    /// them; no other platform does, which is why a scan elsewhere simply reports no placeholders
    /// rather than needing any platform specific code here.
    /// </summary>
    public const FileAttributes CloudPlaceholderBits =
        FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen;

    /// <summary>
    /// Classify one entry.
    ///
    /// The order of the two questions matters and is a decision, not an accident.
    ///
    /// For a FILE, the cloud placeholder question is asked first, because a placeholder is also a
    /// reparse point: a cloud file carries both marks, and the one that matters to a disk scan is that
    /// its bytes are elsewhere. This is also what makes an unreadable placeholder a placeholder rather
    /// than an error - the answer is taken from the directory entry, which is on this disk, and the
    /// file is never opened.
    ///
    /// For a DIRECTORY it is the other way round: a cloud-backed folder is walked, because its listing
    /// is held locally and the files under it each answer for themselves. Only a directory that is a
    /// reparse point and is NOT cloud-backed is a link, and a link is where the walk stops.
    /// </summary>
    /// <param name="attributes">The attributes the file system reports for the entry.</param>
    /// <param name="isDirectory">True when the entry is a directory.</param>
    public static FileSystemEntryKind Classify(FileAttributes attributes, bool isDirectory)
    {
        var isCloudPlaceholder = (attributes & CloudPlaceholderBits) != 0;
        var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

        if (isDirectory)
            return isReparsePoint && !isCloudPlaceholder ? FileSystemEntryKind.Link : FileSystemEntryKind.Directory;

        if (isCloudPlaceholder) return FileSystemEntryKind.CloudPlaceholderFile;

        return isReparsePoint ? FileSystemEntryKind.Link : FileSystemEntryKind.RegularFile;
    }
}
