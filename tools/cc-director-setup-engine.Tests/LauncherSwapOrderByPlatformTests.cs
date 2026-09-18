using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The swap order a launcher update uses is chosen by whether the launcher has a SUPERVISOR on this
/// platform, not by which platform it is. Getting it wrong does not fail loudly - it reinstates the old
/// build and reports success - which is why it is pinned here.
///
/// THE RULE: a supervised launcher is replaced on disk and then restarted BY ITS SUPERVISOR
/// (PlaceThenRestart). An unsupervised, file-locking one is stopped first (StopThenPlaceThenStart).
/// Windows is the only platform in the second group.
///
/// Linux MOVED between the groups when it gained a systemd user unit
/// (<see cref="LauncherSystemdAutostart"/>). Before that, nothing on Linux would restart a stopped
/// launcher, so stopping first was safe. Now the unit carries Restart=on-failure, and stopping first
/// lets systemd bring the OLD build back up in the gap before the swap lands - the machine then holds a
/// launcher that is alive and the wrong version, and every check that follows agrees it is healthy.
/// </summary>
public sealed class LauncherSwapOrderByPlatformTests
{
    private static LauncherUpdateOwner NewOwner() =>
        new(sharedRoot: Path.Combine(Path.GetTempPath(), "cc-director-swap-order-test"),
            directorStillHoldsItsInstance: () => true);

    [Fact]
    public void DefaultSwapOrder_OnASupervisedPlatform_ReplacesTheFileThenAsksTheSupervisor()
    {
        if (OperatingSystem.IsWindows()) return;   // covered by the Windows case below

        Assert.Equal(LauncherSwapOrder.PlaceThenRestart, NewOwner().SwapOrder);
    }

    [Fact]
    public void DefaultSwapOrder_OnWindows_StopsFirstBecauseARunningExecutableIsLocked()
    {
        if (!OperatingSystem.IsWindows()) return;   // covered by the supervised case above

        Assert.Equal(LauncherSwapOrder.StopThenPlaceThenStart, NewOwner().SwapOrder);
    }

    [Fact]
    public void DefaultSwapOrder_IsNeverStopFirstWhereSomethingWouldRestartTheLauncher()
    {
        // Stated as the rule rather than as a platform list, so a platform added later is judged by the
        // thing that actually matters: whether a supervisor exists to race the swap.
        var supervised = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
        if (!supervised) return;

        Assert.NotEqual(LauncherSwapOrder.StopThenPlaceThenStart, NewOwner().SwapOrder);
    }
}
