using System.Diagnostics;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests the bounded-wait behavior of <see cref="ProcessRunner"/> (issue #577): a process that exceeds its
/// timeout is killed and returns a loud failure with the partial output, rather than blocking forever.
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public void Run_ProcessExceedingTimeout_IsKilledAndReturnsTimeoutFailure()
    {
        // Arrange: a deliberately-slow command that sleeps far longer than the tiny test timeout.
        // (Windows: "ping" with a delay is a dependency-free way to make a process linger.)
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
            : ("/bin/sh", "-c \"sleep 30\"");
        var timeout = TimeSpan.FromMilliseconds(500);

        // Act
        var sw = Stopwatch.StartNew();
        var (exit, output) = ProcessRunner.Run(exe, args, onStdoutLine: null, timeout);
        sw.Stop();

        // Assert: it returned promptly (killed, not blocked for 30s), with the timeout sentinel + a clear message.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Run did not honor the timeout (took {sw.Elapsed.TotalSeconds:F1}s).");
        Assert.Equal(ProcessRunner.TimeoutExitCode, exit);
        Assert.Contains("TIMEOUT", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_FastProcessWithinTimeout_ReturnsRealExitCode()
    {
        // A process that finishes well within the bound returns its real exit code, not the timeout sentinel.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c exit 0")
            : ("/bin/sh", "-c \"exit 0\"");

        var (exit, _) = ProcessRunner.Run(exe, args, onStdoutLine: null, TimeSpan.FromSeconds(30));

        Assert.Equal(0, exit);
        Assert.NotEqual(ProcessRunner.TimeoutExitCode, exit);
    }
}
