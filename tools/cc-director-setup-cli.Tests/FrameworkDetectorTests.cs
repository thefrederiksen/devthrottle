using CcDirector.Setup.Cli;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

/// <summary>
/// Exercises PATH lookup by pointing PATH at a temp dir with a known launcher.
/// Mutates process env, so these run serially (no xUnit parallelism within a class).
/// </summary>
public class FrameworkDetectorTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _origPath;
    private readonly string? _origPathExt;

    public FrameworkDetectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-fw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _origPath = Environment.GetEnvironmentVariable("PATH");
        _origPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        Environment.SetEnvironmentVariable("PATH", _dir);
        Environment.SetEnvironmentVariable("PATHEXT", ".EXE;.CMD;.BAT");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _origPath);
        Environment.SetEnvironmentVariable("PATHEXT", _origPathExt);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void FindOnPath_FindsLauncherWithExtension()
    {
        File.WriteAllText(Path.Combine(_dir, "claude.cmd"), "@echo hi");
        var found = FrameworkDetector.FindOnPath("claude");
        Assert.NotNull(found);
        Assert.EndsWith("claude.cmd", found, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindOnPath_FindsBareLauncher()
    {
        File.WriteAllText(Path.Combine(_dir, "codex"), "#!/bin/sh");
        var found = FrameworkDetector.FindOnPath("codex");
        Assert.NotNull(found);
    }

    [Fact]
    public void FindOnPath_NullWhenAbsent()
    {
        Assert.Null(FrameworkDetector.FindOnPath("definitely-not-installed-xyz"));
    }

    [Fact]
    public void Detect_ReportsNameAndPresence()
    {
        File.WriteAllText(Path.Combine(_dir, "claude.exe"), "x");
        var status = FrameworkDetector.Detect("claude");
        Assert.Equal("claude", status.Name);
        Assert.True(status.Found);
        Assert.NotNull(status.Location);
    }
}
