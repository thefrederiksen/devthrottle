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

        var psi = _svc.BuildStartInfo(request);

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
        Assert.True(psi.CreateNoWindow, "Headless must set CreateNoWindow to suppress console");
    }

    // -------------------------------------------------------------------------
    // AC1c: .cmd / .bat files routed through cmd.exe.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildStartInfo_CmdFile_RoutedThroughCmdExe()
    {
        var request = new LaunchRequest { Path = _tempBat };

        var psi = _svc.BuildStartInfo(request);

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Contains(_tempBat, psi.Arguments);
        Assert.False(psi.UseShellExecute, "cmd.exe routing must use UseShellExecute=false");
        Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void BuildStartInfo_CmdFile_ArgsAppendedCorrectly()
    {
        var request = new LaunchRequest { Path = _tempBat, Args = "arg1 arg2" };

        var psi = _svc.BuildStartInfo(request);

        Assert.Contains("arg1 arg2", psi.Arguments);
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

        var psi = _svc.BuildStartInfo(request);

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
