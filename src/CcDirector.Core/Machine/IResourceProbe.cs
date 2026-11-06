namespace CcDirector.Core.Machine;

/// <summary>
/// Reads the whole machine - the processor, memory, swap, the stall readings, each disk's evidence for
/// the slowness diagnosis, and the processes - in one call (the Resource Monitor mission, issue #2811).
/// The one shape the meter, the warning banner, the detail screen, the Gateway summary and the slowness
/// diagnosis all read from, so none of them knows which operating system it is on.
///
/// It reads no disk meter, no per-volume free space and no network throughput: the owner narrowed the
/// mission on 2026-11-06 to memory and the processor, and the disk survives only as evidence the
/// diagnosis puts into a sentence.
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
/// <item>PER-PROCESS PROCESSOR IS A SHARE OF THE WHOLE MACHINE, 0 to 100. A process holding one core of
/// ten reads 10, not 100. Every platform must answer the same way or no per-session total means
/// anything: a figure that is per-core on one operating system and per-machine on another cannot be
/// summed, compared between machines, or placed against the toolbar's own processor bar. The shared
/// probe contract test proves this against the real machine on all three platforms.</item>
/// <item>EVERY INTERVAL AND EVERY RATE COMES OFF A MONOTONIC CLOCK, never off wall-clock time. Take a
/// <see cref="TimeProvider"/> and use <see cref="TimeProvider.GetTimestamp"/> with
/// <see cref="TimeProvider.GetElapsedTime(long, long)"/>; <see cref="ResourceRate"/> does the division
/// and knows the two ways it fails. Wall-clock time STAMPS a sample
/// (<see cref="ResourceSample.TakenAtUtc"/>) and never measures an interval, because time
/// synchronisation steps it and a step backwards across a five-second sample would turn every rate on
/// the machine negative or enormous.</item>
/// <item>A COUNTER THAT STEPS BACKWARDS YIELDS NULL, never zero - a reset, a wrap, or a reused process
/// id. <see cref="ResourceRate"/> enforces it; do not divide by hand.</item>
/// <item>A PROCESS THAT IS STILL RUNNING BUT WHOSE FIGURES THE PLATFORM REFUSES IS LISTED AND NAMED,
/// with those figures null. It is never dropped from the list and never written down as zero. On a Mac
/// an ordinary user is refused the figures for roughly three hundred of seven hundred processes, and
/// the busiest process on the machine is routinely one of them - dropping them hides the very thing the
/// slowness diagnosis exists to name.</item>
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
