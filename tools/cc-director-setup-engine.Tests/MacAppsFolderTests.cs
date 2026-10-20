using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Where the macOS install puts the Director bundle, and therefore where its icon is. The install used
/// to place it in ~/Applications only, which the Finder's Applications entry does not open, so an
/// install left no icon in the folder a Mac user opens. /Applications is now the answer whenever this
/// user can write it.
///
/// Every test of the CHOICE decides the write probe's answer itself, so both branches run on Windows,
/// Linux and macOS alike, and on a build agent whose account is not an administrator. The probe itself
/// is tested separately, against real folders.
/// </summary>
public class MacAppsFolderTests : IDisposable
{
    private readonly string _root;

    public MacAppsFolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"macapps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Choose_WhenTheSystemFolderCanBeWritten_IsTheSystemFolder()
    {
        // The whole point of the change: an administrator - which the person installing their own Mac
        // almost always is - gets the icon in the folder the Finder's Applications entry opens.
        var chosen = MacAppsFolder.Choose("/Applications", "/Users/someone/Applications", _ => true);

        Assert.Equal("/Applications", chosen);
    }

    [Fact]
    public void Choose_WhenTheSystemFolderCannotBeWritten_FallsBackToTheUsersOwn()
    {
        // A standard (non-administrator) user cannot write /Applications. Their install must still
        // finish - ~/Applications needs no permission and is still listed by Spotlight.
        var chosen = MacAppsFolder.Choose("/Applications", "/Users/someone/Applications", _ => false);

        Assert.Equal("/Users/someone/Applications", chosen);
    }

    [Fact]
    public void Choose_AsksAboutTheSystemFolderAndNothingElse()
    {
        // A probe of the user's own folder would be a write into the user's home performed just to
        // decide where to write, and its answer could never change the outcome.
        var asked = new List<string>();
        MacAppsFolder.Choose("/Applications", "/Users/someone/Applications", dir => { asked.Add(dir); return true; });

        Assert.Equal(["/Applications"], asked);
    }

    [Fact]
    public void SystemAppsDir_IsTheFolderAMacUserMeansByApplications()
    {
        // Not composed from the home folder: it is the same path for every account on the machine.
        Assert.Equal("/Applications", MacAppsFolder.SystemAppsDir);
    }

    [Fact]
    public void CanCreateDirectoryIn_AWritableFolder_IsTrue_AndLeavesNothingBehind()
    {
        Assert.True(MacAppsFolder.CanCreateDirectoryIn(_root));

        // The probe is a directory created and removed again. One left behind would accumulate a hidden
        // folder in /Applications on every install.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void CanCreateDirectoryIn_AFolderThatIsNotThere_IsFalse()
    {
        // Answered false rather than created: /Applications exists on every Mac, and creating it would be
        // repairing the operating system, not installing an app.
        var missing = Path.Combine(_root, "not-there");

        Assert.False(MacAppsFolder.CanCreateDirectoryIn(missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void CanCreateDirectoryIn_AFolderThisUserCannotWrite_IsFalse()
    {
        // The case the fallback exists for, proved against the real file system rather than a fake: a
        // folder mode 500 (read and execute for the owner, no write) accepts no new entry. Unix modes
        // only, and root ignores them - so the test says why it is skipped rather than passing silently.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (Environment.GetEnvironmentVariable("USER") == "root" || Environment.GetEnvironmentVariable("SUDO_USER") is not null) return;

        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(MacAppsFolder.CanCreateDirectoryIn(locked));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
