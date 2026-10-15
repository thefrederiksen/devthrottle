using System.Runtime.InteropServices;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE macOS PROBE MUST BE HARMLESS ON THE PLATFORMS IT CANNOT READ.
///
/// These tests run EVERYWHERE on purpose - they are not gated to macOS - because the failure they
/// exist for only happens off it. The probe's whole contract is that it never throws and that a
/// resource it cannot read comes back with a stated reason; a Windows or Linux build machine is where
/// a breach of that gets noticed, and only if something actually looks.
///
/// The regression that prompted them: the Mach timebase was read in a FIELD INITIALISER, so touching
/// the class called a system call that does not exist off macOS. Because <see cref="MacOsResourceProbe.Shared"/>
/// is static, that threw during class initialisation - before the probe's own platform check could
/// answer "this is the macOS probe and the machine is not a Mac". Nothing on a Mac could ever have
/// caught it.
/// </summary>
public class MacOsResourceProbeSafetyTests
{
    [Fact]
    [Trait("Category", "Resources")]
    public void Constructing_OnAnyPlatform_DoesNotThrow()
    {
        var probe = new MacOsResourceProbe();

        Assert.NotNull(probe);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TheSharedProbe_OnAnyPlatform_CanBeTouchedWithoutThrowing()
    {
        // Reading the static field is what runs class initialisation, which is where the original
        // defect threw. A TypeInitializationException here takes down whatever first touched the
        // class, which in the Director is the sampler on start-up.
        Assert.NotNull(MacOsResourceProbe.Shared);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void Read_OnAnyPlatform_ReturnsASampleRatherThanThrowing()
    {
        var sample = new MacOsResourceProbe().Read();

        Assert.NotNull(sample);
        Assert.NotEqual(default, sample.TakenAtUtc);
        Assert.NotEmpty(sample.Volumes);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void Read_OnAPlatformThisProbeCannotRead_SaysSoForEveryResourceAndNeverReportsZero()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // On a Mac this probe reads the machine, and that is covered by the live tests. The
            // assertion below is about the other platforms, so here there is nothing to check beyond
            // the sample existing - which the test above already does. Asserted rather than skipped
            // so the test is not silently doing nothing on the platform it runs on most.
            Assert.True(new MacOsResourceProbe().Read().Memory.CouldRead);
            return;
        }

        var sample = new MacOsResourceProbe().Read();

        // EVERY READING UNREADABLE, EACH WITH A REASON. A zero anywhere here would tell the Director
        // that a Windows machine had an idle processor, no swap in use and a disk doing no work.
        Assert.False(sample.Cpu.CouldRead);
        Assert.False(sample.Memory.CouldRead);
        Assert.False(sample.Swap.CouldRead);
        Assert.False(sample.Disks.CouldRead);
        Assert.False(sample.Network.CouldRead);
        Assert.False(sample.Stall.CouldRead);
        Assert.False(sample.Processes.CouldRead);
        Assert.All(sample.Volumes, volume => Assert.False(volume.CouldRead));

        foreach (var reason in new[]
                 {
                     sample.Cpu.UnreadableReason, sample.Memory.UnreadableReason,
                     sample.Swap.UnreadableReason, sample.Disks.UnreadableReason,
                     sample.Network.UnreadableReason, sample.Stall.UnreadableReason,
                     sample.Processes.UnreadableReason,
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        // And the reason names the actual problem, so a log line from it is worth reading.
        Assert.Contains("macOS probe", sample.Cpu.UnreadableReason, StringComparison.Ordinal);

        // Nothing was invented behind the unreadable flags.
        Assert.Empty(sample.Disks.Disks);
        Assert.Empty(sample.Processes.Processes);
        Assert.Equal(0, sample.Cpu.LogicalCoreCount);
        Assert.Equal(StallSource.None, sample.Stall.Source);
    }
}
