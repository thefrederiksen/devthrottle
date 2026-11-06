using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// The rule that replaces the <c>cd "$DIR"</c> the macOS bundle's bash wrapper used to run before
/// exec'ing the Director (issue #4518). launchd gives a desktop launch the file-system root as its
/// working directory, and every caller of <c>CurrentOrAppDirectory</c> reads that successfully and
/// hands it on - so a new session's working directory defaulted to <c>/</c>.
/// </summary>
public class ProcessWorkingDirectoryTests
{
    [Fact]
    public void IsFilesystemRoot_TheRootItself_IsRecognised()
    {
        // Asked of the running platform's own root rather than written as "/", so this says the same
        // thing on the Windows machine continuous integration runs on.
        var root = Path.GetPathRoot(Path.GetFullPath(Path.DirectorySeparatorChar.ToString()));

        Assert.True(ProcessWorkingDirectory.IsFilesystemRoot(root));
    }

    [Fact]
    public void IsFilesystemRoot_TheRootWrittenTheLongWayRound_IsStillTheRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.DirectorySeparatorChar.ToString()))!;
        var roundabout = Path.Combine(root, "usr", "..");

        Assert.True(ProcessWorkingDirectory.IsFilesystemRoot(roundabout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsFilesystemRoot_NoDirectoryAtAll_IsNotTheRoot(string? directory)
    {
        // Nothing to move out of, and nothing to guess from either.
        Assert.False(ProcessWorkingDirectory.IsFilesystemRoot(directory));
    }

    [Fact]
    public void IsFilesystemRoot_AnOrdinaryDirectory_IsLeftAlone()
    {
        // A Director started from a terminal is already somewhere deliberate.
        Assert.False(ProcessWorkingDirectory.IsFilesystemRoot(Path.GetTempPath()));
        Assert.False(ProcessWorkingDirectory.IsFilesystemRoot(AppContext.BaseDirectory));
    }
}
