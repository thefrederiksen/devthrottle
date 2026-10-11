using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

/// <summary>
/// The Claude Code binary is claude.exe on Windows and plain "claude" on macOS and Linux. The Gateway
/// resolves its agent through <see cref="ClaudeDriver.ResolveExecutable"/>, so this name must follow the
/// platform - a hardcoded claude.exe left the warm brain unable to spawn its agent off Windows. This
/// test drops a file named for the current platform on a temporary PATH and asserts the driver finds it.
/// </summary>
[Collection("ProcessWideState")]
public sealed class ClaudeDriverResolveExecutableTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _savedPath;

    public ClaudeDriverResolveExecutableTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-claude-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _savedPath = Environment.GetEnvironmentVariable("PATH");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _savedPath);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ResolveExecutable_FindsThePlatformBinaryOnPath()
    {
        // The Claude Code binary is claude.exe on Windows (resolved via PATHEXT) and plain "claude" on
        // macOS and Linux (exact extensionless name) - the same rules Codex and Copilot resolve by.
        var exeName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var expected = Path.GetFullPath(Path.Combine(_tempDir, exeName));
        File.WriteAllText(expected, "#!/bin/sh\n");
        Environment.SetEnvironmentVariable("PATH", _tempDir);

        var resolved = new ClaudeDriver().ResolveExecutable(configuredPath: null);

        // Windows PATHEXT resolution returns the extension in PATHEXT's case (.EXE), so compare
        // case-insensitively there; POSIX file names are case-sensitive, so compare exactly.
        Assert.Equal(expected, resolved, ignoreCase: OperatingSystem.IsWindows());
    }
}
