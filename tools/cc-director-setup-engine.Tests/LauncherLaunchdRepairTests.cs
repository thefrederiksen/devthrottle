using CcDirector.Setup.Engine;
using Xunit;
using static CcDirector.Setup.Engine.LauncherLaunchdRepair;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Director's decision about a launcher launchd will not start (#3411). Every branch is pure, so these
/// run on every operating system; what they pin is that a job launchd HOLDS AND REFUSES is rebuilt, and that
/// nothing a person may have chosen - a closed launcher, a switched-off item, a job launchd does not hold -
/// ever is.
/// </summary>
public class LauncherLaunchdRepairTests
{
    // The user's Mac, 6 October 2026: the job launchd held and refused for two weeks.
    private const string Refused =
        "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n";

    [Fact]
    public void Decide_RefusedJob_Repairs()
    {
        var d = Decide(plistExists: true, jobLoaded: true, Refused, installedLaunchersRunning: 0, DisabledState.Enabled);

        Assert.Equal(Verdict.Repair, d.Verdict);
        Assert.Contains("spawn failed", d.Reason);
        Assert.Contains("78: EX_CONFIG", d.Reason);
    }

    [Fact]
    public void Decide_KilledForCodeSigning_Repairs()
    {
        // The same Mac on its first attempt, 23 September 2026.
        const string killed = "state = not running\nruns = 1\nlast exit reason = OS_REASON_CODESIGNING\njob state = spawn failed\n";

        var d = Decide(true, true, killed, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.Repair, d.Verdict);
    }

    [Fact]
    public void Decide_Exit78WithARetryScheduled_Repairs()
    {
        const string scheduled = "state = spawn scheduled\nruns = 5\nlast exit code = 78: EX_CONFIG\n";

        var d = Decide(true, true, scheduled, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.Repair, d.Verdict);
        Assert.Contains("refused to spawn", d.Reason);
    }

    [Fact]
    public void Decide_StoppedByASignalAlone_Repairs()
    {
        // No job state, no exit code: only the signal field. The branch stands on its own.
        const string signalled = "state = not running\nruns = 2\nlast terminating signal = 9: Killed: 9\n";

        var d = Decide(true, true, signalled, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.Repair, d.Verdict);
        Assert.Contains("stopped by a signal", d.Reason);
        Assert.Contains("Killed: 9", d.Reason);
    }

    [Fact]
    public void Decide_EndedByMacOSAlone_Repairs()
    {
        // Only an exit reason: no job state, no exit code, no signal.
        const string ended = "state = not running\nruns = 2\nlast exit reason = OS_REASON_JETSAM\n";

        var d = Decide(true, true, ended, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.Repair, d.Verdict);
        Assert.Contains("ended by macOS", d.Reason);
        Assert.Contains("OS_REASON_JETSAM", d.Reason);
    }

    [Fact]
    public void Decide_Crashed_Repairs()
    {
        const string crashed = "state = not running\nruns = 2\nlast exit code = 134: Abort trap\njob state = exited\n";

        var d = Decide(true, true, crashed, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.Repair, d.Verdict);
    }

    [Fact]
    public void Decide_NoPropertyList_LeavesAlone()
    {
        var d = Decide(plistExists: false, jobLoaded: false, null, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.NoLaunchAgent, d.Verdict);
    }

    [Fact]
    public void Decide_LauncherProcessRunning_IsRunning()
    {
        var d = Decide(true, true, Refused, installedLaunchersRunning: 1, DisabledState.Enabled);

        Assert.Equal(Verdict.Running, d.Verdict);
    }

    [Fact]
    public void Decide_LaunchdReportsPid_IsRunning()
    {
        var d = Decide(true, true, "state = running\npid = 4242\n", 0, DisabledState.Enabled);

        Assert.Equal(Verdict.Running, d.Verdict);
        Assert.Contains("4242", d.Reason);
    }

    [Fact]
    public void Decide_CleanExit_IsClosedOnPurpose()
    {
        // The tray's Quit: a clean exit that KeepAlive (SuccessfulExit=false) deliberately leaves exited.
        const string quit = "state = not running\nruns = 3\nlast exit code = 0\njob state = exited\n";

        var d = Decide(true, true, quit, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.ClosedOnPurpose, d.Verdict);
    }

    [Fact]
    public void Decide_NeverRan_IsClosedOnPurpose()
    {
        const string never = "state = not running\nruns = 0\nlast exit code = (never exited)\n";

        var d = Decide(true, true, never, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.ClosedOnPurpose, d.Verdict);
    }

    [Fact]
    public void Decide_PropertyListButJobNotLoaded_IsLeftAlone()
    {
        // A job launchd does not hold may be a person's doing (a bootout, a switched-off login item) and the
        // Director cannot tell; it is not rebuilt. The installer registers; the Director only repairs.
        var d = Decide(plistExists: true, jobLoaded: false, null, 0, DisabledState.Enabled);

        Assert.Equal(Verdict.NotLoaded, d.Verdict);
        Assert.Contains("not rebuilt", d.Reason);
    }

