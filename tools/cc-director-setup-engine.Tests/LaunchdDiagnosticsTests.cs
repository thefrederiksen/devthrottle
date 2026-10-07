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
    public void Explain_NamesTheExitCodeRunsAndState()
    {
        var sentence = LaunchdDiagnostics.Explain(CrashedJob, jobLoaded: true);

        Assert.NotNull(sentence);
        Assert.Contains("exited with code 134: Abort trap", sentence);
        Assert.Contains("started it 2 time(s)", sentence);
        Assert.Contains("\"not running\"", sentence);
    }

    [Fact]
    public void Explain_SpawnFailed_SaysTheProgramNeverRan()
    {
        // The user's Mac (#3411): launchd could not even start the program. The old sentence said it "started
        // but did not stay running" and pointed at logs a program that never ran could not have written.
        const string refused = "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n";

        var sentence = LaunchdDiagnostics.Explain(refused, jobLoaded: true);

        Assert.NotNull(sentence);
        Assert.Contains("could not start the launcher at all", sentence);
        Assert.Contains("spawn failed", sentence);
        Assert.Contains("78: EX_CONFIG", sentence);
        Assert.Contains("never ran a line", sentence);
        Assert.Contains("tried 6 time(s)", sentence);
        Assert.DoesNotContain("did not stay running", sentence);
    }

    [Fact]
    public void Explain_Exit78WithARetryScheduled_StillSaysTheProgramNeverRan()
    {
        // A GitHub macOS 26 runner, 7 October 2026: the same refusal, but the domain schedules a retry, so the
        // job state reads "spawn scheduled" rather than "spawn failed". Exit 78 is the refusal either way.
        const string refused = "state = spawn scheduled\nruns = 5\nlast exit code = 78: EX_CONFIG\n";

        var sentence = LaunchdDiagnostics.Explain(refused, jobLoaded: true);

        Assert.Contains("could not start the launcher at all", sentence);
        Assert.Contains("78: EX_CONFIG", sentence);
        Assert.Contains("\"spawn scheduled\"", sentence);
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
    public void Fit_UnderBudget_KeepsEverySectionWhole()
    {
        var text = LaunchdDiagnostics.Fit(
        [
            new("ls -ldO", "drwxr-xr-x  5 robert staff 160 /Users/robert/Library", false),
            new("log show", "line 1\nline 2", true),
        ], 1000).Replace("\r\n", "\n");

        Assert.Equal("ls -ldO:\n  drwxr-xr-x  5 robert staff 160 /Users/robert/Library\nlog show:\n  line 1\n  line 2", text);
    }

    [Fact]
    public void Fit_OverBudget_CutsTheLongestSectionFirstAndSaysSo()
    {
        var log = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"log line {i:D3} xpcproxy something"));
        var text = LaunchdDiagnostics.Fit(
        [
            new("launchctl print (full)", "state = spawn scheduled\nlast exit code = 78: EX_CONFIG", false),
            new("log show", log, true),
            new("sw_vers", "ProductVersion: 26.0", false),
        ], 2000).Replace("\r\n", "\n");

        Assert.True(text.Length <= 2000, $"length {text.Length}");
        Assert.Contains("launchctl print (full):\n  state = spawn scheduled\n  last exit code = 78: EX_CONFIG", text);
        Assert.Contains("sw_vers:\n  ProductVersion: 26.0", text);
        Assert.Contains("earlier characters left out to fit the report", text);
        Assert.Contains("log line 400 xpcproxy something", text);
        Assert.DoesNotContain("log line 001", text);
    }

    [Fact]
    public void Fit_ListingSection_KeepsItsStartWhenCut()
    {
        var listing = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"entry {i:D3}"));
        var text = LaunchdDiagnostics.Fit([new("launch agent property list on disk", listing, false)], 600).Replace("\r\n", "\n");

        Assert.True(text.Length <= 600, $"length {text.Length}");
        Assert.Contains("  entry 001\n", text);
        Assert.Contains("later characters left out to fit the report", text);
        Assert.DoesNotContain("entry 300", text);
    }

    [Fact]
    public void Fit_EverySectionAtItsFloor_StillHonoursTheBudgetAndSaysSo()
    {
        var text = LaunchdDiagnostics.Fit(
        [
            new("a", new string('x', LaunchdDiagnostics.MinSectionChars), false),
            new("b", new string('y', LaunchdDiagnostics.MinSectionChars), true),
        ], 100);

        Assert.True(text.Length <= 100, $"length {text.Length}");
        Assert.StartsWith("a:", text);
        Assert.EndsWith("(cut to fit the report)", text);
    }

    [Fact]
    public void DiagnosticsBudget_LeavesRoomForTheWizardAppendix()
    {
        Assert.Equal(CcDirector.Core.ErrorReports.InstallReportLimits.MaxDiagnostics,
            LaunchdDiagnostics.DiagnosticsBudget + LaunchdDiagnostics.WizardAppendixReserve);
        Assert.True(LaunchdDiagnostics.DiagnosticsBudget >= 12000);
    }

    [Fact]
    public void JobFacts_KeepsOnlyNamedScalarFields_NeverArgumentsOrEnvironment()
    {
        const string print = "gui/501/com.devthrottle.cc-launcher = {\n\tactive count = 0\n\tpath = /Users/robert/Library/LaunchAgents/com.devthrottle.cc-launcher.plist\n\tstate = not running\n\tprogram = /Users/robert/Library/Application Support/cc-director/launcher/cc-launcher\n\targuments = {\n\t\t/Users/robert/Library/Application Support/cc-director/launcher/cc-launcher\n\t\t--managed\n\t\t--password hunter2\n\t}\n\tenvironment = {\n\t\tAWS_SECRET_ACCESS_KEY => wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY\n\t\tPATH => /usr/bin\n\t}\n\tendpoints = {\n\t\t\"com.devthrottle.secret-endpoint\" = {\n\t\t\tport = 0x1\n\t\t}\n\t}\n\truns = 6\n\tlast exit code = 78: EX_CONFIG\n\tjob state = spawn failed\n\tproperties = runatload | inferred program\n}\n";

        var text = LaunchdDiagnostics.JobFacts(print);

        Assert.Contains("state = not running", text);
        Assert.Contains("runs = 6", text);
        Assert.Contains("last exit code = 78: EX_CONFIG", text);
        Assert.Contains("job state = spawn failed", text);
        Assert.Contains("program = /Users/robert/Library/Application Support/cc-director/launcher/cc-launcher", text);
        Assert.Contains("properties = runatload | inferred program", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("--managed", text);
        Assert.DoesNotContain("AWS_SECRET", text);
        Assert.DoesNotContain("wJalrXUtnFEMI", text);
        Assert.DoesNotContain("PATH", text);
        Assert.DoesNotContain("secret-endpoint", text);
        Assert.DoesNotContain("port = ", text);
    }

    [Fact]
    public void JobFacts_NoAnswer_SaysSo()
    {
        Assert.Equal("(launchd gave no answer for the job)", LaunchdDiagnostics.JobFacts(null));
        Assert.Equal("(no named job fields in launchd's answer)", LaunchdDiagnostics.JobFacts("nonsense\n"));
    }

    [Fact]
    public void PlistFacts_SendsTheProgramAndLogPaths_NeverTheArgumentsOrTheEnvironment()
    {
        var plist = LauncherLaunchdAutostart.PlistContent("/Users/robert/Library/Application Support/cc-director/launcher/cc-launcher", "--managed --password hunter2", "/Users/robert/Library/Application Support/cc-director/logs/launcher")
            .Replace("    <key>ProcessType</key>", "    <key>EnvironmentVariables</key>\n    <dict>\n        <key>GITHUB_TOKEN</key>\n        <string>ghp_16C7e42F292c6912E7710c838347Ae178B4a</string>\n    </dict>\n    <key>Nickname</key>\n    <string>top secret</string>\n    <key>ProcessType</key>");

        var text = LaunchdDiagnostics.PlistFacts(plist).Replace("\r\n", "\n");

        Assert.Contains("Label = com.devthrottle.cc-launcher", text);
        Assert.Contains("ProgramArguments = 4 entries; program = /Users/robert/Library/Application Support/cc-director/launcher/cc-launcher; the arguments are not sent", text);
        Assert.Contains("RunAtLoad = true", text);
        Assert.Contains("KeepAlive = dict (SuccessfulExit = false)", text);
        Assert.Contains("ProcessType = Interactive", text);
        // The paths are built with the platform separator, as the template builds them.
        Assert.Contains("StandardOutPath = " + Path.Combine("/Users/robert/Library/Application Support/cc-director/logs/launcher", "launchd-stdout.log"), text);
        Assert.Contains("StandardErrorPath = " + Path.Combine("/Users/robert/Library/Application Support/cc-director/logs/launcher", "launchd-stderr.log"), text);
        Assert.Contains("EnvironmentVariables = present (1 entries, not sent)", text);
        Assert.Contains("Nickname = (not sent)", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("--managed", text);
        Assert.DoesNotContain("GITHUB_TOKEN", text);
        Assert.DoesNotContain("ghp_", text);
        Assert.DoesNotContain("top secret", text);
    }

    [Fact]
    public void PlistFacts_NeverEnumeratesUnknownChildrenOfKeepAlive_AndChecksEveryValueKind()
    {
        // KeepAlive is an allowed key, and it used to be a licence to send every child inside it: a planted
        // "Password" child with a short value travelled as "KeepAlive = dict (Password = hunter2)". Only the
        // boolean children launchd defines are sent, and a scalar of the wrong kind is named, not sent.
        // The template carries the source file's own line endings, so they are normalized before the edits.
        var plist = LauncherLaunchdAutostart.PlistContent("/Users/robert/Library/Application Support/cc-director/launcher/cc-launcher", null, "/Users/robert/Library/Application Support/cc-director/logs/launcher")
            .Replace("\r\n", "\n")
            .Replace("        <key>SuccessfulExit</key>", "        <key>Password</key>\n        <string>hunter2</string>\n        <key>Crashed</key>\n        <string>hunter2</string>\n        <key>SuccessfulExit</key>")
            .Replace("    <key>ProcessType</key>\n    <string>Interactive</string>", "    <key>ProcessType</key>\n    <dict>\n        <key>Secret</key>\n        <string>hunter2</string>\n    </dict>\n    <key>RunAtLoad</key>\n    <string>hunter2</string>");

        var text = LaunchdDiagnostics.PlistFacts(plist).Replace("\r\n", "\n");

        Assert.Contains("KeepAlive = dict (Password = (not sent), Crashed = (a string, not a boolean; not sent), SuccessfulExit = false)", text);
        Assert.Contains("ProcessType = (a dict, not a string; not sent)", text);
        Assert.Contains("RunAtLoad = (a string, not a boolean; not sent)", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("Secret", text);
    }

    [Fact]
    public void PlistFacts_NotXml_SaysSoInsteadOfSendingTheBytes()
    {
        var text = LaunchdDiagnostics.PlistFacts("bplist00 binary garbage --password hunter2");

        Assert.StartsWith("(not a readable property list:", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void DomainFacts_KeepsOnlyNamedHeaderFields_NeverTheEnvironmentOrTheServices()
    {
        const string domain = "gui/501 = {\n\ttype = user\n\thandle = 501\n\tactive count = 418\n\ton-demand count = 56\n\tservice count = 603\n\tactive service count = 180\n\tmaximum allowed shutdown time = 65 s\n\tservice stats = {\n\t\tcom.apple.foo => 1\n\t}\n\tenvironment = {\n\t\tGITHUB_TOKEN => ghp_16C7e42F292c6912E7710c838347Ae178B4a\n\t\tHOME => /Users/robert\n\t}\n\tservices = {\n\t\t0 - 0 com.devthrottle.cc-launcher\n\t\t0 - 0 com.example.secret-helper --token=abc\n\t}\n\tendpoints = {\n\t\t\"com.apple.bar\" = {\n\t\t\tport = 0x1\n\t\t}\n\t}\n\tproperties = synthesized | something\n}\n";

        var text = LaunchdDiagnostics.DomainFacts(domain);

        Assert.Contains("type = user", text);
        Assert.Contains("handle = 501", text);
        Assert.Contains("on-demand count = 56", text);
        Assert.Contains("active service count = 180", text);
        Assert.Contains("properties = synthesized | something", text);
        Assert.DoesNotContain("GITHUB_TOKEN", text);
        Assert.DoesNotContain("ghp_", text);
        Assert.DoesNotContain("HOME", text);
        Assert.DoesNotContain("secret-helper", text);
        Assert.DoesNotContain("--token", text);
        Assert.DoesNotContain("endpoints", text);
        Assert.DoesNotContain("maximum allowed shutdown time", text);
    }

    [Fact]
    public void DomainFacts_NoAnswer_SaysSo()
    {
        Assert.Equal("(launchd gave no answer for the domain)", LaunchdDiagnostics.DomainFacts(null));
        Assert.Equal("(no named domain fields in launchd's answer)", LaunchdDiagnostics.DomainFacts("nonsense\n"));
    }

    [Fact]
    public void Fit_OneGiantLine_IsCutToTheBudget()
    {
        var text = LaunchdDiagnostics.Fit([new("log show", new string('x', 5000), true)], 1000);

        Assert.True(text.Length <= 1000, $"length {text.Length}");
        Assert.Contains("earlier characters left out", text);
    }
}
