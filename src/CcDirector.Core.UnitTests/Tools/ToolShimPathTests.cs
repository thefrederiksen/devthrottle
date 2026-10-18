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

    // --- Where the shims of an install go (mission #4070) -------------------------------------------

    private static readonly string DefaultRoot = Path.Combine(Path.GetTempPath(), "fake-home", ".local", "share", "cc-director");
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "fake-home");
    private static readonly string ScratchRoot = Path.Combine(Path.GetTempPath(), "scratch-root");

    [Fact]
    public void ShimDirFor_DefaultRootOnUnix_IsTheUsersLocalBin()
    {
        var dir = ToolShimPath.ShimDirFor(DefaultRoot, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(Home, ".local", "bin"), dir);
    }

    [Fact]
    public void ShimDirFor_NonDefaultRootOnUnix_IsThatRootsOwnBin()
    {
        var dir = ToolShimPath.ShimDirFor(ScratchRoot, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(ScratchRoot, "bin"), dir);
    }

    [Fact]
    public void ShimDirFor_DefaultRootWrittenWithATrailingSeparator_IsStillTheDefault()
    {
        var dir = ToolShimPath.ShimDirFor(DefaultRoot + Path.DirectorySeparatorChar, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(Home, ".local", "bin"), dir);
    }

    [Fact]
    public void ShimDirFor_ARootInsideTheDefaultRoot_IsNotTheDefault()
    {
        var inside = Path.Combine(DefaultRoot, "rig");

        var dir = ToolShimPath.ShimDirFor(inside, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(inside, "bin"), dir);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShimDirFor_Windows_IsAlwaysTheRootsOwnBin(bool isDefault)
    {
        var root = isDefault ? DefaultRoot : ScratchRoot;

        var dir = ToolShimPath.ShimDirFor(root, DefaultRoot, Home, isWindows: true);

        Assert.Equal(Path.Combine(root, "bin"), dir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShimDirFor_DefaultRootOnUnixWithNoHome_Throws(string? home)
    {
        Assert.Throws<InvalidOperationException>(
            () => ToolShimPath.ShimDirFor(DefaultRoot, DefaultRoot, home, isWindows: false));
    }

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
