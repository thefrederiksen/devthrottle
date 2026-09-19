using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// Reading a repository's folder name out of a path that was written on some OTHER machine.
///
/// Every case here runs the same on Windows, on macOS and on Linux, and that is the point being tested:
/// the answer comes from the path, never from the host. The Windows cases are the ones that used to break,
/// because <c>Path.GetFileName</c> finds no separator in a drive path when it is not running on Windows and
/// returns the whole thing.
/// </summary>
public class RepositoryPathsTests
{
    [Theory]
    [InlineData(@"D:\ReposFred\devthrottle_internal", "devthrottle_internal")]
    [InlineData(@"D:\ReposFred\devthrottle_internal\", "devthrottle_internal")]
    [InlineData(@"D:/ReposFred/devthrottle_internal", "devthrottle_internal")]
    [InlineData(@"\\fileserver\share\devthrottle", "devthrottle")]
    [InlineData(@"D:\repo", "repo")]
    public void FolderName_WindowsPath_IsTheLastSegment(string path, string expected) =>
        Assert.Equal(expected, RepositoryPaths.FolderName(path));

    [Theory]
    [InlineData("/Users/dev/ReposFred/devthrottle", "devthrottle")]
    [InlineData("/Users/dev/ReposFred/devthrottle/", "devthrottle")]
    [InlineData("/repo", "repo")]
    public void FolderName_PosixPath_IsTheLastSegment(string path, string expected) =>
        Assert.Equal(expected, RepositoryPaths.FolderName(path));

    /// <summary>A bare folder name is already the answer, and surrounding blanks are not part of it.</summary>
    [Theory]
    [InlineData("devthrottle", "devthrottle")]
    [InlineData("  devthrottle  ", "devthrottle")]
    public void FolderName_ANameWithNoDirectories_IsThatName(string path, string expected) =>
        Assert.Equal(expected, RepositoryPaths.FolderName(path));

    /// <summary>Nothing to read answers nothing, rather than a guess the caller cannot tell from a reading.
    /// The caller decides what an empty answer means for it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(@"\")]
    [InlineData("///")]
    public void FolderName_NothingToRead_IsEmpty(string? path) =>
        Assert.Equal("", RepositoryPaths.FolderName(path));
}
