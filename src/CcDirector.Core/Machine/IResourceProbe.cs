namespace CcDirector.Core.Machine;

/// <summary>
/// Reads the whole machine - processor, memory, swap, disks, volumes, network, stalls and processes -
/// in one call (the Resource Monitor mission, issue #2811). The one shape the meter, the warning
/// banner, the detail screen, the Gateway summary and the slowness diagnosis all read from, so none of
/// them knows which operating system it is on.
///
/// The platform implementations are <c>LinuxResourceProbe</c>, <c>WindowsResourceProbe</c> and
/// <c>MacOsResourceProbe</c>, each written separately. It is an interface, like
/// <see cref="IMachineMemoryProbe"/>, so everything above it can be tested against a sample the test
/// chooses, with no clock and no platform call.
///
/// THE RULES EVERY IMPLEMENTATION KEEPS:
/// <list type="bullet">
/// <item>A figure that cannot be read says so - <c>CouldRead = false</c> and a plain-English
/// <c>UnreadableReason</c> - never a zero and never a guess.</item>
/// <item>A figure the platform simply does not offer is <c>null</c>, which is not a failed read.</item>
/// <item>Percentages run from 0 to 100. Rates are per second, computed from two cumulative readings;
/// the first read after start-up has no interval, so its rates are unreadable with
/// <see cref="ResourceUnreadableReasons.FirstSample"/>.</item>
/// <item>No subprocess is started: this is read on a five-second timer, often on a machine that is
/// already struggling.</item>
/// </list>
/// </summary>
public interface IResourceProbe
{
    /// <summary>
    /// The machine now, or a cached sample up to one second old (the same reasoning as
    /// <see cref="MachineMemoryProbe.CacheFor"/>: several callers ask within the same moment). Never
    /// throws: whatever cannot be read comes back unreadable, with the reason, on its own part of the
    /// sample, so one broken reading never hides the others.
    /// </summary>
    ResourceSample Read();
}
