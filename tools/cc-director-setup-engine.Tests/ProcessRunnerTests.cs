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
    [Trait("Gate", "Quarantine")]
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

    [Trait("Gate", "Quarantine")]
    [Fact]
    public void Run_ShortProcessThatExitsAtOnce_ReturnsAllItsOutput()
    {
        // #3411: the timed wait returned before the pipe readers drained, so a short command's output
        // (codesign -dv on a Mac) came back empty with exit 0. Repeated, because the race was per run.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c (for /L %i in (1,1,9) do @echo line %i) & echo err-line 1>&2")
            : ("/bin/sh", "-c \"for i in 1 2 3 4 5 6 7 8 9; do echo line $i; done; echo err-line 1>&2\"");

        for (var run = 0; run < 20; run++)
        {
            var (exit, output) = ProcessRunner.Run(exe, args, onStdoutLine: null, TimeSpan.FromSeconds(30));

            Assert.Equal(0, exit);
            Assert.Contains("line 9", output, StringComparison.Ordinal);
            Assert.Contains("err-line", output, StringComparison.Ordinal);
        }
    }
}
