using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// What one entry is, decided from its attributes alone. These run on every platform and cover every
/// combination that matters, including the two that decide whether the walk stops or goes on.
/// </summary>
public class EntryClassifierTests
{
    [Fact]
    public void Classify_OrdinaryFile_IsRegularFile()
    {
        Assert.Equal(
            FileSystemEntryKind.RegularFile,
            EntryClassifier.Classify(FileAttributes.Archive, isDirectory: false));
    }

    [Fact]
    public void Classify_OrdinaryDirectory_IsDirectory()
    {
        Assert.Equal(
            FileSystemEntryKind.Directory,
            EntryClassifier.Classify(FileAttributes.Directory, isDirectory: true));
    }

    [Fact]
    public void Classify_FileThatIsAReparsePoint_IsLink()
    {
        Assert.Equal(
            FileSystemEntryKind.Link,
            EntryClassifier.Classify(FileAttributes.Archive | FileAttributes.ReparsePoint, isDirectory: false));
    }

    [Fact]
    public void Classify_DirectoryThatIsAReparsePoint_IsLink()
    {
        Assert.Equal(
            FileSystemEntryKind.Link,
            EntryClassifier.Classify(FileAttributes.Directory | FileAttributes.ReparsePoint, isDirectory: true));
    }

    [Fact]
    public void Classify_FileMarkedOffline_IsCloudPlaceholder()
    {
        Assert.Equal(
            FileSystemEntryKind.CloudPlaceholderFile,
            EntryClassifier.Classify(FileAttributes.Archive | FileAttributes.Offline, isDirectory: false));
    }

    [Fact]
    public void Classify_FileMarkedRecallOnDataAccess_IsCloudPlaceholder()
    {
        Assert.Equal(
            FileSystemEntryKind.CloudPlaceholderFile,
            EntryClassifier.Classify(FileAttributes.Archive | EntryClassifier.RecallOnDataAccess, isDirectory: false));
    }

    [Fact]
    public void Classify_FileMarkedRecallOnOpen_IsCloudPlaceholder()
    {
        Assert.Equal(
            FileSystemEntryKind.CloudPlaceholderFile,
            EntryClassifier.Classify(FileAttributes.Archive | EntryClassifier.RecallOnOpen, isDirectory: false));
    }

    /// <summary>
    /// A cloud file carries both marks. It must be counted as a placeholder, because what matters to a
    /// disk scan is that its bytes are not on the disk - and because reading it as a link would put it
    /// on the list of things the scan refused to follow, which is not what happened.
    /// </summary>
    [Fact]
    public void Classify_CloudFileThatIsAlsoAReparsePoint_IsCloudPlaceholderAndNotALink()
    {
        Assert.Equal(
            FileSystemEntryKind.CloudPlaceholderFile,
            EntryClassifier.Classify(
                FileAttributes.Archive | FileAttributes.ReparsePoint | EntryClassifier.RecallOnDataAccess,
                isDirectory: false));
    }

    /// <summary>
    /// The other half of the same decision, and the one that decides whether a whole tree is counted.
    /// A cloud-backed FOLDER is walked: its listing is held on this disk, and every file under it
    /// answers for itself. Reading it as a link would stop the walk at the top of the user's cloud
    /// folder, which on the machine this was designed for is hundreds of gigabytes.
    /// </summary>
    [Fact]
    public void Classify_CloudBackedDirectoryThatIsAlsoAReparsePoint_IsDirectorySoTheWalkGoesOn()
    {
        Assert.Equal(
            FileSystemEntryKind.Directory,
            EntryClassifier.Classify(
                FileAttributes.Directory | FileAttributes.ReparsePoint | EntryClassifier.RecallOnDataAccess,
                isDirectory: true));
    }

    [Fact]
    public void Classify_DirectoryMarkedOfflineWithNoReparsePoint_IsDirectory()
    {
        Assert.Equal(
            FileSystemEntryKind.Directory,
            EntryClassifier.Classify(FileAttributes.Directory | FileAttributes.Offline, isDirectory: true));
    }
}
