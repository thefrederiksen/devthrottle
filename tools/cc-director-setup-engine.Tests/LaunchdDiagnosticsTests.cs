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
}
