using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Pins the storage root against the defect that made the Director unlaunchable on Linux.
///
/// On Linux, Environment.GetFolderPath(SpecialFolder.LocalApplicationData) returns an EMPTY STRING when
/// the per-user data directory does not yet exist - which is the state of every fresh account. The old
/// code did Path.Combine(localAppData, "cc-director") and never checked the result. Path.Combine does not
/// fail on an empty first segment; it returns the RELATIVE path "cc-director", which resolves against the
/// process working directory. In the shipped Linux build that directory holds the executable, and the
/// executable is itself named "cc-director" - so the storage root landed on the binary and the Director
/// aborted at startup creating "cc-director/logs/director".
///
/// The ".exe" suffix means those names can never collide on Windows, so no Windows test could have caught
/// this and none did. These tests drive the resolution directly with the values the platform actually
/// hands it, so the Linux case is reproducible from any operating system.
///
/// ResolveDefaultBase takes its inputs as parameters rather than reading the environment, so these tests
/// need no CC_DIRECTOR_ROOT mutation and are safe to run in parallel with the rest of the suite.
/// </summary>
public sealed class CcStorageDefaultRootTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-storage-root-test-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // -- The regression guard --------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveDefaultBase_BlankLocalAppData_NeverReturnsARelativePath(string? blankLocalAppData)
    {
        var home = NewTempDir();

        var root = CcStorage.ResolveDefaultBase(blankLocalAppData, xdgDataHome: null, home: home);

        Assert.True(Path.IsPathRooted(root), $"storage root must be absolute, got '{root}'");
        Assert.NotEqual("cc-director", root); // the exact relative value the old code produced
    }

    [Fact]
    public void ResolveDefaultBase_BlankLocalAppDataAndHomeSet_UsesXdgDefaultLocalShare()
    {
        var home = NewTempDir();

        var root = CcStorage.ResolveDefaultBase(localAppData: "", xdgDataHome: null, home: home);

        Assert.Equal(Path.Combine(home, ".local", "share", "cc-director"), root);
    }

    [Fact]
    public void ResolveDefaultBase_XdgDataHomeSet_PrefersItOverHome()
    {
        var home = NewTempDir();
        var xdg = NewTempDir();

        var root = CcStorage.ResolveDefaultBase(localAppData: "", xdgDataHome: xdg, home: home);

        Assert.Equal(Path.Combine(xdg, "cc-director"), root);
    }

    [Fact]
    public void ResolveDefaultBase_DataDirectoryAbsent_CreatesIt()
    {
        var home = NewTempDir();
        Assert.False(Directory.Exists(home), "precondition: the fresh-account state, nothing on disk yet");

        var root = CcStorage.ResolveDefaultBase(localAppData: "", xdgDataHome: null, home: home);

        Assert.True(Directory.Exists(root), "a missing data directory is the normal state of a fresh account, not an error");
    }

    [Fact]
    public void ResolveDefaultBase_NothingResolvable_ThrowsNamingTheOverride()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CcStorage.ResolveDefaultBase(localAppData: "", xdgDataHome: null, home: null));

        // The message has to tell the user the one thing they can do about it.
        Assert.Contains("CC_DIRECTOR_ROOT", ex.Message);
    }

    // -- The unchanged happy path ----------------------------------------------------------------

    [Fact]
    public void ResolveDefaultBase_LocalAppDataPresent_AppendsCcDirectorAndDoesNotTouchDisk()
    {
        var localAppData = NewTempDir();

        var root = CcStorage.ResolveDefaultBase(localAppData, xdgDataHome: null, home: null);

        Assert.Equal(Path.Combine(localAppData, "cc-director"), root);
        // The Windows path must stay side-effect free: it resolved fine for years without creating anything.
        Assert.False(Directory.Exists(root));
    }
}
