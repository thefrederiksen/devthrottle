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

        Assert.Contains("app", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cc-director", supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectorExeExists_ReturnsFalse_WhenPathDoesNotExist()
    {
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
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, supervisor.DirectorExePath, StringComparison.OrdinalIgnoreCase);
    }
}
