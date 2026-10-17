using CcDirector.ControlApi;
using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Avalonia.Tests.Startup;

/// <summary>
/// The data-folder lock of <see cref="SingleInstanceGuard"/> (issue devthrottle_internal#2489). The defect:
/// the only lock was keyed on the program file, so a second program file - a worktree build - ran on the
/// live default instance home and cleared its sessions.json. Two program files on one folder must now share
/// one lock; two folders must not.
/// </summary>
public sealed class SingleInstanceGuardHomeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "guard-home-tests");
    private static readonly string DefaultHome = InstanceContext.InstanceHomeOf(Root, "default");

    [Fact]
    public void HomeMutexNameFor_TwoProgramFilesOnOneHome_ShareOneLock()
    {
        // The program file is not part of the home lock, by design: that is the whole fix.
        var installed = DirectorIdentitySlot.KeyFor("/home/x/.local/share/cc-director/app/cc-director", "default");
        var worktree = DirectorIdentitySlot.KeyFor("/home/x/repo.worktrees/wt03/bin/cc-director", "default");

        Assert.NotEqual(DirectorIdentitySlot.SlotFor(installed), DirectorIdentitySlot.SlotFor(worktree));
        Assert.Equal(SingleInstanceGuard.HomeMutexNameFor(DefaultHome), SingleInstanceGuard.HomeMutexNameFor(DefaultHome));
    }

    [Fact]
    public void HomeMutexNameFor_DifferentInstances_HaveDifferentLocks()
    {
        var linuxtest = InstanceContext.InstanceHomeOf(Root, "linuxtest");

        Assert.NotEqual(SingleInstanceGuard.HomeMutexNameFor(DefaultHome), SingleInstanceGuard.HomeMutexNameFor(linuxtest));
    }

    [Fact]
    public void HomeMutexNameFor_TrailingSeparatorAndRelativeSpelling_AreTheSameFolder()
    {
        var withSeparator = DefaultHome + Path.DirectorySeparatorChar;
        var roundabout = Path.Combine(DefaultHome, "..", "default");

        Assert.Equal(SingleInstanceGuard.HomeMutexNameFor(DefaultHome), SingleInstanceGuard.HomeMutexNameFor(withSeparator));
        Assert.Equal(SingleInstanceGuard.HomeMutexNameFor(DefaultHome), SingleInstanceGuard.HomeMutexNameFor(roundabout));
    }

    [Fact]
    public void HomeMutexNameFor_OnMacOSAndLinux_IsGlobalSoEverySessionSeesIt()
    {
        // A Local name is per login session there: a desktop start and an SSH start would each get the lock.
        Assert.StartsWith(@"Global\cc-director-home-", SingleInstanceGuard.HomeMutexNameFor(DefaultHome, isWindows: false));
    }

    [Fact]
    public void HomeMutexNameFor_OnWindows_IsLocal()
    {
        Assert.StartsWith(@"Local\cc-director-home-", SingleInstanceGuard.HomeMutexNameFor(DefaultHome, isWindows: true));
    }

    [Fact]
    public void HomeMutexNameFor_IsDistinctFromTheProgramLock()
    {
        Assert.DoesNotContain("cc-director-instance-", SingleInstanceGuard.HomeMutexNameFor(DefaultHome));
    }

    [Fact]
    public void HomeMutexNameFor_EmptyHome_Throws()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.HomeMutexNameFor(" "));
    }
}
