using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The launchd diagnostics the macOS launcher step now shows and reports instead of throwing away.
/// The sample is the shape launchctl print gives for a job that crashed and is waiting out launchd's
/// respawn throttle - the state that left the installer with no process id and nothing to say.
/// </summary>
public sealed class LaunchdDiagnosticsTests
{
    private const string CrashedJob = """
        gui/501/com.devthrottle.cc-launcher = {
            active count = 0
            path = /Users/robert/Library/LaunchAgents/com.devthrottle.cc-launcher.plist
            state = not running

            program = /Users/robert/Library/Application Support/cc-director/launcher/cc-launcher
            arguments = {
                /Users/robert/Library/Application Support/cc-director/launcher/cc-launcher
                --managed
            }

            runs = 2
            last exit code = 134: Abort trap
            spawn type = interactive (4)
            job state = exited
            properties = runatload | inferred program
        }
        """;

    [Fact]
    public void UsefulLines_KeepsTheFieldsThatExplainAMissingProcess()
    {
        var lines = LaunchdDiagnostics.UsefulLines(CrashedJob);

        Assert.Contains("state = not running", lines);
        Assert.Contains("runs = 2", lines);
        Assert.Contains("last exit code = 134: Abort trap", lines);
        Assert.Contains("job state = exited", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("properties", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("active count", StringComparison.Ordinal));
    }

    [Fact]
    public void Explain_NamesTheExitCodeRunsAndState()
    {
        var sentence = LaunchdDiagnostics.Explain(CrashedJob, jobLoaded: true);

        Assert.NotNull(sentence);
        Assert.Contains("exited with code 134: Abort trap", sentence);
        Assert.Contains("started it 2 time(s)", sentence);
        Assert.Contains("\"not running\"", sentence);
    }

    [Fact]
    public void Explain_PrefersTheTerminatingSignalOverTheExitCode()
    {
        const string killed = "state = not running\nlast exit code = (never exited)\nlast terminating signal = Killed: 9\n";

        var sentence = LaunchdDiagnostics.Explain(killed, jobLoaded: true);

        Assert.Contains("stopped by Killed: 9", sentence);
        Assert.DoesNotContain("exited with code", sentence);
    }

    [Fact]
    public void Explain_SaysSoWhenTheJobWasUnloaded()
    {
        var sentence = LaunchdDiagnostics.Explain(null, jobLoaded: false);

        Assert.Contains("no longer has the launcher registered", sentence);
    }

    [Fact]
    public void Explain_ReturnsNullWhenLaunchdSaidNothingUseful()
    {
        Assert.Null(LaunchdDiagnostics.Explain("active count = 0\n", jobLoaded: true));
    }

    [Fact]
    public void Tail_ReturnsTheLastNonEmptyLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"launchd-tail-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "one\n\ntwo\nthree\nfour\n\n");
            Assert.Equal("three\nfour", LaunchdDiagnostics.Tail(path, 2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tail_IsNullForAMissingFile()
    {
        Assert.Null(LaunchdDiagnostics.Tail(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.log"), 5));
    }

    [Fact]
    public void Compose_IncludesLaunchdAndEachLogTail()
    {
        var text = LaunchdDiagnostics.Compose(CrashedJob, jobLoaded: true,
            [("launchd-stderr.log (last lines)", "Unhandled exception. System.Exception: boom"), ("launchd-stdout.log (last lines)", null)]);

        Assert.Contains("last exit code = 134: Abort trap", text);
        Assert.Contains("Unhandled exception. System.Exception: boom", text);
        Assert.Contains("launchd-stdout.log (last lines):\n  (missing or empty)", text.Replace("\r\n", "\n"));
    }

    // ---- The launcher's own log in the launcher/start report (issue #3311, B4) ----

    [Fact]
    public void LauncherLogTail_TakesTheNewestLauncherLog_EvenWhileTheLauncherHoldsItOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-launcher-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var older = Path.Combine(dir, "launcher-2026-09-25-100.log");
            File.WriteAllText(older, "old run\n");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.Combine(dir, "launchd-stderr.log"), "not the launcher's own log\n");
            var current = Path.Combine(dir, "launcher-2026-09-26-200.log");
            using var writer = new StreamWriter(new FileStream(current, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            for (var i = 0; i < 100; i++) writer.WriteLine($"line {i}");
            writer.WriteLine("[LauncherCore] StartAsync FAILED: the Gateway refused the registration");

            var tail = LaunchdDiagnostics.LauncherLogTail(dir, 60)!;

            Assert.StartsWith("launcher-2026-09-26-200.log:", tail);
            Assert.Contains("StartAsync FAILED: the Gateway refused the registration", tail);
            Assert.Equal(61, tail.Split('\n').Length);
            Assert.DoesNotContain("old run", tail);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LauncherLogTail_NoLauncherLog_IsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-launcher-log-test-" + Guid.NewGuid().ToString("N"));
        Assert.Null(LaunchdDiagnostics.LauncherLogTail(dir, 60));
    }

    [Fact]
    public void ComposeBinaryChecks_LabelsEachAnswerWithItsExitCode()
    {
        var text = LaunchdDiagnostics.ComposeBinaryChecks(
        [
            ("xattr -l (quarantine flag)", 0, "com.apple.quarantine: 0083;66f4a1b2;Safari;\n"),
            ("codesign -dv (signature)", 1, "code object is not signed at all\n"),
            ("log show (last 3 minutes mentioning cc-launcher)", 0, ""),
        ]).Replace("\r\n", "\n");

        Assert.StartsWith("launcher binary:", text);
        Assert.Contains("xattr -l (quarantine flag) -> exit 0:\n    com.apple.quarantine: 0083;66f4a1b2;Safari;", text);
        Assert.Contains("codesign -dv (signature) -> exit 1:\n    code object is not signed at all", text);
        Assert.Contains("log show (last 3 minutes mentioning cc-launcher) -> exit 0:\n    (no output)", text);
    }

    [Fact]
    public void ComposeBinaryChecks_KeepsOnlyTheLastLinesOfALongLog()
    {
        var log = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var text = LaunchdDiagnostics.ComposeBinaryChecks([("log show", 0, log)]).Replace("\r\n", "\n");

        Assert.Contains($"({100 - LaunchdDiagnostics.MaxBinaryCheckLines} earlier lines left out)", text);
        Assert.Contains("    line 76\n", text);
        Assert.DoesNotContain("    line 75\n", text);
        Assert.EndsWith("    line 100", text);
    }
}
