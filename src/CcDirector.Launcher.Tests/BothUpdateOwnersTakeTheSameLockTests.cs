using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// THE FACT A TEST SEAM COULD OTHERWISE COST.
///
/// This install has two update owners and each stops the other's process: the launcher installs the
/// Director's update, and the Director installs the launcher's. They are kept apart by ONE machine-wide
/// lock, and "one" is the whole mechanism - two owners taking two differently-named mutexes exclude
/// nothing at all while looking, in every log and every test, exactly like a lock that works.
///
/// Both owners let a test override the lock's name, because the alternative was tests taking the real
/// machine-wide lock and failing each other across projects and worktrees for a reason in neither
/// branch. That seam is what makes this assertion necessary rather than obvious: the property that
/// matters is no longer visible at the call site, so it is asserted here instead - the DEFAULT each
/// owner ships with, compared against the other's, not merely against a string this file also spells.
///
/// This is the only project that can see both types; the setup engine cannot reference the launcher.
/// </summary>
public class BothUpdateOwnersTakeTheSameLockTests
{
    [Fact]
    public void TheTwoOwnersDefaultToTheSameMachineWideLock()
    {
        var launcherInstallingTheDirectorsUpdate = new DirectorUpdateOwner(new DirectorSupervisor()).SwapLockName;
        var directorInstallingTheLaunchersUpdate =
            new LauncherUpdateOwner(TestRoot(), directorStillHoldsItsInstance: () => true).SwapLockName;

        Assert.Equal(launcherInstallingTheDirectorsUpdate, directorInstallingTheLaunchersUpdate);
        Assert.Equal(BinarySwapLock.Name, launcherInstallingTheDirectorsUpdate);
    }

    [Fact]
    public void TheLockIsMachineWide_NotSessionScoped()
    {
        // A Director and the launcher supervising it need not share a logon session, and an unqualified
        // mutex name is session-scoped - so a lock that looked identical would still fail to exclude
        // exactly the pair it exists for.
        Assert.StartsWith(@"Global\", BinarySwapLock.Name, StringComparison.Ordinal);
    }

    private static string TestRoot()
        => Path.Combine(Path.GetTempPath(), "cc-swap-lock-" + Guid.NewGuid().ToString("N"));
}
