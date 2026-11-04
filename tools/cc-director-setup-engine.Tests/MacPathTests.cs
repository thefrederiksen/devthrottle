using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// A Mac path reads the same on every machine the tests run on. Before <see cref="MacPath"/>, the Mac decisions went
/// through <see cref="Path"/>, which follows the machine: on Windows the folder of <c>/Users/someone/Applications/Director.app</c>
/// came back with backslashes and a Dock address threw, so ten MacAppNameAndPlaceTests failed there while passing on a Mac.
/// </summary>
public sealed class MacPathTests
{
    [Theory]
    [InlineData("/Users/someone/Applications/Director.app", "/Users/someone/Applications")]
    [InlineData("/Applications/Director.app", "/Applications")]
    [InlineData("/Director.app", "/")]
    [InlineData("Director.app", "")]
    public void DirectoryOf_AMacPath_IsTheFolderWithForwardSlashes(string path, string expected)
    {
        Assert.Equal(expected, MacPath.DirectoryOf(path));
    }

    [Theory]
    [InlineData("/Users/someone/Applications/Director Dev.app", "Director Dev.app")]
    [InlineData("/Applications/Director.app", "Director.app")]
    [InlineData("Director.app", "Director.app")]
    [InlineData("", "")]
    public void FileNameOf_AMacPath_IsItsLastPart(string path, string expected)
    {
        Assert.Equal(expected, MacPath.FileNameOf(path));
    }

    [Theory]
    [InlineData("/Applications/Director.app/", "file:///Applications/Director.app/")]
    [InlineData("/Users/some one/Applications/Director.app/", "file:///Users/some%20one/Applications/Director.app/")]
    [InlineData("/Users/a#b/Director.app/", "file:///Users/a%23b/Director.app/")]
    public void FileUrlOf_AMacPath_IsItsEscapedFileAddress(string path, string expected)
    {
        Assert.Equal(expected, MacPath.FileUrlOf(path));
    }

    [Fact]
    public void FileUrlOf_APathNotAtTheRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => MacPath.FileUrlOf("Applications/Director.app"));
    }
}
