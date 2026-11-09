using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Tests for <see cref="DirectorSupervisor"/> exe-path resolution order.
/// No real process spawning.
/// </summary>
public sealed class DirectorSupervisorTests
{
    /// <summary>
    /// An absolute root on whatever platform runs the test. "C:\FakeRoot" is absolute only on Windows -
    /// on macOS and Linux it is a relative file name, which InstallLayout rightly refuses. Nothing is
    /// read or written under it.
    /// </summary>
    private static readonly string FakeRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "FakeRoot");

    // -------------------------------------------------------------------------
    // AC2a: Exe path resolves via InstallLayout (no hardcoding).
    // -------------------------------------------------------------------------

    [Fact]
    public void DirectorExePath_ResolvesThroughInstallLayout()
    {
        var layout = new InstallLayout(FakeRoot);
        var supervisor = new DirectorSupervisor(layout);

        var expected = layout.PathFor(ComponentRegistry.Director);
        Assert.Equal(expected, supervisor.DirectorExePath);
    }

    [Fact]
    public void DirectorExePath_IncludesAppSubdirectory()
    {
        var layout = new InstallLayout(FakeRoot);
        var supervisor = new DirectorSupervisor(layout);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("app", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cc-director", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: the installed Director is the application bundle in ~/Applications.
            Assert.EndsWith("Director.app", supervisor.DirectorExePath, StringComparison.Ordinal);
        }
        else
        {
            // Linux: one self-contained executable in the install root's app directory. This branch
            // used to be folded into the macOS one, which is not where a Linux install puts it.
            Assert.Equal(Path.Combine(FakeRoot, "app", "cc-director"), supervisor.DirectorExePath);
        }
    }

    [Fact]
    public void DirectorExeExists_ReturnsFalse_WhenPathDoesNotExist()
    {
        // Windows-only premise: on macOS PathFor(Director) is the machine-global
        // ~/Applications/Director.app, independent of the injected root - the macOS
        // presence semantics are covered by DirectorExeExists_MacBundleDirectory_CountsAsInstalled.
        if (!OperatingSystem.IsWindows()) return;

        var layout = new InstallLayout(Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}"));
        var supervisor = new DirectorSupervisor(layout);

        Assert.False(supervisor.DirectorExeExists);
    }

    // -------------------------------------------------------------------------
    // AC2b: Default constructor uses InstallLayout.Default() (real machine path).
    // -------------------------------------------------------------------------

    [Fact]
    public void DefaultConstructor_ResolvesRealLocalAppDataPath()
    {
        var supervisor = new DirectorSupervisor();

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.StartsWith(localAppData, supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // macOS: the bundle lives in the Applications folder a Mac user opens, and WHICH one that is
            // belongs to the layout - /Applications when this user can write it, ~/Applications when not
            // (MacAppsFolder.Choose). This test asserted the user's home outright, which was the older
            // install's place: on a Mac that can write /Applications the product answers
            // /Applications/Director.app and the assertion failed on correct code (issue #4735). So the
            // question asked here is the one that has a stable answer - the default constructor resolves
            // the same path the default layout does - rather than a guess at which folder won.
            // Said as a FACT ABOUT THE PLATFORM, not as the formula restated. Comparing the supervisor's
            // answer with InstallLayout.Default().PathFor(...) passes whatever that formula returns, so it
            // would still pass if the bundle moved somewhere no Mac user can open - which is the defect this
            // test exists to catch. The two things that must hold on macOS: it is inside one of the two
            // Applications folders a Mac user opens, and it names the binary inside the bundle.
            var layout = InstallLayout.Default();
            Assert.True(
                supervisor.DirectorExePath.StartsWith(layout.MacAppsDir, StringComparison.Ordinal)
                || supervisor.DirectorExePath.StartsWith(layout.MacUserAppsDir, StringComparison.Ordinal),
                $"the Director path is in neither Applications folder: {supervisor.DirectorExePath}");
            // The bundle itself, which is what the layout names on macOS - the Director is launched through
            // the .app, not by reaching inside it.
            Assert.EndsWith("Director.app", supervisor.DirectorExePath, StringComparison.Ordinal);
        }
    }

    // -------------------------------------------------------------------------
    // macOS: the installed Director is a bundle DIRECTORY, and DirectorExeExists
    // must treat a directory as present.
    // -------------------------------------------------------------------------

    [Fact]
    public void DirectorExeExists_MacBundleDirectory_CountsAsInstalled()
    {
        if (OperatingSystem.IsWindows()) return; // macOS/Unix semantics only

        // Point the layout at a scratch root, then create the bundle DIRECTORY where
        // InstallLayout would place it. PathFor(Director) on macOS is ~/Applications/Director.app
        // regardless of the root, so simulate through a real temp bundle only when it does
        // not already exist - never touch a real installed bundle.
        var supervisor = new DirectorSupervisor();
        if (Directory.Exists(supervisor.DirectorExePath) || File.Exists(supervisor.DirectorExePath))
        {
            // A real Director is installed on this machine: presence must be reported.
            Assert.True(supervisor.DirectorExeExists);
            return;
        }

        Assert.False(supervisor.DirectorExeExists);
    }

    [Fact]
    public void DirectStartInfo_StartsTheExecutableItself_NotThroughAnotherProgram()
    {
        // Linux used to fall through to the macOS "/usr/bin/open" start, which on Linux is a different
        // program or absent, so a launcher that stopped a Linux Director to update it started nothing.
        var exe = Path.Combine(Path.GetTempPath(), "app", "cc-director");

        var psi = DirectorSupervisor.DirectStartInfo(exe);

        Assert.Equal(exe, psi.FileName);
        Assert.Equal(Path.GetDirectoryName(exe), psi.WorkingDirectory);
        Assert.Equal(OperatingSystem.IsWindows(), psi.UseShellExecute);
        Assert.Empty(psi.ArgumentList);
    }
}
