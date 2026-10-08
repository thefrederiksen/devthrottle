#pragma warning disable CA1416 // RunOnce is macOS-only in production; here every effect is injected, so it runs on every operating system.
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Every outcome of the Director's launcher repair becomes a report (owner ruling of 7 October 2026): rebuilt with
/// the process launchd reports, left alone with its verdict and reason, failed with its diagnostics - and none of
/// them names the user or their home folder.
/// </summary>
public class LauncherRepairReportTests : IDisposable
{
    private const string User = "robert";
    private const string Home = "/Users/robert";

    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _plist;

    public LauncherRepairReportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-launcher-repair-report-" + Guid.NewGuid().ToString("N"));
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "");
        _plist = Path.Combine(_dir, "LaunchAgents", LauncherLaunchdAutostart.Label + ".plist");
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    // launchctl's answer for a refused job, carrying the user's name and home the way a real one can: in the
    // job's path, and in its environment, outside any path.
    private const string Refused = "path = /Users/robert/Library/LaunchAgents/com.devthrottle.cc-launcher.plist\n"
                                   + "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n"
                                   + "environment = {\n\tUSER => robert\n\tLOGNAME => robert\n}\n";
    private const string NotFound = "Could not find service \"com.devthrottle.cc-launcher\" in domain for user gui: 501";
    private const string NothingDisabled = "disabled services = {\n}\n";
    private static readonly IReadOnlyList<string> NoLines = Array.Empty<string>();

    /// <summary>launchd faked: a refused job; after the kickstart a process shows when <paramref name="starts"/>.</summary>
    private static LauncherLaunchdAutostart.CommandRunner Fake(bool starts, string disabledList = NothingDisabled, int uidExit = 0)
    {
        var held = true;
        var kickstarted = false;
        return (exe, args) =>
        {
            if (exe == "/usr/bin/id") return (uidExit, uidExit == 0 ? "501\n" : "");
            if (exe != "/bin/launchctl") return (0, "");
            if (args.StartsWith("print-disabled", StringComparison.Ordinal)) return (0, disabledList);
            if (args.StartsWith("print ", StringComparison.Ordinal))
            {
                if (!held) return (113, NotFound);
                if (!kickstarted) return (0, Refused);
                return starts
                    ? (0, "path = /Users/robert/Library/LaunchAgents/com.devthrottle.cc-launcher.plist\nstate = running\npid = 4242\nruns = 7\n")
                    : (0, "state = spawn scheduled\nruns = 7\nlast exit code = 78: EX_CONFIG\n");
            }
            if (args.StartsWith("bootout ", StringComparison.Ordinal)) { held = false; return (0, ""); }
            if (args.StartsWith("bootstrap ", StringComparison.Ordinal)) { held = true; return (0, ""); }
            if (args.StartsWith("kickstart ", StringComparison.Ordinal)) { kickstarted = true; return (0, ""); }
            return (0, "");
        };
    }

    private LauncherRepairOutcome Run(LauncherLaunchdAutostart.CommandRunner run, int running = 0)
        => LauncherLaunchdRepair.RunOnce(_layout, run, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20), () => running, _plist);

    private static void AssertNamesNobody(LauncherRepairReport.Text text)
    {
        foreach (var part in new[] { text.Message, text.Detail })
        {
            Assert.DoesNotContain(User, part, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Home, part, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RunOnce_RefusedJobThatStarts_IsRebuiltWithThePid()
    {
        var outcome = Run(Fake(starts: true));

        Assert.Equal(LauncherRepairResult.Rebuilt, outcome.Result);
        Assert.Equal("Repair", outcome.Verdict);
        Assert.Equal(4242, outcome.Pid);
        Assert.Contains("launchd refused to spawn the launcher", outcome.Reason);
    }

    [Fact]
    public void Compose_Rebuilt_SaysRebuiltAndRunningWithThePidAndCarriesTheSteps()
    {
        var text = LauncherRepairReport.Compose(Run(Fake(starts: true)), NoLines, User, Home);

        Assert.StartsWith("launcher repair: rebuilt the launch agent and launchd reports the launcher running as process 4242.", text.Message);
        Assert.Contains("Found: launchd refused to spawn the launcher", text.Message);
        Assert.Contains("Rebuilt the launch agent", text.Detail);
        AssertNamesNobody(text);
    }

    [Fact]
    public void Compose_RebuiltButNeverStarted_IsFailedWithLaunchdsAnswer()
    {
        var outcome = Run(Fake(starts: false));
        var text = LauncherRepairReport.Compose(outcome, NoLines, User, Home);

        Assert.Equal(LauncherRepairResult.Failed, outcome.Result);
        Assert.Equal(0, outcome.Pid);
        Assert.StartsWith("launcher repair: failed (Repair): the launcher did not start after the launch agent was rebuilt", text.Message);
        Assert.Contains("never ran", text.Message);
        Assert.StartsWith("FAILED to start the launcher after rebuilding the launch agent", text.Detail);
        Assert.Contains("78: EX_CONFIG", text.Detail);
        AssertNamesNobody(text);
    }

    [Fact]
    public void Compose_SwitchedOff_IsLeftAloneWithItsVerdictAndReason()
    {
        var outcome = Run(Fake(starts: true, disabledList: "disabled services = {\n\t\"com.devthrottle.cc-launcher\" => disabled\n}\n"));
        var text = LauncherRepairReport.Compose(outcome, NoLines, User, Home);

        Assert.Equal(LauncherRepairResult.LeftAlone, outcome.Result);
        Assert.Equal("launcher repair: left alone (Disabled): launchd's disabled list names the launcher: somebody switched it off, and that is not overridden", text.Message);
        Assert.Equal("", text.Detail);
        Assert.Equal("<old/>", File.ReadAllText(_plist));
    }

    [Fact]
    public void Compose_AlreadyRunning_IsLeftAloneAsRunning()
    {
        var text = LauncherRepairReport.Compose(Run(Fake(starts: true), running: 1), NoLines, User, Home);

        Assert.Equal("launcher repair: left alone (Running): 1 installed launcher process(es) running", text.Message);
    }

    [Fact]
    public void Compose_NoLaunchAgent_IsLeftAloneAsNoLaunchAgent()
    {
        File.Delete(_plist);
        var text = LauncherRepairReport.Compose(Run(Fake(starts: true)), NoLines, User, Home);

        Assert.StartsWith("launcher repair: left alone (NoLaunchAgent):", text.Message);
    }

    [Fact]
    public void Compose_ClosedOnPurpose_IsLeftAloneAsClosedOnPurpose()
    {
        LauncherLaunchdAutostart.CommandRunner closed = (exe, args) =>
            exe == "/usr/bin/id" ? (0, "501\n")
            : args.StartsWith("print-disabled", StringComparison.Ordinal) ? (0, NothingDisabled)
            : args.StartsWith("print ", StringComparison.Ordinal) ? (0, "state = not running\nruns = 1\nlast exit code = 0\n")
            : (0, "");
        var text = LauncherRepairReport.Compose(Run(closed), NoLines, User, Home);

        Assert.StartsWith("launcher repair: left alone (ClosedOnPurpose):", text.Message);
    }

    [Fact]
    public void Compose_NoLauncherBinary_IsLeftAloneWithoutTheHomeFolder()
    {
        // The install root sits in the home folder on a Mac; the report must not carry it.
        var outcome = new LauncherRepairOutcome(LauncherRepairResult.LeftAlone, "NoLauncherBinary",
            "no launcher binary at /Users/robert/Library/Application Support/cc-director/launcher/cc-launcher; nothing to repair", 0, "x");
        var text = LauncherRepairReport.Compose(outcome, NoLines, User, Home);

        Assert.Equal("launcher repair: left alone (NoLauncherBinary): no launcher binary at ~/Library/Application Support/cc-director/launcher/cc-launcher; nothing to repair", text.Message);
        AssertNamesNobody(text);
    }

    [Fact]
    public void Compose_UserIdUnresolved_IsFailedNotChecked()
    {
        var outcome = Run(Fake(starts: true, uidExit: 1));
        var text = LauncherRepairReport.Compose(outcome, NoLines, User, Home);

        Assert.Equal(LauncherRepairResult.Failed, outcome.Result);
        Assert.StartsWith("launcher repair: failed (NotChecked): the user id could not be resolved (exit 1)", text.Message);
        Assert.StartsWith("FAILED to resolve the user id (exit 1)", text.Detail);
    }

    [Fact]
    public void Compose_StaysInsideTheReportLimits()
    {
        var big = new string('m', 50_000);
        var text = LauncherRepairReport.Compose(new LauncherRepairOutcome(LauncherRepairResult.Failed, "Repair", big, 0, big), NoLines, User, Home);

        Assert.True(text.Message.Length <= CcDirector.Core.ErrorReports.ErrorReportLimits.MaxMessage);
        Assert.True(text.Detail.Length <= CcDirector.Core.ErrorReports.ErrorReportLimits.MaxStack);
    }

    [Fact]
    public void Compose_HeldErrorLines_TravelInTheDetailWithoutTheUser()
    {
        // A rebuild that throws logs "[LauncherLaunchdAutostart] Rebuild FAILED: ..." carrying up to 300 characters
        // of launchctl's own answer; the pass holds that line for its one report, and it is scrubbed with it.
        var outcome = new LauncherRepairOutcome(LauncherRepairResult.Failed, "Repair", "the launch agent could not be rebuilt", 0, "FAILED to rebuild");
        var held = new[] { "[LauncherLaunchdAutostart] Rebuild FAILED: bootstrap refused (exit 5: USER => robert, path = /Users/robert/Library/x)" };

        var text = LauncherRepairReport.Compose(outcome, held, User, Home);

        Assert.Contains("Error lines logged during the pass:\n[LauncherLaunchdAutostart] Rebuild FAILED: bootstrap refused (exit 5: USER => <user>, path = ~/Library/x)", text.Detail);
        AssertNamesNobody(text);
    }

    [Fact]
    public void Compose_LeftAloneWithHeldLines_CarriesOnlyTheLines()
    {
        var outcome = new LauncherRepairOutcome(LauncherRepairResult.LeftAlone, "Running", "1 installed launcher process(es) running", 0, "Running: x");

        var text = LauncherRepairReport.Compose(outcome, new[] { "[X] Y FAILED: z" }, User, Home);

        Assert.Equal("Error lines logged during the pass:\n[X] Y FAILED: z", text.Detail);
    }

    [Theory]
    [InlineData("USER => robert", "USER => <user>")]
    [InlineData("owner Robert, group staff", "owner <user>, group staff")]
    [InlineData("/Users/robert/Library/x", "~/Library/x")]
    [InlineData("roberta and robert_x stay", "roberta and robert_x stay")]
    public void WithoutIdentity_TakesOutTheNameAsAWordAndTheHomeFolder(string input, string expected)
        => Assert.Equal(expected, LauncherRepairReport.WithoutIdentity(input, User, Home));

    [Fact]
    public void WithoutIdentity_HomeOutsideUsers_BecomesTilde()
    {
        // A home folder the general scrubber does not know the shape of is still taken out.
        Assert.Equal("log at ~/logs", LauncherRepairReport.WithoutIdentity("log at /var/homes/r1/logs", "r1", "/var/homes/r1"));
    }
}
