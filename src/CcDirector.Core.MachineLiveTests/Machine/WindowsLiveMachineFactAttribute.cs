using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// A <see cref="LiveMachineFactAttribute"/> that also needs the machine to be WINDOWS. It skips, visibly
/// and with the reason, unless <c>CC_DIRECTOR_LIVE_MACHINE_TESTS=1</c> is set AND this is Windows - so a
/// live run on Linux or macOS reports these as skipped rather than failing them for reading the wrong
/// operating system, and never as passed.
/// </summary>
public sealed class WindowsLiveMachineFactAttribute : FactAttribute
{
    /// <param name="reads">What this test reads from the machine, in plain English, for the skip reason.</param>
    public WindowsLiveMachineFactAttribute(string reads)
    {
        Skip = SkipReason(reads, LiveMachineFactAttribute.Enabled, OperatingSystem.IsWindows(), Environment.ProcessorCount);
    }

    /// <summary>The reason to skip, or null to run. Not being asked for comes first, as in <see cref="LiveMachineFactAttribute.SkipReason"/>.</summary>
    public static string? SkipReason(string reads, bool enabled, bool isWindows, int logicalCores)
    {
        var notAsked = LiveMachineFactAttribute.SkipReason(reads, 0, enabled, logicalCores);
        if (notAsked is not null) return notAsked;
        return isWindows ? null : $"Reads the real Windows machine ({reads}); this machine is not Windows.";
    }
}
