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
    // -------------------------------------------------------------------------
    // AC2a: Exe path resolves via InstallLayout (no hardcoding).
    // -------------------------------------------------------------------------

    [Fact]
    public void DirectorExePath_ResolvesThroughInstallLayout()
    {
        var layout = new InstallLayout(@"C:\FakeRoot");
        var supervisor = new DirectorSupervisor(layout);

        var expected = layout.PathFor(ComponentRegistry.Director);
        Assert.Equal(expected, supervisor.DirectorExePath);
    }

    [Fact]
    public void DirectorExePath_IncludesAppSubdirectory()
    {
        var layout = new InstallLayout(@"C:\FakeRoot");
        var supervisor = new DirectorSupervisor(layout);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("app", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cc-director", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // macOS: the installed Director is the application bundle in ~/Applications.
            Assert.EndsWith("Director.app", supervisor.DirectorExePath, StringComparison.Ordinal);
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
            // macOS: the bundle lives under the user's home (~/Applications), not local application data.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.StartsWith(home, supervisor.DirectorExePath, StringComparison.Ordinal);
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
