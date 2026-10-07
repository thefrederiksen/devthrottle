using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Director's decision about a launcher launchd will not start (#3411). Every branch is pure, so these
/// run on every operating system; what they pin is that a REFUSED job is rebuilt and a CLOSED one is not.
/// </summary>
public class LauncherLaunchdRepairTests
{
    // The user's Mac, 6 October 2026: the job launchd held and refused for two weeks.
    private const string Refused =
        "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n";

    [Fact]
    public void Decide_RefusedJob_Repairs()
    {
        var d = LauncherLaunchdRepair.Decide(plistExists: true, jobLoaded: true, Refused, installedLaunchersRunning: 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.Repair, d.Verdict);
        Assert.Contains("spawn failed", d.Reason);
        Assert.Contains("78: EX_CONFIG", d.Reason);
    }

    [Fact]
    public void Decide_KilledForCodeSigning_Repairs()
    {
        // The same Mac on its first attempt, 23 September 2026.
        const string killed = "state = not running\nruns = 1\nlast exit reason = OS_REASON_CODESIGNING\njob state = spawn failed\n";

        var d = LauncherLaunchdRepair.Decide(true, true, killed, 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.Repair, d.Verdict);
    }

    [Fact]
    public void Decide_Exit78WithARetryScheduled_Repairs()
    {
        const string scheduled = "state = spawn scheduled\nruns = 5\nlast exit code = 78: EX_CONFIG\n";

        var d = LauncherLaunchdRepair.Decide(true, true, scheduled, 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.Repair, d.Verdict);
        Assert.Contains("refused to spawn", d.Reason);
    }

    [Fact]
    public void Decide_NoPropertyList_LeavesAlone()
    {
        var d = LauncherLaunchdRepair.Decide(plistExists: false, jobLoaded: false, null, 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.NoLaunchAgent, d.Verdict);
    }

    [Fact]
    public void Decide_LauncherProcessRunning_IsRunning()
    {
        var d = LauncherLaunchdRepair.Decide(true, true, Refused, installedLaunchersRunning: 1);

        Assert.Equal(LauncherLaunchdRepair.Verdict.Running, d.Verdict);
    }

    [Fact]
    public void Decide_LaunchdReportsPid_IsRunning()
    {
        var d = LauncherLaunchdRepair.Decide(true, true, "state = running\npid = 4242\n", 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.Running, d.Verdict);
        Assert.Contains("4242", d.Reason);
    }

    [Fact]
    public void Decide_CleanExit_IsClosedOnPurpose()
    {
        // The tray's Quit: a clean exit that KeepAlive (SuccessfulExit=false) deliberately leaves exited.
        const string quit = "state = not running\nruns = 3\nlast exit code = 0\njob state = exited\n";

        var d = LauncherLaunchdRepair.Decide(true, true, quit, 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.ClosedOnPurpose, d.Verdict);
    }

    [Fact]
    public void Decide_NeverRan_IsClosedOnPurpose()
    {
        const string never = "state = not running\nruns = 0\nlast exit code = (never exited)\n";

        var d = LauncherLaunchdRepair.Decide(true, true, never, 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.ClosedOnPurpose, d.Verdict);
    }

    [Fact]
    public void Decide_Crashed_Repairs()
    {
        const string crashed = "state = not running\nruns = 2\nlast exit code = 134: Abort trap\njob state = exited\n";

        var d = LauncherLaunchdRepair.Decide(true, true, crashed, 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.Repair, d.Verdict);
    }

    [Fact]
    public void Decide_PropertyListButJobNotLoaded_RepairsNotLoaded()
    {
        // The #3575 strand: the agent booted itself out and nothing reloaded it.
        var d = LauncherLaunchdRepair.Decide(plistExists: true, jobLoaded: false, null, 0);

        Assert.Equal(LauncherLaunchdRepair.Verdict.RepairNotLoaded, d.Verdict);
    }
}
