namespace CcDirector.Core.Machine;

/// <summary>
/// One reading of the whole machine, taken by an <see cref="IResourceProbe"/> at one moment.
///
/// WHAT IS MEASURED, AND WHAT IS ONLY EXPLAINED. The owner narrowed the Resource Monitor on 2026-11-06:
/// the meter shows MEMORY AND THE PROCESSOR, and nothing else is shown anywhere he looks - not the
/// toolbar, not the detail tiles, not a Gateway row, not the Cockpit. So this sample carries no disk
/// meter, no per-volume free space and no network throughput; those parts were designed for the wider
/// scope and are gone. <see cref="Swap"/> stays because it has its own warning limits and is the late
/// sign of a machine short of memory. <see cref="Stall"/> and <see cref="DiskEvidence"/> stay because
/// the SLOWNESS DIAGNOSIS still reads the disk: on devlinux on 2026-10-15 memory and the processor were
/// both healthy while programs were stalled on a spinning disk 32% of the time, and two green bars on a
/// dead machine is exactly what the diagnosis exists to prevent.
///
/// Each part carries its own <c>CouldRead</c> and reason, because they fail independently: a machine
/// whose stall figures are switched off in its kernel still has a readable processor, and the meter must
/// show the one it has and say "cannot read" for the one it does not. There is no sample-wide
/// "could read".
/// </summary>
/// <param name="TakenAtUtc">When the sample was taken, so a cached one can be aged.</param>
/// <param name="Cpu">The processor, whole machine and busiest core.</param>
/// <param name="Memory">The machine's memory - the existing reading from issue #2818, reused unchanged.</param>
/// <param name="Swap">The swap file or page file.</param>
/// <param name="Stall">How much of the time programs were frozen waiting on each resource, the disk included.</param>
/// <param name="DiskEvidence">
/// Each physical disk's name, kind and timings - FOR THE SLOWNESS DIAGNOSIS ONLY. It is never a bar, a
/// tile, a Gateway column or a Cockpit column. It exists so the diagnosis can name the disk it blames.
/// </param>
/// <param name="Processes">Every process, with what it costs the machine.</param>
public sealed record ResourceSample(
    DateTime TakenAtUtc,
    CpuReading Cpu,
    MachineMemoryReading Memory,
    SwapReading Swap,
    StallReading Stall,
    DiskEvidenceSet DiskEvidence,
    ProcessSetReading Processes);
