using System.Diagnostics;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Tests for <see cref="LaunchService"/> - asserts on the built ProcessStartInfo,
/// no real process spawning.
/// </summary>
public sealed class LaunchServiceTests : IDisposable
{
    // We need a real file to satisfy the existence check in BuildStartInfo.
    private readonly string _tempExe;
    private readonly string _tempBat;
    private readonly LaunchService _svc = new();

    public LaunchServiceTests()
    {
        _tempExe = Path.Combine(Path.GetTempPath(), $"test-launcher-{Guid.NewGuid():N}.exe");
        _tempBat = Path.Combine(Path.GetTempPath(), $"test-launcher-{Guid.NewGuid():N}.bat");
        File.WriteAllText(_tempExe, "placeholder");
        File.WriteAllText(_tempBat, "@echo off");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempExe)) File.Delete(_tempExe); } catch { }
        try { if (File.Exists(_tempBat)) File.Delete(_tempBat); } catch { }
    }

    // -------------------------------------------------------------------------
    // AC1a: GUI launch uses UseShellExecute = true (clean parentage recipe).
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStartInfo_GuiApp_UsesShellExecute()
    {
        var request = new LaunchRequest { Path = _tempExe };

        var psi = _svc.BuildStartInfoFor(request, LaunchPlatform.Windows);

        Assert.True(psi.UseShellExecute, "GUI app must use UseShellExecute=true for clean parentage");
        Assert.Equal(_tempExe, psi.FileName);
    }

    [Fact]
    public void BuildStartInfo_GuiApp_DoesNotSetCreateNoWindow()
    {
        var request = new LaunchRequest { Path = _tempExe };

        var psi = _svc.BuildStartInfo(request);

        // UseShellExecute = true and CreateNoWindow = true are mutually exclusive.
        Assert.False(psi.CreateNoWindow);
    }

    // -------------------------------------------------------------------------
    // AC1b: Headless launch uses UseShellExecute=false + CreateNoWindow=true.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStartInfo_Headless_UsesFalseShellExecuteAndCreateNoWindow()
    {
        var request = new LaunchRequest { Path = _tempExe, Headless = true };

        var psi = _svc.BuildStartInfo(request);

        Assert.False(psi.UseShellExecute, "Headless must NOT use UseShellExecute (no shell association)");
        if (OperatingSystem.IsWindows())
            Assert.True(psi.CreateNoWindow, "Headless must set CreateNoWindow to suppress console");
    }

    // -------------------------------------------------------------------------
    // AC1c: .cmd / .bat files routed through cmd.exe.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStartInfo_CmdFile_RoutedThroughCmdExe()
    {
        var request = new LaunchRequest { Path = _tempBat };

        var psi = _svc.BuildStartInfoFor(request, LaunchPlatform.Windows);

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Contains(_tempBat, psi.Arguments);
        Assert.False(psi.UseShellExecute, "cmd.exe routing must use UseShellExecute=false");
        Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void BuildStartInfo_CmdFile_ArgsAppendedCorrectly()
    {
        var request = new LaunchRequest { Path = _tempBat, Args = "arg1 arg2" };

        var psi = _svc.BuildStartInfoFor(request, LaunchPlatform.Windows);

        Assert.Contains("arg1 arg2", psi.Arguments);
    }

    // -------------------------------------------------------------------------
    // macOS branch: bundles via /usr/bin/open, shell scripts via /bin/bash,
    // plain executables spawned directly, batch files refused.
    //
    // Every platform-specific test names its platform through BuildStartInfoFor
    // rather than reading the machine's, so each one runs - and means the same
    // thing - on Windows, macOS and Linux alike. They used to return early on the
    // "wrong" host, which read as a pass, and on Linux they took the Linux arm
    // and failed against macOS expectations.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStartInfo_Mac_PlainExecutable_SpawnsDirectly()
    {
        var request = new LaunchRequest { Path = _tempExe, Args = "--foo bar" };

        var psi = _svc.BuildStartInfoFor(request, LaunchPlatform.MacOs);

        Assert.Equal(_tempExe, psi.FileName);
        Assert.False(psi.UseShellExecute, "macOS plain executables must spawn directly");
        Assert.Equal("--foo bar", psi.Arguments);
    }

    [Fact]
    public void BuildStartInfo_Mac_AppBundle_RoutedThroughOpen()
    {
        var bundle = Path.Combine(Path.GetTempPath(), $"test-launcher-{Guid.NewGuid():N}.app");
        Directory.CreateDirectory(bundle);
        try
        {
            var request = new LaunchRequest { Path = bundle, Args = "--managed \"two words\"" };

            var psi = _svc.BuildStartInfoFor(request, LaunchPlatform.MacOs);

            Assert.Equal("/usr/bin/open", psi.FileName);
            Assert.Equal(bundle, psi.ArgumentList[0]);
            Assert.Equal("--args", psi.ArgumentList[1]);
            Assert.Equal("--managed", psi.ArgumentList[2]);
            Assert.Equal("two words", psi.ArgumentList[3]);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_Mac_ShellScript_RoutedThroughBash()
    {
        var script = Path.Combine(Path.GetTempPath(), $"test-launcher-{Guid.NewGuid():N}.sh");
        File.WriteAllText(script, "#!/bin/bash\n");
        try
        {
            var request = new LaunchRequest { Path = script, Args = "one two" };

            var psi = _svc.BuildStartInfoFor(request, LaunchPlatform.MacOs);

            Assert.Equal("/bin/bash", psi.FileName);
            Assert.Equal(script, psi.ArgumentList[0]);
            Assert.Equal("one", psi.ArgumentList[1]);
            Assert.Equal("two", psi.ArgumentList[2]);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void BuildStartInfo_Mac_BatchFile_Throws()
    {
        var request = new LaunchRequest { Path = _tempBat };

        Assert.Throws<NotSupportedException>(() => _svc.BuildStartInfoFor(request, LaunchPlatform.MacOs));
    }

    // -------------------------------------------------------------------------
    // AC1d: Missing path throws (no silent fallback).
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStartInfo_MissingPath_Throws()
    {
        var request = new LaunchRequest { Path = @"C:\does-not-exist-cc-launcher-test.exe" };

        Assert.Throws<FileNotFoundException>(() => _svc.BuildStartInfo(request));
    }

    [Fact]
    public void BuildStartInfo_EmptyPath_Throws()
    {
        var request = new LaunchRequest { Path = "" };

        Assert.Throws<ArgumentException>(() => _svc.BuildStartInfo(request));
    }

    // -------------------------------------------------------------------------
    // AC1e: Optional Args and Cwd are passed through.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStartInfo_GuiApp_ArgsPassedThrough()
    {
        var request = new LaunchRequest { Path = _tempExe, Args = "--foo bar" };

        var psi = _svc.BuildStartInfoFor(request, LaunchPlatform.Windows);

        Assert.Equal("--foo bar", psi.Arguments);
    }

    [Fact]
    public void BuildStartInfo_GuiApp_CwdPassedThrough()
    {
        var cwd = Path.GetTempPath();
        var request = new LaunchRequest { Path = _tempExe, Cwd = cwd };

        var psi = _svc.BuildStartInfo(request);

        Assert.Equal(cwd, psi.WorkingDirectory);
    }

    [Fact]
    public void BuildStartInfo_NullRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _svc.BuildStartInfo(null!));
    }
}
