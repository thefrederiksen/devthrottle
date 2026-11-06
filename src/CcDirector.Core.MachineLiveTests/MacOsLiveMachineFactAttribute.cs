using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A test that reads the real macOS operating system: it needs BOTH the machine-reading opt-in
/// (<see cref="LiveMachineFactAttribute.Variable"/>) and a Mac to run on, and SKIPS - visibly, with a
/// stated reason - when either is missing.
///
/// WHY THIS EXISTS RATHER THAN PLAIN <see cref="LiveMachineFactAttribute"/>. That attribute gates on the
/// opt-in and on the core count, and deliberately says nothing about the platform, because the shared
/// contract it was written for runs on all three. These tests cannot: Mach, IOKit, <c>statfs</c> and
/// <c>libproc</c> exist only on macOS, so on a Linux or Windows runner with the opt-in set they would
/// RUN and FAIL rather than skip. A general platform gate on
/// <see cref="LiveMachineFactAttribute"/> is being added by the seat that owns it; when it lands this
/// attribute collapses into it and should be deleted. It composes that attribute's decision rather than
/// restating it, so there is one definition of "was the machine asked for".
///
/// THE PLATFORM REASON COMES FIRST, which is a deliberate difference from
/// <see cref="LiveMachineFactAttribute.SkipReason"/>, where not being asked for comes first. On a
/// machine that is not a Mac, the opt-in is irrelevant: telling that reader to set a variable would
/// promise them a run they can never have. The permanent reason is the useful one, so it wins.
///
/// WHY A SKIP AND NOT AN EARLY RETURN, which is the same reasoning as the attribute it builds on: an
/// <c>if (!OperatingSystem.IsMacOS()) return;</c> reports a PASS for a test that ran no assertion, so a
/// green count on a Linux build machine would silently include every macOS test doing nothing.
///
/// HOW TO RUN THESE: on a Mac, with <c>CC_DIRECTOR_LIVE_MACHINE_TESTS=1</c>.
/// </summary>
public sealed class MacOsLiveMachineFactAttribute : FactAttribute
{
    /// <param name="reads">What this test reads from the machine, in plain English, for the skip reason.</param>
    public MacOsLiveMachineFactAttribute(string reads) =>
        Skip = SkipReason(reads, LiveMachineFactAttribute.Enabled, OperatingSystem.IsMacOS());

    /// <summary>
    /// The reason this test should be skipped, or null when it should run. Pure: it reads neither the
    /// environment nor the operating system, so both branches can be exercised from a test on any
    /// platform - including the branch that only a Mac would otherwise reach.
    /// </summary>
    /// <param name="reads">What the test reads from the machine, in plain English.</param>
    /// <param name="enabled">Whether the caller asked for machine-reading tests.</param>
    /// <param name="onMacOs">Whether this machine is a Mac.</param>
    public static string? SkipReason(string reads, bool enabled, bool onMacOs)
    {
        if (!onMacOs)
            return $"Reads the real macOS operating system ({reads}), which exists only on a Mac. "
                   + $"Setting {LiveMachineFactAttribute.Variable} will not make it run here.";

        return LiveMachineFactAttribute.SkipReason(reads, minimumLogicalCores: 0, enabled, int.MaxValue);
    }
}