    [Fact]
    public void Decide_DisabledByThePerson_IsNeverRepaired_EvenWhenLaunchdReportsARefusal()
    {
        var d = Decide(true, true, Refused, 0, DisabledState.Disabled);

        Assert.Equal(Verdict.Disabled, d.Verdict);
        Assert.Contains("switched it off", d.Reason);
    }

    [Fact]
    public void Decide_DisabledStateUnknown_FailsClosed()
    {
        var d = Decide(true, true, Refused, 0, DisabledState.Unknown);

        Assert.Equal(Verdict.Unknown, d.Verdict);
        Assert.Contains("nothing is rebuilt", d.Reason);
    }

    [Theory]
    [InlineData("disabled services = {\n\t\"com.apple.something\" => disabled\n\t\"com.devthrottle.cc-launcher\" => disabled\n}\n", "Disabled")]
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher\" => true\n}\n", "Disabled")]
    [InlineData("Disabled Services = {\n\t\"com.devthrottle.cc-launcher\" => disabled\n}\n", "Disabled")]
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher\" => enabled\n}\n", "Enabled")]
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher\" => false\n}\n", "Enabled")]
    [InlineData("disabled services = {\n\t\"com.apple.something\" => disabled\n}\n", "Enabled")]
    [InlineData("disabled services = {\n}\n", "Enabled")]
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher.helper\" => disabled\n}\n", "Enabled")]
    public void ParseDisabled_ReadsACompleteDisabledList(string output, string expected)
    {
        // The enum is internal, so the expectation travels as its name.
        Assert.Equal(Enum.Parse<DisabledState>(expected), ParseDisabled(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("nonsense\n")]
    [InlineData("disabled services = {\n\t\"com.apple.something\" => disabled\n")]                       // no closing brace: truncated
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher\"\n}\n")]                        // our label with no value
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher\" => maybe\n}\n")]               // a value this does not know
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher\" => disabled\n\t\"com.devthrottle.cc-launcher\" => enabled\n}\n")] // our label named twice
    [InlineData("disabled services = {\n\t\"com.example.other\" => disabled\n\t\"com.example.other\" => enabled\n}\n")] // ANOTHER label named twice: not one dictionary either
    [InlineData("disabled services = {\n\tsomething that is not an entry\n}\n")]                           // the format changed
    [InlineData("disabled services = {\n}\ntruncated garbage\n")]                                 // a valid-looking prefix, then garbage
    [InlineData("some preamble\ndisabled services = {\n\t\"com.apple.something\" => disabled\n}\n")]     // leading material
    [InlineData("disabled services = {\n\t\"com.apple.something\" => disabled\n}\n\"trailing\" => disabled\n")] // trailing material
    [InlineData("disabled services = {\n}\nenabled = {\n\t\"com.devthrottle.cc-launcher\" => disabled\n}\n")] // a second dictionary
    [InlineData("disabled services = {\n\tnested = {\n\t\t\"com.apple.something\" => disabled\n\t}\n}\n")]  // a nested dictionary
    [InlineData("disabled services = {\n\t\"com.apple.something\" => maybe\n}\n")]                       // an unknown value on another label
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher => disabled\n}\n")]              // a label whose quote never closes
    [InlineData("not disabled services = {\n}\n")]                                                  // a header that merely CONTAINS the words
    [InlineData("disabled = {\n}\n")]                                                               // the old, loose header: not launchctl's
    [InlineData("services disabled = {\n\t\"com.devthrottle.cc-launcher\" => disabled\n}\n")]        // the words in another order
    public void ParseDisabled_AnythingButACompleteRecognisableList_IsUnknown(string? output)
    {
        Assert.Equal(DisabledState.Unknown, ParseDisabled(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("disabled services = {\n\t\"com.devthrottle.cc-launcher\" => disabled\n")]
    [InlineData("disabled services = {\n}\ntruncated garbage\n")]
    [InlineData("some preamble\ndisabled services = {\n}\n")]
    [InlineData("disabled services = {\n}\n}\n")]
    [InlineData("disabled services = {\n}\nenabled = {\n}\n")]
    [InlineData("disabled services = {\n\t\"com.apple.something\" => maybe\n}\n")]
    [InlineData("not disabled services = {\n}\n")]
    [InlineData("disabled services = {\n\t\"com.example.other\" => disabled\n\t\"com.example.other\" => enabled\n}\n")]
    public void Decide_NeverRepairsWhenTheDisabledListIsUnknown(string? printDisabled)
    {
        var d = Decide(true, true, Refused, 0, ParseDisabled(printDisabled));

        Assert.NotEqual(Verdict.Repair, d.Verdict);
        Assert.Equal(Verdict.Unknown, d.Verdict);
    }
}
