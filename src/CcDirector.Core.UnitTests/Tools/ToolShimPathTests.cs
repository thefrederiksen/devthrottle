using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.UnitTests.Tools;

/// <summary>
/// The rule the Director uses at start-up to put <c>~/.local/bin</c> on its own PATH, and so on the PATH
/// of every session it starts (issue devthrottle_internal#2474). Pure: no test changes this process's PATH.
/// </summary>
public sealed class ToolShimPathTests
{
    private static readonly string Sep = Path.PathSeparator.ToString();
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "home", ".local", "bin");

    [Fact]
    public void WithDirectory_Missing_PrependsIt()
    {
        var original = string.Join(Sep, "/usr/bin", "/bin");

        var updated = ToolShimPath.WithDirectory(original, Dir);

        Assert.Equal(Dir + Sep + original, updated);
    }

    [Fact]
    public void WithDirectory_AlreadyPresent_ReturnsItUnchanged()
    {
        var original = string.Join(Sep, "/usr/bin", Dir, "/bin");

        var updated = ToolShimPath.WithDirectory(original, Dir);

        Assert.Equal(original, updated);
    }

    [Fact]
    public void WithDirectory_PresentWithTrailingSeparator_ReturnsItUnchanged()
    {
        var original = string.Join(Sep, "/usr/bin", Dir + Path.DirectorySeparatorChar);

        var updated = ToolShimPath.WithDirectory(original, Dir);

        Assert.Equal(original, updated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithDirectory_NoPath_IsJustTheDirectory(string? original)
    {
        Assert.Equal(Dir, ToolShimPath.WithDirectory(original, Dir));
    }

    [Fact]
    public void Contains_DirectoryOnlyAsPrefixOfAnother_IsFalse()
    {
        var original = string.Join(Sep, Dir + "-other", "/bin");

        Assert.False(ToolShimPath.Contains(original, Dir));
    }

    [Fact]
    public void UnixShimDir_IsDotLocalBinUnderTheHomeFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, ".local", "bin"), ToolShimPath.UnixShimDir);
    }
}
