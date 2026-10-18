using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.UnitTests.Tools;

/// <summary>
/// The macOS and Linux install layout in the tool catalog (issue devthrottle_internal#2474). There the
/// installer never creates the bin directory; each tool is a console script in <c>pyenv/bin</c>, linked
/// from <c>~/.local/bin</c>. Before this, the catalog looked only in bin, so IsBuilt was always false and a
/// Director whose PATH lacked <c>~/.local/bin</c> - one started over SSH - reported all twelve tools
/// "not built" right after a successful repair. These tests give the catalog exactly that PATH.
/// </summary>
public sealed class ToolCatalogUnixLayoutTests : IDisposable
{
    private const string PathWithoutShimDir = "/usr/bin:/bin";
    private readonly string _root;
    private readonly string _binDir;

    public ToolCatalogUnixLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ToolCatalogUnixLayout_" + Guid.NewGuid().ToString("N"));
        // The bin directory is NOT created: a real macOS or Linux install has none.
        _binDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(Path.Combine(_root, "pyenv", "bin"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void GetCatalog_PyenvBinScriptPresent_PathWithoutShimDir_IsBuiltAndAvailable()
    {
        if (OperatingSystem.IsWindows()) return;

        // Arrange
        var script = Path.Combine(_root, "pyenv", "bin", "cc-devthrottle");
        File.WriteAllText(script, "#!/bin/sh\n");

        // Act
        var tool = new ToolCatalogService(_binDir, PathWithoutShimDir, null).GetCatalog()
            .Single(d => d.Name == "cc-devthrottle");

        // Assert
        Assert.True(tool.IsBuilt);
        Assert.False(tool.IsOnPath);
        Assert.True(tool.IsAvailable);
        Assert.True(tool.IsExpected);
        Assert.Equal(script, tool.BinaryPath);
    }

    [Fact]
    public void GetCatalog_NoPyenvScript_PathWithoutShimDir_IsNotAvailable()
    {
        if (OperatingSystem.IsWindows()) return;

        // Act
        var tool = new ToolCatalogService(_binDir, PathWithoutShimDir, null).GetCatalog()
            .Single(d => d.Name == "cc-devthrottle");

        // Assert
        Assert.False(tool.IsBuilt);
        Assert.False(tool.IsAvailable);
    }

    [Fact]
    public void GetCatalog_WindowsStyleScriptsDirectory_IsNotReadOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        // Arrange: a pyenv\Scripts exe is the Windows layout and means nothing on macOS or Linux.
        Directory.CreateDirectory(Path.Combine(_root, "pyenv", "Scripts"));
        File.WriteAllText(Path.Combine(_root, "pyenv", "Scripts", "cc-devthrottle.exe"), "stub");

        // Act
        var tool = new ToolCatalogService(_binDir, PathWithoutShimDir, null).GetCatalog()
            .Single(d => d.Name == "cc-devthrottle");

        // Assert
        Assert.False(tool.IsBuilt);
    }

    [Fact]
    public void GetCatalog_NativeBinaryAndPyenvScriptBothPresent_NativeWins()
    {
        if (OperatingSystem.IsWindows()) return;

        // Arrange
        Directory.CreateDirectory(_binDir);
        var native = Path.Combine(_binDir, "cc-devthrottle");
        File.WriteAllText(native, "stub");
        File.WriteAllText(Path.Combine(_root, "pyenv", "bin", "cc-devthrottle"), "#!/bin/sh\n");

        // Act
        var tool = new ToolCatalogService(_binDir, PathWithoutShimDir, null).GetCatalog()
            .Single(d => d.Name == "cc-devthrottle");

        // Assert
        Assert.Equal(native, tool.BinaryPath);
    }
}
