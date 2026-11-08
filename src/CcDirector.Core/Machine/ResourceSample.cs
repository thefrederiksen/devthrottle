namespace CcDirector.Core.Machine;

/// <summary>
/// One reading of the whole machine, taken by an <see cref="IResourceProbe"/> at one moment.
///
/// WHAT IS MEASURED, AND WHAT IS ONLY EXPLAINED. THIS IS THE ONE PLACE THE NARROWING IS EXPLAINED; every
/// other type in this folder points here rather than repeating it, because two copies of an explanation
/// drift apart exactly as two copies of code do.
///
/// The owner narrowed the Resource Monitor on 2026-11-06: the meter showed MEMORY AND THE PROCESSOR, and
/// the disk meter, per-volume free space and network throughput went out. ON 2026-11-07 HE BROUGHT FREE
/// SPACE BACK, after the C: drive on SOREN_NORTH ran out in an afternoon with nothing in DevThrottle
/// showing it coming: <see cref="DiskSpace"/> carries free and total space per fixed drive, and it is a
/// DISK bar beside CPU and MEM, a detail tile per drive, a figure in the five-minute upload and the
/// Cockpit. ONLY FREE SPACE CAME BACK - the disk ACTIVITY figures (busy, queue, throughput) and network
/// throughput stay out, and the disk activity survives only as <see cref="DiskEvidence"/>, which no meter
/// reads. <see cref="Swap"/> stays
/// because it has its own warning limits and is the late sign of a machine short of memory.
/// <see cref="Stall"/> and <see cref="DiskEvidence"/> stay because the SLOWNESS DIAGNOSIS still reads
/// the disk: on devlinux on 2026-10-15 memory and the processor were both healthy while programs were
/// stalled on a spinning disk 32% of the time, and two green bars on a dead machine is exactly what the
/// diagnosis exists to prevent. Mission section 5c-i rules what the diagnosis may read on each platform
/// and what it may then claim; <see cref="StallReading"/> carries that rule.
///
/// Each part carries its own <c>CouldRead</c> and reason, because they fail independently: a machine
/// whose stall figures are switched off in its kernel still has a readable processor, and the meter must
/// show the one it has and say "cannot read" for the one it does not. There is no sample-wide
/// "could read".
/// </summary>
/// <param name="TakenAtUtc">
/// STAMPS when the sample was taken, for a caller that wants to print it. NEVER used to age a cache or
/// to measure an interval: that is what the monotonic clock is for, and comparing two of these stamps is
/// the exact defect this interface was changed to remove - see <see cref="ResourceRate"/>.
/// </param>
/// <param name="Cpu">The processor, whole machine and busiest core.</param>
/// <param name="Memory">The machine's memory - the existing reading from issue #2818, reused unchanged.</param>
/// <param name="Swap">The swap file or page file.</param>
/// <param name="Stall">How much of the time programs were frozen waiting on each resource, the disk included.</param>
/// <param name="DiskEvidence">
/// Each physical disk's name, kind and timings - FOR THE SLOWNESS DIAGNOSIS ONLY. It is never a bar, a
/// tile, a Gateway column or a Cockpit column. It exists so the diagnosis can name the disk it blames.
/// </param>
/// <param name="Processes">Every process, with what it costs the machine.</param>
/// <param name="DiskSpace">
/// Free and total space on each fixed drive (brought back by the owner on 2026-11-07). The DISK bar, the
/// per-drive tiles and the uploaded drive figures all read this, and nothing reads
/// <see cref="DiskEvidence"/> for free space.
/// </param>
public sealed record ResourceSample(
    DateTime TakenAtUtc,
    CpuReading Cpu,
    MachineMemoryReading Memory,
    SwapReading Swap,
    StallReading Stall,
    DiskEvidenceSet DiskEvidence,
    ProcessSetReading Processes,
    DiskSpaceSet DiskSpace);
