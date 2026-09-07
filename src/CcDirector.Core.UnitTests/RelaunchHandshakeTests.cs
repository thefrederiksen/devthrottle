using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.UnitTests;

/// <summary>
/// The relaunch handshake, which is what lets the single-instance guard be taken BEFORE any update
/// action.
///
/// WHY THIS MATTERS MORE THAN IT LOOKS. The Director used to claim its single-instance mutex ninety
/// lines into startup, after five update actions. So the guard prevented a duplicate DIRECTOR and did
/// NOT prevent duplicate UPDATER WORK - a second copy started against a live one ran half-swap
/// recovery, rollback, cleanup and staged-update application before finding out it was not wanted.
/// That is how an update could proceed over a Director nobody had accounted for.
///
/// Taking the guard first closes it, and could not simply be done: ONE of the two relaunch paths
/// handed over with no handshake at all. TryApplyStagedUpdateAtStartup has always passed the parent
/// process id (LaunchRelauncher) and waited on it (ApplyUpdate). The ROLLBACK relaunch called
/// Process.Start and returned, so the restored build started while its parent was still exiting - and
/// with an early guard it would have found the mutex held by that dying parent and refused to start,
/// LEAVING THE MACHINE WITH NO DIRECTOR AT ALL. That is worse than both problems being solved.
///
/// So these tests pin the handshake itself: the argument is emitted, it is emitted where the target
/// platform will actually deliver it, and it is read back. The correct pattern was already in the same
/// file, a hundred lines from the path that lacked it.
/// </summary>
public sealed class RelaunchHandshakeTests
{
    private const string Target = @"C:\install\cc-director.exe";

    private static string[] Args(System.Diagnostics.ProcessStartInfo psi) => psi.ArgumentList.ToArray();

    /// <summary>The handshake reaches the relaunched build.</summary>
    [Fact]
    public void A_relaunch_asked_to_wait_carries_the_parent_process_id()
    {
        var psi = UpdateInstaller.BuildRelaunchStartInfo(Target, "default", waitForProcessId: 4242);

        var args = Args(psi);
        var i = Array.IndexOf(args, "--wait-for-exit");
        Assert.True(i >= 0, "the relaunch must carry --wait-for-exit");
        Assert.Equal("4242", args[i + 1]);
    }

    /// <summary>
    /// THE CONTROL. A relaunch that was NOT asked to wait must not carry the argument - otherwise the
    /// test above would pass against an implementation that always waits, which would make every
    /// ordinary start pause for a process that is not there.
    /// </summary>
    [Fact]
    public void A_relaunch_not_asked_to_wait_carries_no_handshake()
    {
        var psi = UpdateInstaller.BuildRelaunchStartInfo(Target, "default");

        Assert.DoesNotContain("--wait-for-exit", Args(psi));
    }

    /// <summary>A process id of zero or less is not a process, and must not be emitted as one.</summary>
    [Fact]
    public void A_relaunch_carries_no_handshake_when_there_is_no_instance_either()
    {
        var psi = UpdateInstaller.BuildRelaunchStartInfo(Target, instanceSlug: null);

        Assert.DoesNotContain("--wait-for-exit", Args(psi));
        Assert.DoesNotContain("--instance", Args(psi));
    }

    /// <summary>
    /// ON MACOS EVERY APPLICATION ARGUMENT TRAVELS BEHIND ONE "--args", AND THIS IS WHERE THE FIRST
    /// DRAFT WAS WRONG. It appended the handshake after the instance block, so a relaunch carrying a
    /// handshake and NO instance slug emitted "--wait-for-exit 4242" with no "--args" in front of it -
    /// which hands them to /usr/bin/open rather than to the Director, and open does not know them.
    /// The Director would then start with no handshake at all and race its own parent, which is
    /// precisely the failure the handshake exists to prevent, on the platform nobody was testing.
    ///
    /// Asserted as ORDER rather than presence: the arguments being there is not the property, being
    /// there BEHIND the separator is.
    /// </summary>
    [Fact]
    public void Every_application_argument_sits_behind_a_single_args_separator()
    {
        if (!OperatingSystem.IsMacOS()) return;

        foreach (var slug in new string?[] { "default", null })
        {
            var args = Args(UpdateInstaller.BuildRelaunchStartInfo(Target, slug, waitForProcessId: 4242));

            var separator = Array.IndexOf(args, "--args");
            Assert.True(separator >= 0, "application arguments need a --args separator on macOS");
            Assert.Equal(1, args.Count(a => a == "--args"));

            var wait = Array.IndexOf(args, "--wait-for-exit");
            Assert.True(wait > separator, "the handshake must sit BEHIND the separator, not in front of it");
        }
    }

    /// <summary>On every other platform the arguments go straight to the executable, with no separator
    /// at all - so a separator appearing there would be passed to the Director as a real argument.</summary>
    [Fact]
    public void Off_macOS_the_arguments_go_straight_to_the_executable()
    {
        if (OperatingSystem.IsMacOS()) return;

        var args = Args(UpdateInstaller.BuildRelaunchStartInfo(Target, "work", waitForProcessId: 99));

        Assert.DoesNotContain("--args", args);
        Assert.Equal(new[] { "--instance", "work", "--wait-for-exit", "99" }, args);
    }

    /// <summary>The relaunch still scrubs CC_DIRECTOR_ROOT. Asserted here because the handshake work
    /// edited this method, and a change that quietly dropped the scrub would make an updated Director
    /// nest a new empty data tree inside the old one - which reads to a person as "the update wiped
    /// my Director".</summary>
    [Fact]
    public void The_relaunch_still_refuses_to_pass_on_an_inherited_root()
    {
        var psi = UpdateInstaller.BuildRelaunchStartInfo(Target, "default", waitForProcessId: 7);

        Assert.False(psi.Environment.ContainsKey("CC_DIRECTOR_ROOT")
                     && !string.IsNullOrEmpty(psi.Environment["CC_DIRECTOR_ROOT"]));
        Assert.False(psi.UseShellExecute);
    }
}
