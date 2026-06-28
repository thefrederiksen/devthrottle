using System.Runtime.InteropServices;
using CcDirector.Core.Backends;
using CcDirector.Core.UnixPty;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Regression tests for the macOS/Linux PTY backend. The original implementation
/// spawned the child with redirected pipes and never attached the PTY subordinate, so the
/// child's stdin was not a terminal -- which made Claude Code drop into --print mode
/// and exit with "Input must be provided either through stdin or as a prompt argument".
/// The child MUST see a real TTY on stdin.
/// </summary>
public class UnixPtyBackendTests
{
    private static bool OnUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task Start_ChildStdin_IsATty()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        using var backend = new UnixPtyBackend();
        // `test -t 0` succeeds only when stdin is a terminal.
        backend.Start("/bin/sh", "-c \"test -t 0 && echo TTY_YES || echo TTY_NO\"",
            Path.GetTempPath(), 120, 30);

        var output = await WaitForOutputAsync(backend, "TTY_", TimeSpan.FromSeconds(5));

        Assert.Contains("TTY_YES", output);
        Assert.DoesNotContain("TTY_NO", output);
    }

    [Fact]
    public async Task Start_ChildSeesRequestedWindowSize()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        using var backend = new UnixPtyBackend();
        // `stty size` prints "<rows> <cols>" from the kernel winsize. Regression for the
        // arm64 macOS variadic-ioctl bug, where TIOCSWINSZ wrote garbage (e.g. 62608x28302),
        // making Claude Code draw a 28000-wide rule that wrapped into a wall of lines.
        backend.Start("/bin/sh", "-c \"stty size\"", Path.GetTempPath(), 120, 30);

        var output = await WaitForOutputAsync(backend, "30 120", TimeSpan.FromSeconds(5));

        Assert.Contains("30 120", output);
    }

    [Fact]
    public async Task Start_ChildReceivesWorkingDirectory()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        var tempDir = Path.Combine(Path.GetTempPath(), "ccd-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var backend = new UnixPtyBackend();
            backend.Start("/bin/sh", "-c pwd", tempDir, 120, 30);

            var output = await WaitForOutputAsync(backend, Path.GetFileName(tempDir), TimeSpan.FromSeconds(5));

            // macOS may resolve /var -> /private/var; compare on the unique leaf name.
            Assert.Contains(Path.GetFileName(tempDir), output);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task<string> WaitForOutputAsync(UnixPtyBackend backend, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var text = backend.Buffer?.DumpAll() is { Length: > 0 } bytes
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : string.Empty;
            if (text.Contains(marker))
                return text;
            await Task.Delay(50);
        }
        return backend.Buffer?.DumpAll() is { Length: > 0 } final
            ? System.Text.Encoding.UTF8.GetString(final)
            : string.Empty;
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("--session-id abc123", new[] { "--session-id", "abc123" })]
    [InlineData("  --resume   1234   ", new[] { "--resume", "1234" })]
    [InlineData("-c \"echo hello world\"", new[] { "-c", "echo hello world" })]
    [InlineData("--path '/some/dir with spaces/x'", new[] { "--path", "/some/dir with spaces/x" })]
    public void TokenizeArgs_SplitsCorrectly(string input, string[] expected)
    {
        var actual = UnixProcessHost.TokenizeArgs(input);
        Assert.Equal(expected, actual);
    }
}
