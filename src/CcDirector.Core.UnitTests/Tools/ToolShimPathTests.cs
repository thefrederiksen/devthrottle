using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.UnitTests.Tools;

/// <summary>
/// Where the tool shims live (<see cref="ToolShimPath.ShimDirFor"/>) and the rule the Director uses at start-up to put
/// that directory on its own PATH, and so on the PATH of every session it starts (issue devthrottle_internal#2474).
/// Pure: no test reads the real home folder or changes this process's PATH.
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

    // --- Which machine root the Director's readers ask about (review of pull request 4211) ------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShimDirForRootSetting_NoSetting_IsTheUsersLocalBin(string? setting)
    {
        var dir = ToolShimPath.ShimDirForRootSetting(setting, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(Home, ".local", "bin"), dir);
    }

    [Fact]
    public void ShimDirForRootSetting_ADirectorsInstanceHomeUnderTheDefaultRoot_IsTheUsersLocalBin()
    {
        // The installed Director hands every session its own instance home; that is still the machine's install.
        var setting = Path.Combine(DefaultRoot, "instances", "default");

        var dir = ToolShimPath.ShimDirForRootSetting(setting, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(Home, ".local", "bin"), dir);
    }

    [Fact]
    public void ShimDirForRootSetting_AScratchRoot_IsThatRootsOwnBin()
    {
        var dir = ToolShimPath.ShimDirForRootSetting(ScratchRoot, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(ScratchRoot, "bin"), dir);
    }

    [Fact]
    public void ShimDirForRootSetting_AnInstanceHomeUnderAScratchRoot_IsTheScratchRootsOwnBin()
    {
        // A test Director on a scratch root hands its sessions <scratch>/instances/<slug>; the links it
        // wrote are in <scratch>/bin, so that is where every reader must look.
        var setting = Path.Combine(ScratchRoot, "instances", "slot-5");

        var dir = ToolShimPath.ShimDirForRootSetting(setting, DefaultRoot, Home, isWindows: false);

        Assert.Equal(Path.Combine(ScratchRoot, "bin"), dir);
    }
}
