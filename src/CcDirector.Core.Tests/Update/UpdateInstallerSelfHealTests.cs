using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// Self-healing startup decisions for the auto-updater (issue #242): half-applied-swap
/// recovery and post-update health rollback. These cover the pure decision helpers; the
/// filesystem swap/restore and relaunch side effects live in UpdateInstaller/Program.cs.
/// </summary>
public class UpdateInstallerSelfHealTests
{
    // --- NeedsHalfSwapRecovery -------------------------------------------------

    [Fact]
    public void NeedsHalfSwapRecovery_InstallMissingWithBackup_True()
    {
        // The swap deleted/moved the install exe but never wrote the new one; a backup remains.
        Assert.True(UpdateInstaller.NeedsHalfSwapRecovery(
            installExists: false, installLength: 0, oldExists: true, oldLength: 12345));
    }

    [Fact]
    public void NeedsHalfSwapRecovery_InstallZeroLengthWithBackup_True()
    {
        // A copy started but produced a zero-length exe; the backup is intact.
        Assert.True(UpdateInstaller.NeedsHalfSwapRecovery(
            installExists: true, installLength: 0, oldExists: true, oldLength: 12345));
    }

    [Fact]
    public void NeedsHalfSwapRecovery_HealthyInstall_False()
    {
        // A normal install with a non-empty exe needs no recovery even if a backup lingers.
        Assert.False(UpdateInstaller.NeedsHalfSwapRecovery(
            installExists: true, installLength: 9999, oldExists: true, oldLength: 12345));
    }

    [Fact]
    public void NeedsHalfSwapRecovery_NoBackup_False()
    {
        // Without a usable backup there is nothing to recover from.
        Assert.False(UpdateInstaller.NeedsHalfSwapRecovery(
            installExists: false, installLength: 0, oldExists: false, oldLength: 0));
    }

    [Fact]
    public void NeedsHalfSwapRecovery_ZeroLengthBackup_False()
    {
        // A zero-length backup is itself broken; restoring from it would not help.
        Assert.False(UpdateInstaller.NeedsHalfSwapRecovery(
            installExists: false, installLength: 0, oldExists: true, oldLength: 0));
    }

    // --- NeedsHealthRollback ---------------------------------------------------

    [Fact]
    public void NeedsHealthRollback_PendingVersionNeverBecameRunning_True()
    {
        // A health check is pending for 0.6.11 but the running build is still 0.6.10, so the
        // new build failed to come up and hand control back -- roll back.
        Assert.True(UpdateInstaller.NeedsHealthRollback(
            pendingHealthVersion: "0.6.11", runningVersion: "0.6.10", oldExists: true, oldLength: 12345));
    }

    [Fact]
    public void NeedsHealthRollback_PendingVersionIsRunning_False()
    {
        // The pending version IS the running build -- it came up; the marker will be cleared
        // by MarkCurrentBuildHealthy, not rolled back.
        Assert.False(UpdateInstaller.NeedsHealthRollback(
            pendingHealthVersion: "0.6.11", runningVersion: "0.6.11", oldExists: true, oldLength: 12345));
    }

    [Fact]
    public void NeedsHealthRollback_NoPendingCheck_False()
    {
        Assert.False(UpdateInstaller.NeedsHealthRollback(
            pendingHealthVersion: null, runningVersion: "0.6.10", oldExists: true, oldLength: 12345));
    }

    [Fact]
    public void NeedsHealthRollback_NoBackupToRollBackTo_False()
    {
        // Without a non-empty backup there is no working build to roll back to.
        Assert.False(UpdateInstaller.NeedsHealthRollback(
            pendingHealthVersion: "0.6.11", runningVersion: "0.6.10", oldExists: false, oldLength: 0));
    }

    // --- UpdaterState round-trip ----------------------------------------------

    [Fact]
    public void UpdaterState_RoundTripsSelfHealFields()
    {
        var original = new UpdaterState
        {
            PendingHealthCheckVersion = "0.6.11",
            PinnedBadVersion = "0.6.9",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<UpdaterState>(json);

        Assert.NotNull(restored);
        Assert.Equal("0.6.11", restored.PendingHealthCheckVersion);
        Assert.Equal("0.6.9", restored.PinnedBadVersion);
    }
}
